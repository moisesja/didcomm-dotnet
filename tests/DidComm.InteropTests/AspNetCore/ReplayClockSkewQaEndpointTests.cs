using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #105 QA pass, kept as regressions: the replay rule's own clock skew at the server side — through <c>AddDidComm</c>'s
/// guard and the endpoints' per-application fallback guard, on the HTTP endpoint, the WebSocket
/// endpoint and the session-aware callback endpoint. A refused message gets 202 with an empty body,
/// reaches no handler, opens no session. <c>DidCommOptions.ExpiresClockSkew</c> (any value) never
/// changes admission. All on the harness's injected clock.
/// </summary>
public sealed class ReplayClockSkewQaEndpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    // ---- DI: ExpiresClockSkew never reaches the guard ---------------------------------------------

    public static TheoryData<TimeSpan> ExpirySkews() => new()
    {
        TimeSpan.MinValue,
        TimeSpan.FromHours(-1),
        TimeSpan.FromSeconds(-1),
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(59),
        TimeSpan.FromSeconds(61),
        TimeSpan.FromHours(1),
        TimeSpan.FromDays(10_000),
        TimeSpan.MaxValue,
    };

    [Theory]
    [MemberData(nameof(ExpirySkews))]
    public void Through_AddDidComm_ExpiresClockSkew_never_changes_an_answer_of_the_guard(TimeSpan expiresClockSkew)
    {
        var now = Start.AddMilliseconds(640);
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.Configure(o =>
            {
                o.Clock = () => now;
                o.ExpiresClockSkew = expiresClockSkew;
            });
        });
        using var sp = services.BuildServiceProvider();
        var guard = sp.GetRequiredService<IInboundReplayGuard>();
        var reference = new InMemoryReplayGuard(new ReplayProtectionOptions());

        for (var offset = -305; offset <= 65; offset++)
        {
            var created = Start.ToUnixTimeSeconds() + offset;
            guard.TryAdmit("did:example:a", "m" + offset, created, now)
                .Should().Be(reference.TryAdmit("did:example:a", "m" + offset, created, now), "offset {0}", offset);
        }
        // Retention: the entry stamped at the bound is held exactly Window + 2 × ClockSkew, whatever ExpiresClockSkew says.
        foreach (var t in new[] { 420, 421 })
        {
            var later = Start.AddSeconds(t);
            guard.TryAdmit("did:example:noise", "n" + t, later.ToUnixTimeSeconds(), later).Should().Be(ReplayAdmission.Admitted);
            reference.TryAdmit("did:example:noise", "n" + t, later.ToUnixTimeSeconds(), later);
            ((InMemoryReplayGuard)guard).Count.Should().Be(reference.Count, "at +{0} s", t);
        }
    }

    // ---- HTTP: the future bound at several skews, sub-second clocks -------------------------------

    [Theory]
    [InlineData(-1, 0)]     // default options (60 s)
    [InlineData(-1, 999)]
    [InlineData(0, 0)]
    [InlineData(0, 999)]
    [InlineData(1, 500)]
    [InlineData(59, 0)]
    [InlineData(61, 999)]
    public async Task Http_admits_up_to_now_plus_ClockSkew_and_refuses_one_second_later(int skewSeconds, int startMilliseconds)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.Start = Start.AddMilliseconds(startMilliseconds);
            if (skewSeconds >= 0)
                o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromSeconds(skewSeconds);
        });
        var skew = skewSeconds < 0 ? 60 : skewSeconds;
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var nowS = host.Clock.Now.ToUnixTimeSeconds();

        var expectedInvocations = 0;
        foreach (var ahead in new[] { skew - 1, skew, skew + 1, skew + 2 }.Where(a => a >= 0))
        {
            var inbound = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + ahead).Build();
            var response = await host.PostAsync(await host.AuthcryptAsync(host.Alice, inbound));
            if (ahead <= skew)
            {
                expectedInvocations++;
                response.Status.Should().Be(HttpStatusCode.OK, "{0} s ahead at ClockSkew {1}", ahead, skew);
                (await host.Alice.Client.UnpackAsync(response.BodyText)).Message.Thid.Should().Be(inbound.Id);
                host.DropLines(inbound.Id).Should().BeEmpty();
            }
            else
            {
                AssertRefused(response, "{0} s ahead at ClockSkew {1}", ahead, skew);
                host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
            }
            host.Recorder.Invocations.Should().Be(expectedInvocations, "only admitted requests reach the handler");
        }
        host.Registry.Find(host.Alice.Did).Should().BeEmpty();
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- HTTP + fallback guard: ExpiresClockSkew of any value is ignored ----------------------------

    public static TheoryData<long, bool> ExpirySkewsTimesGuard()
    {
        var data = new TheoryData<long, bool>();
        foreach (var ticks in new[] { TimeSpan.MinValue.Ticks, TimeSpan.FromHours(-1).Ticks, -1L, 0L, TimeSpan.FromSeconds(59).Ticks, TimeSpan.FromHours(1).Ticks, TimeSpan.MaxValue.Ticks })
            foreach (var fallback in new[] { false, true })
                data.Add(ticks, fallback);
        return data;
    }

    [Theory]
    [MemberData(nameof(ExpirySkewsTimesGuard))]
    public async Task ExpiresClockSkew_of_any_value_does_not_change_admission_at_the_HTTP_endpoint(long expiresClockSkewTicks, bool fallbackGuard)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.ConfigureDidComm = b => b.Configure(c => c.ExpiresClockSkew = TimeSpan.FromTicks(expiresClockSkewTicks));
            if (fallbackGuard)
            {
                o.PostConfigureServices = s =>
                {
                    s.RemoveAll<IInboundReplayGuard>();
                    s.RemoveAll<IReturnRouteSessionRegistry>();
                };
            }
        });
        if (fallbackGuard)
            host.App.Services.GetService<IInboundReplayGuard>().Should().BeNull("the scenario is a host without AddDidComm's guard");
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var nowS = host.Clock.Now.ToUnixTimeSeconds();

        var cases = new (int Offset, ReplayAdmission? Refusal)[]
        {
            (-301, ReplayAdmission.TooOld),
            (-300, null),
            (0, null),
            (1, null),
            (59, null),
            (60, null),
            (61, ReplayAdmission.FutureDated),
            (3600, ReplayAdmission.FutureDated),
        };
        var admitted = 0;
        foreach (var (offset, refusal) in cases)
        {
            var inbound = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + offset).Build();
            var packed = await host.AuthcryptAsync(host.Alice, inbound);
            var response = await host.PostAsync(packed);
            if (refusal is null)
            {
                admitted++;
                response.Status.Should().Be(HttpStatusCode.OK, "offset {0}", offset);
                // And the replay of it, at once, is a Duplicate.
                AssertRefused(await host.PostAsync(packed), "the replay at offset {0}", offset);
                host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
            }
            else
            {
                AssertRefused(response, "offset {0}", offset);
                host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(refusal.ToString());
            }
            host.Recorder.Invocations.Should().Be(admitted);
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- ClockSkew = 0 differential against main (main: ExpiresClockSkew = 0, its default) ----------

    /// <summary>
    /// The answers <c>main</c> (f336823) gave with its default <c>ExpiresClockSkew = 0</c>, recorded by
    /// the #105 QA pass with the same offsets in the same order: each offset, then a replay of every
    /// admitted one.
    /// </summary>
    internal static readonly string[] MainStrictAnswers =
    {
        "-301:TooOld", "-300:OK", "-300:replay:Duplicate", "0:OK", "0:replay:Duplicate",
        "1:FutureDated", "2:FutureDated", "59:FutureDated", "60:FutureDated", "61:FutureDated",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockSkew_zero_answers_exactly_as_main_at_its_default(bool fallbackGuard)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.Start = Start.AddMilliseconds(999);
            o.ConfigureReplay = r => r.ClockSkew = TimeSpan.Zero;
            if (fallbackGuard)
                o.PostConfigureServices = s => s.RemoveAll<IInboundReplayGuard>();
        });
        (await StrictAnswersAsync(host)).Should().Equal(MainStrictAnswers);
    }

    internal static async Task<List<string>> StrictAnswersAsync(ReturnRouteTestHost host)
    {
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var nowS = host.Clock.Now.ToUnixTimeSeconds();
        var answers = new List<string>();
        foreach (var offset in new[] { -301, -300, 0, 1, 2, 59, 60, 61 })
        {
            var inbound = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + offset).Build();
            var packed = await host.AuthcryptAsync(host.Alice, inbound);
            var response = await host.PostAsync(packed);
            if (response.Status == HttpStatusCode.OK)
            {
                answers.Add($"{offset}:OK");
                (await host.PostAsync(packed)).Status.Should().Be(HttpStatusCode.Accepted);
                answers.Add($"{offset}:replay:{Outcome(host.DropLines(inbound.Id).Single())}");
            }
            else
            {
                answers.Add($"{offset}:{Outcome(host.DropLines(inbound.Id).Single())}");
            }
        }
        return answers;
    }

    // ---- HTTP: second-by-second sweep through retention, then the clock back -----------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_sweep_a_future_dated_request_is_never_admitted_again(bool fallbackGuard)
    {
        const int Window = 5;
        const int Skew = 3;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.Start = Start.AddMilliseconds(250);
            o.ConfigureReplay = r =>
            {
                r.Window = TimeSpan.FromSeconds(Window);
                r.ClockSkew = TimeSpan.FromSeconds(Skew);
            };
            if (fallbackGuard)
                o.PostConfigureServices = s => s.RemoveAll<IInboundReplayGuard>();
        });
        // Every admitted request gets a 200 reply, so a 202 with an empty body is a refusal.
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var t0 = host.Clock.Now.ToUnixTimeSeconds();
        var created = t0 + Skew;
        var inbound = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(created).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);
        (await host.PostAsync(packed)).Status.Should().Be(HttpStatusCode.OK);
        host.Recorder.Invocations.Should().Be(1);

        var expected = new List<string>();
        var noise = 0;
        async Task ReplayAt(long second, bool freshAdmitted = true)
        {
            host.Clock.Advance(DateTimeOffset.FromUnixTimeSeconds(second).AddMilliseconds(250) - host.Clock.Now);
            // Carol's traffic at this instant drives the purge. After a step back of more than
            // Window + ClockSkew from the latest purge instant, a fresh request stamped at the earlier
            // instant is refused too (TooOld): availability, never integrity.
            var other = host.Stamped(host.RecordingMessage(host.Carol).WithReturnRoute(ReturnRouteMode.All)).Build();
            (await host.PostAsync(await host.AuthcryptAsync(host.Carol, other))).Status.Should().Be(freshAdmitted ? HttpStatusCode.OK : HttpStatusCode.Accepted);
            if (freshAdmitted)
                noise++;
            else
                host.DropLines(other.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.TooOld));
            AssertRefused(await host.PostAsync(packed), "the replay at {0}s from created", second - created);
            expected.Add(second > created + Window ? nameof(ReplayAdmission.TooOld)
                : second < created - Skew ? nameof(ReplayAdmission.FutureDated)
                : nameof(ReplayAdmission.Duplicate));
        }

        // Forward to the last second the entry is held; back (while held) to created — a step of
        // exactly Window + ClockSkew — and further, to before admission; then forward past retention.
        for (var s = t0; s <= created + Window + Skew; s++)
            await ReplayAt(s);
        await ReplayAt(created);
        await ReplayAt(t0 - 1, freshAdmitted: false);
        await ReplayAt(created + Window + Skew + 1);
        await ReplayAt(created + Window + Skew + 2);

        host.Recorder.Invocations.Should().Be(1 + noise, "the original and Carol's requests only");
        host.DropLines(inbound.Id).Select(Outcome).Should().Equal(expected);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]   // the defaults: Window 300 s, ClockSkew 60 s → a 61 s step back
    [InlineData(0)]    // ClockSkew 0 (main's default behaviour) → a 1 s step back
    public async Task Http_a_clock_step_back_of_ClockSkew_plus_one_after_a_purge_refuses_the_captured_request(int skewSeconds)
    {
        // Was a defect probe (also on main): ordinary forward time and other traffic purge the entry
        // at created + Window + ClockSkew + 1; the host clock is then stepped back ClockSkew + 1
        // seconds (e.g. an NTP correction). The captured request used to be handled a second time; it
        // is now refused as TooOld (its entry may have been forgotten).
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            if (skewSeconds >= 0)
                o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromSeconds(skewSeconds);
        });
        var skew = skewSeconds < 0 ? 60 : skewSeconds;
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);
        (await host.PostAsync(packed)).Status.Should().Be(HttpStatusCode.OK);

        host.Clock.Advance(TimeSpan.FromSeconds(300 + skew + 1));
        var other = host.Stamped(host.RecordingMessage(host.Carol).WithReturnRoute(ReturnRouteMode.All)).Build();
        (await host.PostAsync(await host.AuthcryptAsync(host.Carol, other))).Status.Should().Be(HttpStatusCode.OK);
        ((InMemoryReplayGuard)host.ReplayGuard).PeerCount.Should().Be(1, "Alice's entry was purged by ordinary traffic");

        host.Clock.Advance(TimeSpan.FromSeconds(-(skew + 1)));
        var replay = await host.PostAsync(packed);

        host.Recorder.Seen.Count(r => r.Message.Id == inbound.Id).Should().Be(1,
            "a captured request must reach the handler once; the replay after a {0} s clock step back got {1}", skew + 1, replay.Status);
        replay.Status.Should().Be(HttpStatusCode.Accepted);
        host.DropLines(inbound.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.TooOld));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_posts_of_one_request_at_the_future_bound_reach_the_handler_once(bool fallbackGuard)
    {
        const int Copies = 32;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            if (fallbackGuard)
                o.PostConfigureServices = s => s.RemoveAll<IInboundReplayGuard>();
        });
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, message.From!));
        var inbound = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(host.Clock.Now.ToUnixTimeSeconds() + 60).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);

        var responses = await Task.WhenAll(Enumerable.Range(0, Copies).Select(_ => Task.Run(() => host.PostAsync(packed))));

        responses.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1);
        responses.Where(r => r.Status != HttpStatusCode.OK).Should().AllSatisfy(r => AssertRefused(r, "a concurrent copy"));
        host.Recorder.Invocations.Should().Be(1);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- WebSocket ------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    public async Task WebSocket_refuses_one_second_past_the_bound_silently_and_without_a_session(int skewSeconds, bool fallbackGuard)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.AllowSameSocketReplies = true;
            o.Start = Start.AddMilliseconds(999);
            if (skewSeconds >= 0)
                o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromSeconds(skewSeconds);
            if (fallbackGuard)
                o.PostConfigureServices = s => s.RemoveAll<IInboundReplayGuard>();
        });
        var skew = skewSeconds < 0 ? 60 : skewSeconds;
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var nowS = host.Clock.Now.ToUnixTimeSeconds();
        var refused = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + skew + 1).Build();
        var admitted = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + skew).Build();
        using var socket = await host.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, refused));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.DropLines(refused.Id).Count > 0, Timeout)).Should().BeTrue();
        host.DropLines(refused.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));
        host.Recorder.Invocations.Should().Be(0);
        host.Registry.Find(host.Alice.Did).Should().BeEmpty("a refused message opens no session");

        // The next frame on this socket is the reply to the admitted message: nothing was sent for the refused one.
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, admitted));
        var frame = await ReturnRouteTestHost.ReceiveAsync(socket, Timeout);
        (await host.Alice.Client.UnpackAsync(frame.Text)).Message.Thid.Should().Be(admitted.Id);
        host.Recorder.Invocations.Should().Be(1);
        host.Recorder.Seen.Single().Message.Id.Should().Be(admitted.Id);
        host.Registry.Find(host.Alice.Did).Should().HaveCount(1, "the admitted message opened the session");

        // A replay of the admitted message on the same socket: refused, nothing sent, session unchanged.
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, admitted));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.DropLines(admitted.Id).Count > 0, Timeout)).Should().BeTrue();
        host.DropLines(admitted.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.Duplicate));
        host.Recorder.Invocations.Should().Be(1);
        host.LeakingLogLines().Should().BeEmpty();
    }

    [Fact]
    public async Task WebSocket_without_same_socket_replies_applies_the_same_bound()
    {
        await using var host = await ReturnRouteTestHost.StartAsync();
        var nowS = host.Clock.Now.ToUnixTimeSeconds();
        var at61 = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + 61).Build();
        var at60 = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + 60).Build();
        using var socket = await host.ConnectAsync();

        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, at61));
        await ReturnRouteTestHost.SendAsync(socket, await host.AuthcryptAsync(host.Alice, at60));
        (await ReturnRouteTestHost.WaitUntilAsync(() => host.Recorder.Count == 1 && host.DropLines(at61.Id).Count == 1, Timeout)).Should().BeTrue();

        host.Recorder.Seen.Single().Message.Id.Should().Be(at60.Id);
        host.DropLines(at61.Id).Single().Should().Contain(nameof(ReplayAdmission.FutureDated));
        host.Registry.Find(host.Alice.Did).Should().BeEmpty("same-socket replies are off: no session either way");
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- the session-aware callback endpoint ------------------------------------------------------

    [Fact]
    public async Task The_callback_endpoint_applies_the_same_bound_and_never_runs_the_callback_for_a_refusal()
    {
        var calls = new ConcurrentQueue<string>();
        ReturnRouteTestHost? hostRef = null;
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
            o.Callback = (unpacked, _, _) =>
            {
                calls.Enqueue(unpacked.Message.Id);
                return Task.FromResult<Message?>(ReturnRouteHttpTests.ReplyTo(hostRef!, unpacked.Message, hostRef!.Alice.Did));
            });
        hostRef = host;
        var nowS = host.Clock.Now.ToUnixTimeSeconds();
        var at60 = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + 60).Build();
        var at61 = host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All).WithCreatedTime(nowS + 61).Build();

        AssertRefused(await host.PostAsync(await host.AuthcryptAsync(host.Alice, at61), "/didcomm-callback"), "61 s ahead");
        calls.Should().BeEmpty();
        host.DropLines(at61.Id).Should().ContainSingle().Which.Should().Contain(nameof(ReplayAdmission.FutureDated));

        var packed60 = await host.AuthcryptAsync(host.Alice, at60);
        (await host.PostAsync(packed60, "/didcomm-callback")).Status.Should().Be(HttpStatusCode.OK);
        AssertRefused(await host.PostAsync(packed60, "/didcomm-callback"), "replay");
        calls.Should().Equal(at60.Id);
        host.LeakingLogLines().Should().BeEmpty();
    }

    // ---- invalid options fail closed ---------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_negative_ClockSkew_fails_closed_and_reaches_no_handler(bool fallbackGuard)
    {
        await using var host = await ReturnRouteTestHost.StartAsync(o =>
        {
            o.ConfigureReplay = r => r.ClockSkew = TimeSpan.FromSeconds(-1);
            if (fallbackGuard)
                o.PostConfigureServices = s => s.RemoveAll<IInboundReplayGuard>();
        });
        host.Recorder.Behavior = (message, _) => (Array.Empty<Message>(), ReturnRouteHttpTests.ReplyTo(host, message, host.Alice.Did));
        var inbound = host.Stamped(host.RecordingMessage(host.Alice).WithReturnRoute(ReturnRouteMode.All)).Build();
        var packed = await host.AuthcryptAsync(host.Alice, inbound);

        HttpStatusCode? status = null;
        Exception? fault = null;
        try
        {
            status = (await host.PostAsync(packed)).Status;
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        host.Recorder.Invocations.Should().Be(0, "an invalid replay configuration admits nothing (status {0}, fault {1})", status, fault?.GetType().Name);
        // TestServer surfaces the endpoint's exception to the client (Kestrel would answer 500): the
        // guard's construction fails on the request path, every time, and nothing is dispatched.
        status.Should().BeNull();
        fault.Should().BeOfType<InvalidOperationException>().Which.Message.Should().StartWith("ReplayProtectionOptions.ClockSkew");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static void AssertRefused(HttpPostResult response, string because, params object[] args)
    {
        response.Status.Should().Be(HttpStatusCode.Accepted, because, args);
        response.Body.Should().BeEmpty(because, args);
    }

    private static string Outcome(string dropLine)
    {
        const string Marker = "replay admission ";
        var start = dropLine.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        var end = dropLine.IndexOf(' ', start);
        return dropLine[start..end];
    }
}
