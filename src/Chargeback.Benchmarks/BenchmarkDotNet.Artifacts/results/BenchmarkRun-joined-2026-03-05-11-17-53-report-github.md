```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.7781/25H2/2025Update/HudsonValley2)
AMD Ryzen AI 7 PRO 350 w/ Radeon 860M 2.00GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 11.0.100-preview.1.26104.118
  [Host]     : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4
  Job-YFEFPZ : .NET 10.0.3 (10.0.3, 10.0.326.7603), X64 RyuJIT x86-64-v4

IterationCount=10  WarmupCount=3  

```
| Type                    | Method                      | Mean      | Error      | StdDev     | Gen0   | Gen1   | Gen2   | Allocated |
|------------------------ |---------------------------- |----------:|-----------:|-----------:|-------:|-------:|-------:|----------:|
| CalculatorBenchmarks    | CalculateCost               |  80.15 ns |   2.502 ns |   1.655 ns |      - |      - |      - |         - |
| SerializationBenchmarks | SerializeLogIngestRequest   | 505.72 ns | 158.087 ns | 104.565 ns | 0.0272 | 0.0010 | 0.0010 |     984 B |
| CalculatorBenchmarks    | CalculateCustomerCost       |  95.20 ns |   6.933 ns |   3.626 ns | 0.0064 |      - |      - |     192 B |
| SerializationBenchmarks | DeserializeLogIngestRequest | 940.78 ns |  90.137 ns |  59.620 ns | 0.0353 |      - |      - |     984 B |
| SerializationBenchmarks | SerializeCachedLogData      | 411.53 ns |  27.813 ns |  16.551 ns | 0.0200 | 0.0005 | 0.0005 |         - |
| SerializationBenchmarks | DeserializeCachedLogData    | 643.28 ns |  57.617 ns |  34.287 ns | 0.0148 |      - |      - |     408 B |
