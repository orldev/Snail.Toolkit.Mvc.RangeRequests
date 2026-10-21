using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Snail.Toolkit.Mvc.RangeRequests.Benchmarks;

/// <summary>
/// One request answered end to end on a fresh context, the content shared across requests as the docs describe:
/// the whole entity, one range, a download resumed against the tag it was sent, three ranges as multipart, and a
/// conditional request answered 304. The writer writes
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

    private const string Modified = "Thu, 01 Jan 2026 00:00:00 GMT";

    private const string Tag = "\"48de48c8b4e80000\"";

    private readonly RangeFileHttpResult _result = new(Content);

    private ServiceProvider _services = null!;

    [Params("none", "one range", "resume", "three ranges", "if-none-match")]
    public string Request { get; set; } = "none";

    /// <summary>Builds the services and checks the answer to the request, so a change timed here cannot also change it.</summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var context = Requesting();
        await _result.ExecuteAsync(context).ConfigureAwait(false);

        var headers = context.Response.Headers;
        string actual = $"{context.Response.StatusCode} {headers.ContentRange} {headers.LastModified} {headers.ETag} {context.Response.ContentLength}";
        string expected = Request switch
        {
            "one range" or "resume" => $"206 bytes 0-1023/1048576 {Modified} {Tag} 1024",
            "three ranges" => $"206  {Modified} {Tag} 642",
            "if-none-match" => $"304   {Tag} ",
            _ => $"200  {Modified} {Tag} 1048576",
        };

        if (actual != expected || (Request == "three ranges" && context.Response.ContentType?.StartsWith("multipart/byteranges", StringComparison.Ordinal) != true))
            throw new InvalidOperationException($"'{Request}' was answered '{actual}', not '{expected}'.");
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark]
    public Task Answer() => _result.ExecuteAsync(Requesting());

    private DefaultHttpContext Requesting()
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Method = HttpMethods.Get;

        switch (Request)
        {
            case "one range":
                context.Request.Headers.Range = "bytes=0-1023";
                break;
            case "resume":
                context.Request.Headers.Range = "bytes=0-1023";
                context.Request.Headers.IfRange = Tag;
                break;
            case "three ranges":
                context.Request.Headers.Range = "bytes=0-99,200-299,400-499";
                break;
            case "if-none-match":
                context.Request.Headers.IfNoneMatch = "*";
                break;
        }

        return context;
    }
}
