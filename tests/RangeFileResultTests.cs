using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

public class RangeFileResultTests
{
    private const long EntityLength = 1000;
    private const string EntityContentType = "application/octet-stream";

    private static readonly DateTimeOffset LastModified = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Entity = [.. Enumerable.Range(0, (int)EntityLength).Select(index => (byte)index)];

    [Fact]
    public async Task ExecuteResultAsync_NoRangeHeader_ServesTheWholeEntity()
    {
        var context = Requesting();

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(EntityLength, context.Response.ContentLength);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal(Entity, BodyOf(context));
    }

    [Fact]
    public async Task ExecuteResultAsync_SingleRange_ServesExactlyThoseBytes()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=100-199";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal($"bytes 100-199/{EntityLength}", context.Response.Headers.ContentRange);
        Assert.Equal(100, context.Response.ContentLength);
        Assert.Equal(Entity[100..200], BodyOf(context));
    }

    /// <summary>
    /// The header that used to escape the try block as an IndexOutOfRangeException and answer 500.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_MalformedRange_ServesTheWholeEntity()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=100";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(Entity, BodyOf(context));
    }

    [Fact]
    public async Task ExecuteResultAsync_RangeOutsideTheEntity_Answers416WithTheEntityLength()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=5000-6000";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, context.Response.StatusCode);
        Assert.Equal($"bytes */{EntityLength}", context.Response.Headers.ContentRange);
        Assert.Empty(BodyOf(context));
    }

    /// <summary>
    /// The multipart envelope is measured in UTF-8 bytes before the body is written, so the promise and
    /// the payload have to agree exactly. A mismatch of one byte hangs or truncates the client.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_MultipleRanges_WritesExactlyTheLengthItPromised()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=0-99,500-599";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.StartsWith("multipart/byteranges; boundary=", context.Response.ContentType, StringComparison.Ordinal);
        Assert.Equal(context.Response.ContentLength, BodyOf(context).Length);
    }

    [Fact]
    public async Task ExecuteResultAsync_MultipleRanges_CarriesEveryPartAndItsRange()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=0-99,500-599";

        await ServeAsync(context);

        string body = Encoding.UTF8.GetString(BodyOf(context));

        Assert.Contains($"Content-Range: bytes 0-99/{EntityLength}", body, StringComparison.Ordinal);
        Assert.Contains($"Content-Range: bytes 500-599/{EntityLength}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteResultAsync_MoreRangesThanAllowed_ServesTheWholeEntity()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=0-1,2-3,4-5,6-7,8-9,10-11";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(EntityLength, context.Response.ContentLength);
    }

    [Fact]
    public async Task ExecuteResultAsync_HeadRequest_SendsTheHeadersWithoutABody()
    {
        var context = Requesting("HEAD");
        context.Request.Headers.Range = "bytes=100-199";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal(100, context.Response.ContentLength);
        Assert.Empty(BodyOf(context));
    }

    /// <summary>
    /// An unparseable conditional header is absent, not false. Read as DateTime.MinValue it made every
    /// such request fail its precondition permanently.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_UnparseableIfUnmodifiedSince_IsIgnored()
    {
        var context = Requesting();
        context.Request.Headers.IfUnmodifiedSince = "not a date at all";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExecuteResultAsync_StaleIfUnmodifiedSince_Answers412()
    {
        var context = Requesting();
        context.Request.Headers.IfUnmodifiedSince = LastModified.AddDays(-1).ToString("r");

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExecuteResultAsync_MatchingIfNoneMatch_Answers304()
    {
        var context = Requesting();
        context.Request.Headers.IfNoneMatch = await TagAsync();

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    /// <summary>
    /// RFC 7232 §3.2 gives a safe method 304 here. The previous code answered 412 for every client that
    /// revalidated its cache with a wildcard.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_WildcardIfNoneMatchOnGet_Answers304()
    {
        var context = Requesting();
        context.Request.Headers.IfNoneMatch = "*";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExecuteResultAsync_IfMatchListWithSpaces_MatchesTheSecondTag()
    {
        var context = Requesting();
        context.Request.Headers.IfMatch = $"\"nonsense\", {await TagAsync()}";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>
    /// The order of RFC 7232 §6: a stale If-Match is answered before the range is even looked at, so the
    /// client is told its copy is gone rather than told to fix its offsets.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_StaleIfMatchAndUnsatisfiableRange_Answers412()
    {
        var context = Requesting();
        context.Request.Headers.IfMatch = "\"stale\"";
        context.Request.Headers.Range = "bytes=5000-6000";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExecuteResultAsync_StaleIfRange_ServesTheWholeEntity()
    {
        var context = Requesting();
        context.Request.Headers.IfRange = "\"stale\"";
        context.Request.Headers.Range = "bytes=100-199";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(EntityLength, context.Response.ContentLength);
    }

    [Fact]
    public async Task ExecuteResultAsync_CurrentIfRange_ServesTheRange()
    {
        var context = Requesting();
        context.Request.Headers.IfRange = await TagAsync();
        context.Request.Headers.Range = "bytes=100-199";

        await ServeAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExecuteResultAsync_EntityLongerThanAllowed_Answers413()
    {
        var context = Requesting();

        await ServeAsync(context, new RangeRules { MaxFileLength = EntityLength - 1 });

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Empty(BodyOf(context));
    }

    [Fact]
    public async Task ExecuteResultAsync_RangeLongerThanAllowed_IsTruncatedAndSaysSo()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=0-999";

        await ServeAsync(context, new RangeRules { MaxRangeLength = 100 });

        Assert.Equal($"bytes 0-99/{EntityLength}", context.Response.Headers.ContentRange);
        Assert.Equal(100, context.Response.ContentLength);
    }

    /// <summary>
    /// The file name was demanded by the constructor and then never sent to anyone.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_FileNameGiven_SendsContentDisposition()
    {
        var context = Requesting();

        await ServeAsync(context);

        string disposition = context.Response.Headers.ContentDisposition.ToString();

        Assert.StartsWith("inline;", disposition, StringComparison.Ordinal);
        Assert.Contains("filename=sample.bin", disposition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteResultAsync_Always_SendsAValidatorTheClientCanResumeWith()
    {
        var context = Requesting();

        await ServeAsync(context);

        Assert.True(EntityTagHeaderValue.TryParse(context.Response.Headers.ETag.ToString(), out _));
        Assert.Equal(LastModified.ToString("r"), context.Response.Headers.LastModified);
    }

    /// <summary>
    /// Content-Length is already promised by the time the writer runs, so a swallowed failure would hand
    /// the client a truncated body under a success status. Both catch blocks this replaced did exactly that.
    /// </summary>
    [Fact]
    public async Task ExecuteResultAsync_WriterFails_AbortsRatherThanClaimingSuccess()
    {
        var context = Requesting();

        var failing = new RangeContent
        {
            Write = (_, _, _, _) => throw new IOException("the store went away"),
            Length = EntityLength,
            ContentType = EntityContentType,
            LastModified = LastModified
        };

        await Assert.ThrowsAsync<IOException>(
            () => new RangeFileResult(failing).ExecuteResultAsync(ActionContextOf(context)));
    }

    [Fact]
    public async Task ExecuteAsync_MinimalApiResult_ServesTheSameRange()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=100-199";

        await new RangeFileHttpResult(ContentOf(RangeRules.Default)).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal(Entity[100..200], BodyOf(context));
    }

    private static DefaultHttpContext Requesting(string method = "GET")
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };

        context.Request.Method = method;
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static Task ServeAsync(DefaultHttpContext context, RangeRules? rules = null)
        => new RangeFileResult(ContentOf(rules ?? RangeRules.Default)).ExecuteResultAsync(ActionContextOf(context));

    private static RangeContent ContentOf(RangeRules rules) => new()
    {
        Write = (body, offset, length, cancellationToken) =>
            body.WriteAsync(Entity.AsMemory(checked((int)offset), checked((int)length)), cancellationToken).AsTask(),
        Length = EntityLength,
        ContentType = EntityContentType,
        LastModified = LastModified,
        FileName = "sample.bin",
        Rules = rules
    };

    private static ActionContext ActionContextOf(HttpContext context)
        => new(context, new RouteData(), new ActionDescriptor());

    private static byte[] BodyOf(HttpContext context)
    {
        var body = (MemoryStream)context.Response.Body;

        return body.ToArray();
    }

    private static async Task<string> TagAsync()
    {
        var probe = Requesting();

        await ServeAsync(probe);

        return probe.Response.Headers.ETag.ToString();
    }
}
