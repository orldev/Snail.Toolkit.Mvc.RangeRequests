using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Snail.Toolkit.Mvc.RangeRequests.Benchmarks;

/// <summary>
/// One request answered end to end on a fresh context, the content shared across requests as the docs describe:
/// the whole entity, one range, three ranges as multipart, and a conditional request answered 304. The writer writes
/// nothing, so what is timed is the library.
/// </summary>
[MemoryDiagnoser]
public class AnswerBenchmarks
{
    private static readonly RangeContent Content = new()
    {
        Write = static (_, _, _, _) => Task.CompletedTask,
        Length = 1 << 20,
        ContentType = "video/mp4",
        LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        FileName = "clip.mp4"
    };

    private readonly RangeFileHttpResult _result = new(Content);

    private ServiceProvider _services = null!;

    [Params("none", "one range", "three ranges", "if-none-match")]
    public string Request { get; set; } = "none";

    [GlobalSetup]
    public void Setup() => _services = new ServiceCollection().AddLogging().BuildServiceProvider();

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark]
    public Task Answer()
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Method = HttpMethods.Get;

        switch (Request)
        {
            case "one range":
                context.Request.Headers.Range = "bytes=0-1023";
                break;
            case "three ranges":
                context.Request.Headers.Range = "bytes=0-99,200-299,400-499";
                break;
            case "if-none-match":
                context.Request.Headers.IfNoneMatch = "*";
                break;
        }

        return _result.ExecuteAsync(context);
    }
}
