using DidComm.Messages;
using DidComm.Protocols;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Registry;

public sealed class ProtocolHandlerRegistryTests
{
    private sealed class StaticHandler(string protocolUri) : IProtocolHandler
    {
        public string ProtocolUri { get; } = protocolUri;
        public Task<Message?> HandleAsync(Message message, ProtocolContext context, CancellationToken ct)
            => Task.FromResult<Message?>(null);
    }

    [Fact]
    public void Resolves_exact_PIURI_match()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/trust-ping/2.0"));
        reg.TryResolve("https://didcomm.org/trust-ping/2.0/ping", out var handler).Should().BeTrue();
        handler!.ProtocolUri.Should().Be("https://didcomm.org/trust-ping/2.0");
    }

    [Fact]
    public void Ignores_punctuation_and_case_on_protocol_name()
    {
        // FR-PROTO-01: "match protocol+message ignoring case and punctuation".
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/trustping/2.0"));
        reg.TryResolve("https://didcomm.org/Trust_Ping/2.0/ping", out var handler).Should().BeTrue();
        handler!.ProtocolUri.Should().Be("https://didcomm.org/trustping/2.0");
    }

    [Fact]
    public void Older_minor_serves_newer_minor_inbound()
    {
        // FR-PROTO-02: same major + differing minor interop at the OLDER minor. A 2.0 handler
        // is the only one registered, so a 2.1 inbound resolves to it.
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/x/2.0"));
        reg.TryResolve("https://didcomm.org/x/2.1/msg", out var handler).Should().BeTrue();
        handler!.ProtocolUri.Should().Be("https://didcomm.org/x/2.0");
    }

    [Fact]
    public void Newer_registered_minor_wins_when_inbound_is_at_least_that_new()
    {
        // Both 2.0 and 2.1 are registered; a 2.1 inbound prefers 2.1; a 2.0 inbound still
        // gets the 2.0 (which is the only one ≤ the inbound).
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/x/2.0"));
        reg.Register(new StaticHandler("https://didcomm.org/x/2.1"));

        reg.TryResolve("https://didcomm.org/x/2.1/msg", out var h21).Should().BeTrue();
        h21!.ProtocolUri.Should().Be("https://didcomm.org/x/2.1");

        reg.TryResolve("https://didcomm.org/x/2.0/msg", out var h20).Should().BeTrue();
        h20!.ProtocolUri.Should().Be("https://didcomm.org/x/2.0");
    }

    [Fact]
    public void Major_version_mismatch_does_not_resolve()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/x/1.0"));
        reg.TryResolve("https://didcomm.org/x/2.0/msg", out _).Should().BeFalse();
    }

    [Fact]
    public void Unknown_PIURI_returns_false_without_throw()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.TryResolve("https://didcomm.org/unknown/1.0/msg", out var handler).Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void Null_or_malformed_message_type_returns_false()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/x/1.0"));
        reg.TryResolve(null, out _).Should().BeFalse();
        reg.TryResolve("", out _).Should().BeFalse();
        reg.TryResolve("not-a-uri", out _).Should().BeFalse();
    }

    [Fact]
    public void Double_slash_type_that_parses_as_MTURI_but_not_PIURI_returns_false_without_throwing()
    {
        // Parser differential: the MTURI docUri group (`.+?`) tolerates a trailing '/', so
        // "https://didcomm.org//x/1.0/m" parses as an MTURI with docUri "https://didcomm.org/";
        // but the stricter PIURI group (`.+?[^/]`) rejects the derived PIURI. TryResolve MUST
        // fail closed to "no handler", not throw ProtocolException on the dispatch path — a
        // remote peer sets Message.Type, so a throw here would be remotely triggerable.
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/x/1.0"));

        Action act = () => reg.TryResolve("https://didcomm.org//x/1.0/m", out _);

        act.Should().NotThrow();
        reg.TryResolve("https://didcomm.org//x/1.0/m", out var handler).Should().BeFalse();
        handler.Should().BeNull();
    }

    [Fact]
    public void Re_registering_same_PIURI_replaces_handler()
    {
        var reg = new ProtocolHandlerRegistry();
        var first = new StaticHandler("https://didcomm.org/x/1.0");
        var second = new StaticHandler("https://didcomm.org/x/1.0");
        reg.Register(first);
        reg.Register(second);
        reg.TryResolve("https://didcomm.org/x/1.0/msg", out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(second);
    }

    [Fact]
    public void Register_rejects_malformed_PIURI()
    {
        var reg = new ProtocolHandlerRegistry();
        Action act = () => reg.Register(new StaticHandler("not-a-piuri"));
        act.Should().Throw<ArgumentException>().WithMessage("*FR-PROTO-01*");
    }

    [Fact]
    public void Register_rejects_PIURI_with_trailing_slash_doc_uri()
    {
        // Tightened FR-PROTO-01 parser: a docUri ending in '/' cannot smuggle an empty path
        // segment ("https://didcomm.org//empty/1.0") past the regex.
        var reg = new ProtocolHandlerRegistry();
        Action act = () => reg.Register(new StaticHandler("https://didcomm.org//empty/1.0"));
        act.Should().Throw<ArgumentException>().WithMessage("*FR-PROTO-01*");
    }

    [Fact]
    public void All_returns_every_registered_handler()
    {
        var reg = new ProtocolHandlerRegistry();
        reg.Register(new StaticHandler("https://didcomm.org/a/1.0"));
        reg.Register(new StaticHandler("https://didcomm.org/b/1.0"));
        reg.All.Should().HaveCount(2);
    }
}
