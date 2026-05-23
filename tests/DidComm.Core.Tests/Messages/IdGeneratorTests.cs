using DidComm.Messages;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Messages;

public sealed class IdGeneratorTests
{
    [Fact]
    public void Default_generator_emits_lowercase_uuid_v4()
    {
        var id = UuidV4MessageIdGenerator.Instance.NewId();

        // 36 chars, lowercase, version-4 nibble = '4', RFC 4122 variant nibble in {8,9,a,b}.
        id.Should().MatchRegex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
        Guid.Parse(id).ToString("D").Should().Be(id);
    }

    [Fact]
    public void Generator_produces_no_collisions_across_10000_ids()
    {
        var set = new HashSet<string>(capacity: 10_000);
        for (var i = 0; i < 10_000; i++)
        {
            var id = UuidV4MessageIdGenerator.Instance.NewId();
            set.Add(id).Should().BeTrue($"id #{i} was a duplicate of an earlier id");
        }
        set.Should().HaveCount(10_000);
    }

    [Fact]
    public void Custom_generator_is_honored_by_the_builder()
    {
        var fixedGen = new FixedIdGenerator("custom-id-123");
        var msg = new MessageBuilder(fixedGen)
            .WithType("https://didcomm.org/empty/1.0/empty")
            .Build();
        msg.Id.Should().Be("custom-id-123");
    }

    private sealed class FixedIdGenerator(string id) : IMessageIdGenerator
    {
        public string NewId() => id;
    }
}
