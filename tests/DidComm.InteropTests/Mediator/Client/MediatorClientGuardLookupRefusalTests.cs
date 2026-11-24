using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Mediator.Client;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Mediator.Client;

/// <summary>
/// #110 (the Core guard, L-075), through the recipient client: a mediator whose DID document names
/// an endpoint whose host name the lookup refuses outright (longer than 255 characters) is refused
/// with the <see cref="TransportException"/> the client documents for a blocked mediator address,
/// before anything is sent. It used to escape as an <see cref="ArgumentOutOfRangeException"/>. The
/// refusal's text names no host and no URI.
/// </summary>
public sealed class MediatorClientGuardLookupRefusalTests
{
    private static readonly string LongHost = string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".example";

    [Fact]
    public async Task A_mediator_endpoint_whose_name_cannot_be_looked_up_is_a_TransportException_naming_no_host()
    {
        var mediator = await MediatorCast.MintAsync(new DidCommServiceSpec($"https://{LongHost}/didcomm"));
        var bob = await MediatorCast.MintAsync();
        var secrets = new InMemorySecretsResolver();
        foreach (var key in bob.Privates)
            secrets.Add(key);
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.UseHttpTransport();
            b.AddMediatorClient();
        });
        await using var sp = services.BuildServiceProvider();

        var caught = await Record.ExceptionAsync(() => sp.GetRequiredService<MediationClient>().RequestMediationAsync(mediator.Did, bob.Did).WaitAsync(MediatorTestServer.Timeout));

        var refusal = caught.Should().BeOfType<TransportException>().Which;
        refusal.HttpStatusCode.Should().BeNull("nothing was sent");
        refusal.ToString().Should().NotContain(new string('a', 60), "the refusal names no host").And.NotContain("://", "nor a URI");
    }
}
