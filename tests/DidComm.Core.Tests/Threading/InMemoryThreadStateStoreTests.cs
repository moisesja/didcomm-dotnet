using DidComm.Threading;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Threading;

public sealed class InMemoryThreadStateStoreTests
{
    [Fact]
    public void GetOrCreate_returns_same_instance_per_thid()
    {
        var store = new InMemoryThreadStateStore();
        var first = store.GetOrCreate("thread-a");
        var second = store.GetOrCreate("thread-a");
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Different_threads_do_not_share_state()
    {
        var store = new InMemoryThreadStateStore();
        var a = store.GetOrCreate("thread-a");
        var b = store.GetOrCreate("thread-b");
        a.Should().NotBeSameAs(b);
        a.AcceptLang = new[] { "fr", "en" };
        b.AcceptLang.Should().BeNull();
    }

    [Fact]
    public void Accept_lang_preference_does_not_leak_across_concurrent_threads()
    {
        // FR-I18N-02 acceptance test: a preference set on one thread MUST NOT influence another.
        var store = new InMemoryThreadStateStore();
        store.GetOrCreate("thread-fr").AcceptLang = new[] { "fr" };
        var concurrent = store.GetOrCreate("thread-en");
        concurrent.AcceptLang.Should().BeNull();
    }

    [Fact]
    public void Remove_drops_state_so_next_GetOrCreate_starts_fresh()
    {
        // FR-I18N-02: "applies … until the current protocol thread (thid) ends".
        var store = new InMemoryThreadStateStore();
        store.GetOrCreate("thread-a").AcceptLang = new[] { "fr" };
        store.Remove("thread-a").Should().BeTrue();
        store.Get("thread-a").Should().BeNull();
        store.GetOrCreate("thread-a").AcceptLang.Should().BeNull();
    }

    [Fact]
    public void Get_returns_null_for_unknown_thread()
    {
        var store = new InMemoryThreadStateStore();
        store.Get("nope").Should().BeNull();
    }

    [Fact]
    public void Empty_thid_throws()
    {
        var store = new InMemoryThreadStateStore();
        ((Action)(() => store.GetOrCreate(string.Empty))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Get(string.Empty))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Remove(string.Empty))).Should().Throw<ArgumentException>();
    }
}
