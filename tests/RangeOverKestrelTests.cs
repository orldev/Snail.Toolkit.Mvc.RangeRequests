using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

/// <summary>
/// The protocol as a real client sees it over a real server, rather than as a DefaultHttpContext records it.
/// </summary>
public class RangeOverKestrelTests
{
    private const long EntityLength = 4096;
    private const string EntityContentType = "application/octet-stream";

    private static readonly DateTimeOffset LastModified = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Entity = [.. Enumerable.Range(0, (int)EntityLength).Select(index => (byte)index)];

    [Fact]
    public async Task Get_NoRange_ServesTheWholeEntity()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var response = await server.Client.GetAsync("/entity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EntityLength, response.Content.Headers.ContentLength);
        Assert.Equal(Entity, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Get_SingleRange_ServesExactlyThoseBytes()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var response = await server.Client.SendAsync(Asking("bytes=100-199"));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(100, 199, EntityLength), response.Content.Headers.ContentRange);
        Assert.Equal(Entity[100..200], await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// Kestrel resets the connection when a response writes fewer bytes than its Content-Length promised, so
    /// the multipart envelope has to be measured exactly. A MemoryStream body cannot catch a mismatch here.
    /// </summary>
    [Fact]
    public async Task Get_MultipleRanges_DeliversAMultipartBodyOfThePromisedLength()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var response = await server.Client.SendAsync(Asking("bytes=0-99,2000-2099"));
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("multipart/byteranges", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(response.Content.Headers.ContentLength, body.Length);
    }

    [Fact]
    public async Task Get_MultipleRanges_CarriesEveryRequestedPart()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var response = await server.Client.SendAsync(Asking("bytes=0-99,2000-2099"));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"Content-Range: bytes 0-99/{EntityLength}", body, StringComparison.Ordinal);
        Assert.Contains($"Content-Range: bytes 2000-2099/{EntityLength}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_MalformedRange_ServesTheWholeEntity()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var response = await server.Client.SendAsync(Asking("bytes=100"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EntityLength, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Get_RangeOutsideTheEntity_Answers416WithTheEntityLength()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var response = await server.Client.SendAsync(Asking("bytes=99999-100999"));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal(EntityLength, response.Content.Headers.ContentRange?.Length);
    }

    [Fact]
    public async Task Head_Range_SendsTheHeadersWithoutABody()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var request = new HttpRequestMessage(HttpMethod.Head, "/entity");
        request.Headers.Range = RangeHeaderValue.Parse("bytes=100-199");

        var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Get_MatchingIfNoneMatch_Answers304()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var first = await server.Client.GetAsync("/entity");

        var request = new HttpRequestMessage(HttpMethod.Get, "/entity");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        var second = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Get_CurrentIfRange_ServesTheRangeRatherThanTheWholeEntity()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        var first = await server.Client.GetAsync("/entity");

        var request = new HttpRequestMessage(HttpMethod.Get, "/entity");
        request.Headers.Range = RangeHeaderValue.Parse("bytes=100-199");
        request.Headers.IfRange = new RangeConditionHeaderValue(first.Headers.ETag!);

        var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
    }

    /// <summary>
    /// The failure the two swallowed catch blocks used to hide: Content-Length is already on the wire, so a
    /// writer that dies must break the connection rather than let a short body pass as a complete one.
    /// </summary>
    /// <remarks>
    /// Measured against a real socket, the client sees "Connection reset by peer" — the abort reaching the
    /// wire. Under a MemoryStream body this path proves nothing, because there is no connection to reset.
    /// </remarks>
    [Fact]
    public async Task Get_WriterFailsMidBody_BreaksTheConnectionInsteadOfTruncatingSilently()
    {
        var failing = Serving() with
        {
            Write = async (body, _, _, cancellationToken) =>
            {
                await body.WriteAsync(Entity.AsMemory(0, 512), cancellationToken);

                throw new IOException("the store went away");
            }
        };

        await using var server = await RangeServer.StartAsync(failing);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ReadFullyAsync(server));
    }

    /// <summary>
    /// The risk a MemoryStream body cannot show: a caller whose delegate writes fewer bytes than the Length
    /// it declared.
    /// </summary>
    /// <remarks>
    /// Measured: the client raises "The response ended prematurely (ResponseEnded)" against the promised
    /// Content-Length. The short body is rejected rather than handed over as a complete file, which is the
    /// guarantee that matters; a MemoryStream accepts it silently and the assertion would pass on nothing.
    /// </remarks>
    [Fact]
    public async Task Get_WriterUnderWritesItsDeclaredLength_IsCaughtRatherThanServed()
    {
        var shortWriting = Serving() with
        {
            Write = (body, _, _, cancellationToken) =>
                body.WriteAsync(Entity.AsMemory(0, 512), cancellationToken).AsTask()
        };

        await using var server = await RangeServer.StartAsync(shortWriting);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ReadFullyAsync(server));
    }

    private static async Task ReadFullyAsync(RangeServer server)
    {
        using var response = await server.Client.GetAsync("/entity", HttpCompletionOption.ResponseHeadersRead);

        await response.Content.ReadAsByteArrayAsync();
    }

    private static RangeContent Serving() => new()
    {
        Write = (body, offset, length, cancellationToken) =>
            body.WriteAsync(Entity.AsMemory(checked((int)offset), checked((int)length)), cancellationToken).AsTask(),
        Length = EntityLength,
        ContentType = EntityContentType,
        LastModified = LastModified,
        FileName = "sample.bin"
    };

    private static HttpRequestMessage Asking(string range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/entity");

        request.Headers.TryAddWithoutValidation("Range", range);

        return request;
    }
}
