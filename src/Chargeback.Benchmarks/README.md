# Chargeback API Benchmarks

Performance benchmarks using [BenchmarkDotNet](https://benchmarkdotnet.org/).

## Running Benchmarks

```bash
# Run all benchmarks
cd src/Chargeback.Benchmarks
dotnet run -c Release -- --filter *

# Run specific benchmark class
dotnet run -c Release -- --filter *Calculator*
dotnet run -c Release -- --filter *Serialization*

# Run endpoint benchmarks (requires Redis running)
dotnet run -c Release -- --filter *Endpoint*
```

## Benchmark Categories

| Benchmark | What it measures |
|-----------|-----------------|
| CalculatorBenchmarks | Pure CPU cost of chargeback cost calculation |
| SerializationBenchmarks | JSON serialization/deserialization overhead |
| EndpointBenchmarks | Full HTTP pipeline (requires Redis) |
