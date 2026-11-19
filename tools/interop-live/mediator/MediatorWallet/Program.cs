namespace MediatorWallet;

/// <summary>
/// The wallet of the mediator interop leg (#98, harness contract §10). Each command prints one JSON
/// object on stdout (<see cref="CommandResult"/>); diagnostics go to stderr. Exit codes: 0 success,
/// 1 failure (an expectation did not hold, or an operation failed), 2 usage error, 3 a
/// precondition does not hold (<c>--expect-env-proxy</c>).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
            return Usage();

        var command = args[0];
        var result = new CommandResult(command);
        int exit;
        try
        {
            var line = CommandLine.Parse(args.Skip(1));
            exit = command switch
            {
                "enrol" => await EnrolCommand.RunAsync(line, result).ConfigureAwait(false),
                "fetch" => await FetchCommand.RunAsync(line, result).ConfigureAwait(false),
                "ws-attempt" => await WsAttemptCommand.RunAsync(line, result).ConfigureAwait(false),
                "observe" => await ObserveCommand.RunAsync(line, result).ConfigureAwait(false),
                "boundary" => await BoundaryCommand.RunAsync(line, result).ConfigureAwait(false),
                _ => throw new UsageException($"Unknown command '{command}'."),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"MediatorWallet: {ex.Message}");
            result.Fail(ex);
            Console.WriteLine(result.ToJson());
            Usage();
            return 2;
        }
        catch (PreconditionException ex)
        {
            Console.Error.WriteLine($"MediatorWallet: {ex.Message}");
            result.Fail(ex);
            exit = 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MediatorWallet: {ex.GetType().Name}: {ex.Message}");
            result.Fail(ex);
            exit = 1;
        }

        Console.WriteLine(result.ToJson());
        return exit;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              MediatorWallet enrol --identity-dir <dir> --invitation <invitation.json> --expect-mediator-did <did>
                                   [--expect-version 2.0|3.0] [--pin V2|V3] [--endpoint-override <url>] --out <state.json>
              MediatorWallet fetch --state <state.json> --expect-count <n> [--limit <n>] [--explicit-ack]
                                   [--wait-timeout <s>] --out-dir <dir>
              MediatorWallet ws-attempt --state <state.json> --ws-endpoint auto|<url> [--timeout <s>]
              MediatorWallet observe --state <state.json> [--triggers <dir>] --out <observations.json>
              MediatorWallet boundary --out <refs.json> [--forbid <name>[,<prefix>*...]]
            Common options:
                                   [--allow-hosts <h1,h2>] [--trusted-proxy <url>] [--resolve-dns-names true|false]
                                   [--expect-env-proxy <url>] [--relax-created-time] [--request-timeout <s>]
                                   [--log-out <log.jsonl>]
            Exit: 0 ok · 1 failed · 2 usage · 3 precondition
            """);
        return 2;
    }
}
