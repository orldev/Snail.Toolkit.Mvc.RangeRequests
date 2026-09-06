using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

/// <summary>
/// The validator a client resumes and revalidates against, whether the store named it or not.
/// </summary>
public class EntityTagTests
{
    private const long EntityLength = 1000;
    private const string StoredTag = "\"9f2c-revision-4\"";

    private static readonly DateTimeOffset LastModified = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Serving_ContentThatNamesItsOwnTag_SendsThatTag()
    {
        var context = Requesting();

        await ServeAsync(context, StoredTag);

        Assert.Equal(StoredTag, context.Response.Headers.ETag);
    }

    [Fact]
    public async Task Serving_ContentThatNamesItsOwnTag_ResumesAgainstIt()
    {
        var context = Requesting();
        context.Request.Headers.IfRange = StoredTag;
        context.Request.Headers.Range = "bytes=100-199";

        await ServeAsync(context, StoredTag);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal($"bytes 100-199/{EntityLength}", context.Response.Headers.ContentRange);
    }

    [Fact]
    public async Task Serving_ContentThatNamesItsOwnTag_AnswersAMatchingIfNoneMatchWith304()
    {
        var context = Requesting();
        context.Request.Headers.IfNoneMatch = StoredTag;

        await ServeAsync(context, StoredTag);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    /// <summary>
    /// A tag the store owns survives a change of modification date, which is the whole point of naming one:
    /// a derived tag would move with the date and invalidate every cached copy for nothing.
    /// </summary>
    [Fact]
    public async Task Serving_ContentThatNamesItsOwnTag_KeepsItWhenTheDateMoves()
    {
        var earlier = Requesting();
        var later = Requesting();

        await ServeAsync(earlier, StoredTag);
        await ServeAsync(later, StoredTag, LastModified.AddHours(3));

        Assert.Equal(earlier.Response.Headers.ETag, later.Response.Headers.ETag);
    }

    [Fact]
    public async Task Serving_ContentThatNamesNoTag_DerivesOneThatMovesWithTheDate()
    {
        var earlier = Requesting();
        var later = Requesting();

        await ServeAsync(earlier, tag: null);
        await ServeAsync(later, tag: null, LastModified.AddHours(3));

        Assert.NotEqual(earlier.Response.Headers.ETag, later.Response.Headers.ETag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unquoted")]
    [InlineData("\"unterminated")]
    [InlineData("\"forged\"\r\nX-Injected: yes")]
    public void RangeContent_ETagThatIsNotAnEntityTag_IsRefused(string tag)
        => Assert.Throws<ArgumentException>(() => ContentOf(tag));

    /// <summary>
    /// RFC 7232 §2.1 bars a weak validator from the strong comparison If-Range requires, so accepting one
    /// would answer every resumed download with the entity whole.
    /// </summary>
    [Fact]
    public void RangeContent_WeakETag_IsRefused()
        => Assert.Throws<ArgumentException>(() => ContentOf("W/\"9f2c\""));

    [Fact]
    public void RangeContent_ETagWithSurroundingSpace_IsStoredCanonically()
        => Assert.Equal(StoredTag, ContentOf($" {StoredTag} ").ETag);

    [Fact]
    public void RangeContent_NoETag_AnswersNull()
        => Assert.Null(ContentOf(tag: null).ETag);

    private static DefaultHttpContext Requesting()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };

        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static Task ServeAsync(DefaultHttpContext context, string? tag, DateTimeOffset? lastModified = null)
        => new RangeFileResult(ContentOf(tag, lastModified)).ExecuteResultAsync(
            new ActionContext(context, new RouteData(), new ActionDescriptor()));

    private static RangeContent ContentOf(string? tag, DateTimeOffset? lastModified = null) => new()
    {
        Write = (body, _, length, cancellationToken) =>
            body.WriteAsync(new byte[length], cancellationToken).AsTask(),
        Length = EntityLength,
        ContentType = "application/octet-stream",
        LastModified = lastModified ?? LastModified,
        ETag = tag
    };
}
