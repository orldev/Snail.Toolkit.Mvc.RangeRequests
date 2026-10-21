using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Snail.Toolkit.Mvc.RangeRequests;

var entity = Encoding.ASCII.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
var content = new RangeContent
{
    Write = (body, offset, length, cancellationToken) => body.WriteAsync(entity.AsMemory((int)offset, (int)length), cancellationToken).AsTask(),
    Length = entity.Length,
    ContentType = "text/plain",
    LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
};

await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

var single = await AnswerAsync("bytes=10-15");
var multipart = await AnswerAsync("bytes=0-1,30-35");
var whole = await AnswerAsync(null);
var isSingle = single.Status == StatusCodes.Status206PartialContent && single.Body == "abcdef";
var isMultipart = multipart.Status == StatusCodes.Status206PartialContent && multipart.Body.Contains("uvwxyz", StringComparison.Ordinal);
var isWhole = whole.Status == StatusCodes.Status200OK && whole.Body.Length == entity.Length;

Console.WriteLine($"single {single.Status} '{single.Body}', multipart {multipart.Status} {multipart.Body.Length} chars, whole {whole.Status} {whole.Body.Length} bytes");

return isSingle && isMultipart && isWhole ? 0 : 1;

async Task<(int Status, string Body)> AnswerAsync(string? range)
{
    using var body = new MemoryStream();
    var context = new DefaultHttpContext { RequestServices = services };
    context.Request.Method = HttpMethods.Get;
    context.Response.Body = body;

    if (range is not null)
        context.Request.Headers.Range = range;

    await new RangeFileHttpResult(content).ExecuteAsync(context);

    return (context.Response.StatusCode, Encoding.ASCII.GetString(body.ToArray()));
}
