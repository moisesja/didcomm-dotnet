using DidComm.Exceptions;
using DidComm.Messages;
using DidComm.Threading;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Threading;

/// <summary>
/// The thread a reply joins (#138). DIDComm v2.1 threading.md L15: a request that declares no
/// <c>thid</c> has its <c>id</c> as its <c>thid</c>; L17: every later message in the thread carries
/// the request's <c>thid</c>.
/// </summary>
public sealed class ThreadIdsTests
{
    private static Message Request(string id = "request-id-1") =>
        new MessageBuilder().WithId(id).WithType("https://didcomm.org/test/1.0/request").Build();

    [Fact]
    public void A_request_without_thid_is_answered_on_its_id()
    {
        var request = Request();
        ThreadIds.ResponseThreadId(request).Should().Be("request-id-1");
    }

    [Fact]
    public void A_request_that_declares_thid_is_answered_on_that_thid_not_its_id()
    {
        var request = Request();
        request.Thid = "thread-the-requester-named";
        ThreadIds.ResponseThreadId(request).Should().Be("thread-the-requester-named");
    }

    [Fact]
    public void A_request_whose_thid_equals_its_id_is_answered_on_that_value()
    {
        var request = Request();
        request.Thid = request.Id;
        ThreadIds.ResponseThreadId(request).Should().Be("request-id-1");
    }

    [Fact]
    public void The_declared_thid_is_returned_as_the_request_carries_it()
    {
        // Comparing ids case-insensitively is the requester's job (threading.md L9); the reply echoes.
        var request = Request();
        request.Thid = "Thread-MiXeD.Case_~1";
        ThreadIds.ResponseThreadId(request).Should().Be("Thread-MiXeD.Case_~1");
    }

    [Fact]
    public void A_pthid_does_not_change_the_thread()
    {
        var withoutThid = Request();
        withoutThid.Pthid = "parent-thread";
        ThreadIds.ResponseThreadId(withoutThid).Should().Be("request-id-1");

        var withThid = Request();
        withThid.Thid = "child-thread";
        withThid.Pthid = "parent-thread";
        ThreadIds.ResponseThreadId(withThid).Should().Be("child-thread");
    }

    public static TheoryData<string> InvalidThids => new()
    {
        "",
        " ",
        "zz9 zz9",
        "zz9/zz9",
        "zz9#zz9",
        "zz9" + (char)0x1B + "[31m",
        "zz9" + (char)0x0A + "zz9",
        "zz9" + (char)0xE9,
    };

    [Theory]
    [MemberData(nameof(InvalidThids))]
    public void A_declared_thid_that_is_not_a_valid_thread_id_is_refused_without_quoting_it(string thid)
    {
        // A message built by hand reaches a reply builder unvalidated. Its invalid thid must be
        // refused, never replaced by the id (which would answer on a thread nobody named).
        var request = Request();
        request.Thid = thid;

        var ex = Record.Exception(() => ThreadIds.ResponseThreadId(request));

        ex.Should().BeOfType<MalformedMessageException>();
        ex!.Message.Should().NotContain("zz9");
        ex.Message.Should().NotContain("request-id-1");
    }

    [Fact]
    public void A_null_request_is_refused()
    {
        var ex = Record.Exception(() => ThreadIds.ResponseThreadId(null!));
        ex.Should().BeOfType<ArgumentNullException>();
    }
}
