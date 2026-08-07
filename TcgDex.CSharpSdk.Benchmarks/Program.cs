using BenchmarkDotNet.Running;
using TcgDex.Benchmarks;

// Runs every benchmark in the assembly, or a subset via the usual
// BenchmarkDotNet arguments — for example:
//
//   dotnet run -c Release --project TcgDex.CSharpSdk.Benchmarks -- --filter *Query*
//
// A benchmark type is the assembly marker rather than Program: top-level
// statements already generate a Program class, and declaring another collides
// with it.
BenchmarkSwitcher.FromAssembly(typeof(QueryBenchmarks).Assembly).Run(args);
