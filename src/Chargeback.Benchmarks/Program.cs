using BenchmarkDotNet.Running;

namespace Chargeback.Benchmarks;

public class BenchmarkRunner
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(CalculatorBenchmarks).Assembly).Run(args);
    }
}
