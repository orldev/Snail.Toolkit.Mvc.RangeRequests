using BenchmarkDotNet.Attributes;

namespace Snail.Toolkit.Mvc.RangeRequests.Benchmarks;

/// <summary>
/// The Range header read against an entity: one range, several to coalesce, and one header too long to parse.
/// </summary>
[MemoryDiagnoser]
public class ParseBenchmarks
{
    [Params("bytes=0-1023", "bytes=400-499,0-99,200-299,90-150", "bytes=0-0,1-1,2-2,3-3,4-4,5-5,6-6")]
    public string Header { get; set; } = "bytes=0-1023";

    [Benchmark]
    public ByteRangeSet Parse() => ByteRanges.Parse(Header, 1 << 20, 5);
}
