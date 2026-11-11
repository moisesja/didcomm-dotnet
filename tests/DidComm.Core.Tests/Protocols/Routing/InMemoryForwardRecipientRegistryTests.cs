using DidComm.Protocols.Routing;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// Plan matrix U3 (#90): <see cref="InMemoryForwardRecipientRegistry"/> stores DID subjects
/// (PRD §4.3 <c>DidSubjectOf</c>), rejects non-DIDs without echoing them, answers lookups with an
/// ordinal hash probe on the bare DID, and is safe under concurrent mutation.
/// </summary>
public sealed class InMemoryForwardRecipientRegistryTests
{
    private const string Bob = "did:example:bob";

    [Fact]
    public async Task Starts_empty()
    {
        var registry = new InMemoryForwardRecipientRegistry();

        registry.Count.Should().Be(0);
        (await registry.IsRegisteredAsync(Bob, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("did:example:bob#key-1")]
    [InlineData("did:example:bob/path")]
    [InlineData("did:example:bob?versionId=1")]
    [InlineData("did:example:bob;service=agent/path?q=1#frag")]
    public async Task Register_reduces_a_DID_URL_to_its_subject(string didUrl)
    {
        var registry = new InMemoryForwardRecipientRegistry();

        registry.Register(didUrl);

        registry.Count.Should().Be(1);
        (await registry.IsRegisteredAsync(Bob, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Lookup_does_not_reduce_its_argument()
    {
        // Documented contract: callers pass the bare DID; the handler reduces `next` before asking.
        var registry = new InMemoryForwardRecipientRegistry();
        registry.Register(Bob);

        (await registry.IsRegisteredAsync(Bob + "#key-1", CancellationToken.None)).Should().BeFalse();
        (await registry.IsRegisteredAsync(Bob, CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-did")]
    [InlineData("did:")]
    [InlineData("https://example.com/bob")]
    [InlineData("#key-1")]
    public void Register_rejects_a_non_DID_without_echoing_it(string value)
    {
        var registry = new InMemoryForwardRecipientRegistry();

        var act = () => registry.Register(value);

        var failure = act.Should().Throw<ArgumentException>().WithParameterName("didOrDidUrl").Which;
        if (value.Length > 0)
            failure.Message.Should().NotContain(value);
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void Register_rejects_an_over_long_value_without_echoing_it()
    {
        // 4097 characters: over the FR-API-09 key-id cap, so DidSubjectOf declines to parse it.
        var registry = new InMemoryForwardRecipientRegistry();
        var fragment = new string('k', 4097 - (Bob.Length + 1));
        var value = Bob + "#" + fragment;
        value.Length.Should().Be(4097);

        var act = () => registry.Register(value);

        var failure = act.Should().Throw<ArgumentException>().Which;
        failure.Message.Should().NotContain(fragment[..64]).And.NotContain(value);
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void Register_accepts_a_value_at_exactly_the_cap()
    {
        var registry = new InMemoryForwardRecipientRegistry();
        var value = Bob + "#" + new string('k', 4096 - (Bob.Length + 1));
        value.Length.Should().Be(4096);

        registry.Register(value);

        registry.Count.Should().Be(1);
    }

    [Fact]
    public void Register_rejects_null()
    {
        var registry = new InMemoryForwardRecipientRegistry();

        var act = () => registry.Register(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Lookup_rejects_null()
    {
        var registry = new InMemoryForwardRecipientRegistry();

        var act = async () => await registry.IsRegisteredAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Unregister_removes_and_reports()
    {
        var registry = new InMemoryForwardRecipientRegistry();
        registry.Register(Bob);

        registry.Unregister("did:example:bob#any-key").Should().BeTrue("a DID URL is reduced to the registered subject");
        (await registry.IsRegisteredAsync(Bob, CancellationToken.None)).Should().BeFalse();
        registry.Unregister(Bob).Should().BeFalse("already removed");
        registry.Unregister("did:example:carol").Should().BeFalse("never registered");
        registry.Unregister("not-a-did").Should().BeFalse("a non-DID is reported, not thrown");
        registry.Unregister(null!).Should().BeFalse();
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void Count_tracks_registrations()
    {
        var registry = new InMemoryForwardRecipientRegistry();

        registry.Register(Bob);
        registry.Register(Bob + "#key-1");
        registry.Count.Should().Be(1, "re-registration under the same subject is a no-op");

        registry.Register("did:example:carol");
        registry.Count.Should().Be(2);

        registry.Unregister(Bob);
        registry.Count.Should().Be(1);
    }

    [Fact]
    public async Task Lookup_is_ordinal()
    {
        var registry = new InMemoryForwardRecipientRegistry();
        registry.Register(Bob);

        (await registry.IsRegisteredAsync("did:example:Bob", CancellationToken.None)).Should().BeFalse();
        (await registry.IsRegisteredAsync("DID:example:bob", CancellationToken.None)).Should().BeFalse();
        (await registry.IsRegisteredAsync(Bob, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_register_unregister_and_lookup_do_not_throw()
    {
        var registry = new InMemoryForwardRecipientRegistry();
        using var start = new ManualResetEventSlim();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < 500; i++)
            {
                var did = $"did:example:w{worker}-{i % 16}";
                registry.Register(did + "#key-" + i);
                _ = await registry.IsRegisteredAsync(did, CancellationToken.None);
                _ = await registry.IsRegisteredAsync(Bob, CancellationToken.None);
                if (i % 3 == 0)
                    registry.Unregister(did);
                _ = registry.Count;
            }
        })).ToArray();

        start.Set();
        var act = async () => await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        await act.Should().NotThrowAsync();
        registry.Count.Should().BeLessThanOrEqualTo(8 * 16);
    }
}
