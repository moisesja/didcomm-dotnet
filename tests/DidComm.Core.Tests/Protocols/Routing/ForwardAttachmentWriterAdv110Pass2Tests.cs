using System.Diagnostics.Tracing;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Json;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #110 red team pass 2, Low (ADV-110-2c-1), resolved as documented (the coordinator's call): the
/// capped buffer a forward's <c>data.json</c> attachment is written into counts only the bytes written,
/// so the writer's own scratch space is outside the cap; the docs state that bound (about eight times
/// the limit, briefly). For a string that needs escaping, <c>Utf8JsonWriter</c> rents an escape buffer of six times
/// the string's length from the first character that needs it, rounded up to the pool's power-of-two
/// size. One escaped character in front of a long plain string costs that much although almost
/// nothing is written escaped: an attachment written within the receive limit rents eight times the
/// limit. Measured as the largest array rented from the shared pool on this thread (deterministic,
/// unlike allocation counts, which depend on what the pool already holds).
/// </summary>
[Collection(ForwardRetentionProbeCollection.Name)]
public sealed class ForwardAttachmentWriterAdv110Pass2Tests(ITestOutputHelper output)
{
    private const int Limit = 1024 * 1024;

    [Fact]
    public void An_attachment_written_within_the_receive_limit_rents_at_most_about_eight_times_the_limit()
    {
        // 700,000 plain characters after one control character: written as 700,014 bytes, within the
        // 1 MiB limit, as a sender's plaintext of that size can carry.
        var escaped = Measure("{\"x\":\"\\u0001" + new string('a', 700_000) + "\"}");
        var plain = Measure("{\"x\":\"" + new string('a', 700_006) + "\"}");

        output.WriteLine($"one escape first: written {escaped.Written:N0} B, largest rent {escaped.MaxRented:N0} B, thread allocated {escaped.Allocated:N0} B");
        output.WriteLine($"plain:            written {plain.Written:N0} B, largest rent {plain.MaxRented:N0} B, thread allocated {plain.Allocated:N0} B");

        escaped.Written.Should().BeLessThan(Limit, "the attachment passes the cap");
        escaped.MaxRented.Should().BeLessThanOrEqualTo(8L * Limit, "the writer's escape scratch is bounded by about eight times the limit (ForwardProcessor.ExtractAttachmentBytes remarks)");
        plain.MaxRented.Should().BeLessThanOrEqualTo(2L * Limit, "a plain attachment needs no escape scratch");
    }

    private static (long Written, long MaxRented, long Allocated) Measure(string attachmentJson)
    {
        var json = "{\"id\":\"x\",\"type\":\"t\",\"body\":{},\"attachments\":[{\"id\":\"a1\",\"data\":{\"json\":" + attachmentJson + "}}]}";
        var attachment = JsonSerializer.Deserialize<Message>(json, DidCommJson.Default)!.Attachments![0];

        using var listener = new PoolRentListener();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var text = ForwardProcessor.ExtractAttachmentText(attachment, Limit);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return (Encoding.UTF8.GetByteCount(text), listener.MaxRented, allocated);
    }

    /// <summary>The largest array rented from the shared pools on the thread that created it.</summary>
    private sealed class PoolRentListener : EventListener
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;

        public long MaxRented { get; private set; }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // BufferRented (event 1): bufferId, bufferSize, poolId, bucketId.
            if (eventData.EventId == 1 && eventData.Payload is { Count: > 1 } payload && Environment.CurrentManagedThreadId == _thread)
                MaxRented = Math.Max(MaxRented, Convert.ToInt64(payload[1]));
        }
    }
}
