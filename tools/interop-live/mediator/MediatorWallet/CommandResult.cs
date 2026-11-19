using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediatorWallet;

/// <summary>
/// The one JSON object a command prints on stdout:
/// <c>{"command","ok","steps":[{"name","ok","ms","detail"}],"observed":{},"error":{"type","inner":[],"message"}}</c>.
/// Each step is timed on the monotonic clock; a step that throws is recorded as failed and the
/// exception goes on (the command fails), so the last step names where it stopped.
/// </summary>
internal sealed class CommandResult(string command)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonArray _steps = new();
    private JsonObject? _error;

    /// <summary>What the command measured, by name (data for the summary; not all of it gated).</summary>
    public JsonObject Observed { get; } = new();

    /// <summary>Run one step and record it; its detail is computed from the step's value.</summary>
    public async Task<T> StepAsync<T>(string name, Func<Task<T>> body, Func<T, JsonNode?>? detail = null)
    {
        var watch = Stopwatch.StartNew();
        T value;
        try
        {
            value = await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddStep(name, ok: false, watch.ElapsedMilliseconds, Describe(ex));
            throw;
        }

        AddStep(name, ok: true, watch.ElapsedMilliseconds, detail?.Invoke(value));
        return value;
    }

    /// <summary>Run one step that returns nothing.</summary>
    public Task StepAsync(string name, Func<Task> body, JsonNode? detail = null)
        => StepAsync<bool>(name, async () =>
        {
            await body().ConfigureAwait(false);
            return true;
        }, _ => detail);

    /// <summary>Record a check; a false one fails the command with <see cref="ExpectationException"/>.</summary>
    public void Check(string name, bool holds, JsonNode? detail = null)
    {
        AddStep(name, holds, 0, detail);
        if (!holds)
            throw new ExpectationException($"Check failed: {name}.");
    }

    /// <summary>Record the exception that ended the command.</summary>
    public void Fail(Exception ex) => _error = Describe(ex);

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["command"] = command,
            ["ok"] = _error is null,
            ["steps"] = _steps.DeepClone(),
            ["observed"] = Observed.DeepClone(),
            ["error"] = _error?.DeepClone(),
        };
        return root.ToJsonString(Json);
    }

    /// <summary>An exception as <c>{"type","inner":[types],"message"}</c>, inner types outermost first.</summary>
    public static JsonObject Describe(Exception ex)
    {
        var inner = new JsonArray();
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
            inner.Add(e.GetType().FullName);
        var described = new JsonObject
        {
            ["type"] = ex.GetType().FullName,
            ["inner"] = inner,
            ["message"] = ex.Message,
        };
        if (ex is DidComm.Mediator.Client.MediationProtocolException protocol)
            described["problem_code"] = protocol.ProblemCode;
        if (ex is DidComm.Exceptions.TransportException transport)
            described["http_status"] = transport.HttpStatusCode;
        return described;
    }

    private void AddStep(string name, bool ok, long ms, JsonNode? detail)
        => _steps.Add(new JsonObject { ["name"] = name, ["ok"] = ok, ["ms"] = ms, ["detail"] = detail });
}
