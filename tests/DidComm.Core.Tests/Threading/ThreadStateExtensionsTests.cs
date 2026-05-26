using DidComm.Threading;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Threading;

public sealed class ThreadStateExtensionsTests
{
    [Fact]
    public void ErrorCount_defaults_to_zero_and_is_mutable_per_thread()
    {
        var store = new InMemoryThreadStateStore();
        var a = store.GetOrCreate("thread-a");
        var b = store.GetOrCreate("thread-b");
        a.ErrorCount.Should().Be(0);
        b.ErrorCount.Should().Be(0);
        a.ErrorCount++;
        a.ErrorCount.Should().Be(1);
        b.ErrorCount.Should().Be(0); // isolation
    }
}
