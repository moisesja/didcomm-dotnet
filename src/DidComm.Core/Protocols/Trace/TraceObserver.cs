using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Messages;
using DidComm.Transports;

namespace DidComm.Protocols.Trace;

/// <summary>
/// Pure-function decision surface for Trace 2.0 (FR-PROTO-11 / FR-PROTO-11a). Given an
/// inbound message + the active <see cref="TraceOptions"/>, decides whether the runtime
/// should POST a <c>trace-report</c> to the URI advertised in the message's <c>trace</c>
/// header — and if so, returns the URI to use.
/// </summary>
/// <remarks>
/// <para>
/// Phase 6.2c ships the decision logic only; actually issuing the HTTP POST is left to a
/// future integration (operators that opt-in today can wire their own transport against
/// <see cref="ShouldReport"/>). This keeps the FR-PROTO-11a "off by default" guarantee
/// crisp: nothing is sent without explicit operator setup.
/// </para>
/// </remarks>
public static class TraceObserver
{
    /// <summary>
    /// Decide whether the runtime SHOULD emit a trace-report for <paramref name="inbound"/>.
    /// Returns <c>false</c> + <paramref name="reportUri"/> = <c>null</c> when:
    /// (a) <see cref="TraceOptions.Enabled"/> is <c>false</c>;
    /// (b) the message has no <c>trace</c> header;
    /// (c) the header is malformed or missing <c>report_uri</c>;
    /// (d) the <c>report_uri</c> uses a scheme other than <c>http</c>/<c>https</c>;
    /// (e) the <c>report_uri</c> targets a loopback / private IP literal (defense-in-depth
    /// against typo'd allowlist entries — reuses <see cref="OutboundEndpointGuard.IsPrivateOrReserved"/>);
    /// (f) the <c>report_uri</c> is not on <see cref="TraceOptions.AllowedReportingUris"/> (comparison
    /// is done on the canonical <see cref="Uri.AbsoluteUri"/> form on both sides so trailing-slash,
    /// default-port, and percent-encoding normalisations don't drop legitimate matches).
    /// </summary>
    /// <param name="inbound">The message to consider. May be any type — Trace observes ALL messages.</param>
    /// <param name="options">The active TraceOptions.</param>
    /// <param name="reportUri">On <c>true</c>, the validated absolute report URI to POST to.</param>
    public static bool ShouldReport(Message inbound, TraceOptions options, out Uri? reportUri)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(options);
        reportUri = null;

        // FR-PROTO-11a — default off.
        if (!options.Enabled) return false;

        // Read the trace header. It lives under Message.AdditionalHeaders because it's not one
        // of the strongly-typed members; the value is an object with a `report_uri` member.
        if (inbound.AdditionalHeaders is null) return false;
        if (!inbound.AdditionalHeaders.TryGetValue(Trace.HeaderName, out var headerElement))
            return false;

        string? rawUri = TryReadStringMember(headerElement, Trace.ReportUriField);
        if (string.IsNullOrEmpty(rawUri)) return false;

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var parsed)) return false;

        // Scheme guard — `file://`, `javascript:`, `data:`, `gopher:` etc. all parse as absolute
        // but only http/https are valid trace-report transports per FR-PROTO-11.
        if (parsed.Scheme is not ("http" or "https")) return false;

        // SSRF defense-in-depth: reject IP-literal hosts that classify as loopback / private /
        // link-local / metadata. DNS hostnames are deferred to the transport-level guard at
        // connect-time (OutboundEndpointGuard.ConnectAsync) — checking them here would require
        // synchronous DNS resolution per inbound message.
        if ((parsed.HostNameType == UriHostNameType.IPv4 || parsed.HostNameType == UriHostNameType.IPv6)
            && IPAddress.TryParse(parsed.Host, out var hostIp)
            && OutboundEndpointGuard.IsPrivateOrReserved(hostIp))
        {
            return false;
        }

        // Allowlist gate — normalise both sides via Uri.AbsoluteUri so trailing slashes,
        // default-port stripping (`:443`), percent-encoding, and host-case normalisation don't
        // silently drop a legitimate operator-configured match.
        if (!IsAllowlisted(options, parsed)) return false;

        reportUri = parsed;
        return true;
    }

    private static bool IsAllowlisted(TraceOptions options, Uri parsed)
    {
        var target = parsed.AbsoluteUri;
        foreach (var allowed in options.AllowedReportingUris)
        {
            if (Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri)
                && string.Equals(target, allowedUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? TryReadStringMember(System.Text.Json.JsonElement element, string memberName)
    {
        // Headers come in as JsonElement (System.Text.Json's [JsonExtensionData] type).
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(memberName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    /// <summary>
    /// Build a trace-report message body. Useful for consumers wiring up the POST themselves;
    /// the body includes the trace_id (when present in the inbound header) and a short summary
    /// of the inbound message that triggered the report.
    /// </summary>
    /// <param name="inbound">The message that carried the trace header.</param>
    /// <param name="options">The active TraceOptions — controls whether routing details are included.</param>
    public static JsonObject BuildReportBody(Message inbound, TraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(options);
        var body = new JsonObject
        {
            ["observed_message_id"] = inbound.Id,
            ["observed_message_type"] = inbound.Type,
        };
        if (inbound.Thid is not null) body["observed_thid"] = inbound.Thid;
        if (inbound.AdditionalHeaders is not null
            && inbound.AdditionalHeaders.TryGetValue(Trace.HeaderName, out var headerElement)
            && TryReadStringMember(headerElement, Trace.TraceIdField) is { } traceId)
        {
            body["trace_id"] = traceId;
        }
        // IncludeRoutingDetails is honored by the future integration layer that builds the
        // final body; the placeholder here keeps the option observable for now.
        body["routing_detail_opt_in"] = options.IncludeRoutingDetails;
        return body;
    }
}
