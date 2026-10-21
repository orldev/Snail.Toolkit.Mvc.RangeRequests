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

    /// <summary>Checks the ranges each header is read as, so a change timed here cannot also change them.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string expected = Header switch
        {
            "bytes=0-1023" => "Satisfiable 0-1023",
            "bytes=400-499,0-99,200-299,90-150" => "Satisfiable 0-150 200-299 400-499",
            _ => "Ignored",
        };

        var read = Parse();
        string actual = string.Join(' ', [read.Satisfaction.ToString(), .. read.Ranges.Select(range => $"{range.From}-{range.To}")]);

        if (actual != expected)
            throw new InvalidOperationException($"'{Header}' was read as '{actual}', not '{expected}'.");
    }

    [Benchmark]
    public ByteRangeSet Parse() => ByteRanges.Parse(Header, 1 << 20, 5);
}
