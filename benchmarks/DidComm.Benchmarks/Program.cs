using BenchmarkDotNet.Running;

namespace DidComm.Benchmarks;

/// <summary>Entry point: <c>dotnet run -c Release --project benchmarks/DidComm.Benchmarks</c>.</summary>
public static class Program
{
    /// <summary>Run the suite (BenchmarkSwitcher honors --filter/--job etc.).</summary>
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
