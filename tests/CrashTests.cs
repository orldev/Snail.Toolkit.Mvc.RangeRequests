using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

/// <summary>
/// Attacks on the library rather than exercises of it: malformed callers, hostile metadata, floods and
/// parallelism. Every test here states the failure it is meant to reproduce.
/// </summary>
public class CrashTests
{
    private const long EntityLength = 4096;

    private static readonly DateTimeOffset LastModified = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Entity = [.. Enumerable.Range(0, (int)EntityLength).Select(index => (byte)index)];

    /// <summary>
    /// Breaks: nothing validates the rules. A zero or negative cap makes Clamp compute To = From + limit - 1,
    /// which lands before From, so the entity is described by a range that ends before it starts.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RangeRules_NonPositiveMaxRangeLength_IsRefused(long limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RangeRules { MaxRangeLength = limit });

    /// <summary>
    /// Breaks: MaxRanges below one silently disables range serving altogether, because the count check is
    /// "greater than", and no caller is told.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RangeRules_NonPositiveMaxRanges_IsRefused(int limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RangeRules { MaxRanges = limit });

    /// <summary>
    /// Breaks: Length is taken on trust. A negative one reaches response.ContentLength and the caller's own
    /// delegate as a negative count.
    /// </summary>
    [Fact]
    public void RangeContent_NegativeLength_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Serving() with { Length = -1 });

    /// <summary>
    /// Breaks: ContentType was required but not checked. A blank one produced a bare "Content-Type:" line
    /// inside every multipart part, and one carrying CRLF forged part headers in the response body, where
    /// Kestrel's header validation does not reach.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("text/plain\r\nX-Injected: yes")]
    [InlineData("not a media type at all")]
    public void RangeContent_ContentTypeThatIsNotAMediaType_IsRefused(string contentType)
        => Assert.Throws<ArgumentException>(() => Serving() with { ContentType = contentType });

    /// <summary>
    /// Breaks: a limit of zero or less refuses every entity, which is the opposite of what a limit means.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RangeRules_NonPositiveMaxFileLength_IsRefused(long limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RangeRules { MaxFileLength = limit });

    /// <summary>
    /// Breaks: default(ByteRangeSet) bypasses the primary constructor, so Ranges is null behind a
    /// non-nullable reference type and the nullable analysis never warns.
    /// </summary>
    [Fact]
    public void ByteRangeSet_Default_CarriesNoRangesRatherThanNull()
    {
        ByteRangeSet uninitialised = default;

        Assert.Empty(uninitialised.Ranges);
    }

    /// <summary>
    /// Breaks: MaxRanges is enforced after RangeHeaderValue.TryParse has already materialised every range in
    /// the header. Kestrel caps a header at 32 KB, so one request buys roughly eight thousand objects the
    /// server then throws away.
    /// </summary>
    [Fact]
    public void Parse_HeaderWithFarMoreRangesThanAllowed_DoesNotMaterialiseThemAll()
    {
        string flood = $"bytes={string.Join(',', Enumerable.Range(0, 8000).Select(index => $"{index}-{index}"))}";

        long before = GC.GetAllocatedBytesForCurrentThread();
        ByteRanges.Parse(flood, EntityLength, maxRanges: 5);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024, $"rejecting an over-long byte-range-set allocated {allocated} bytes");
    }

    /// <summary>
    /// A media type that survives validation reaches the multipart body in its canonical form, parameters
    /// and all, and carries nothing that could end the line early.
    /// </summary>
    [Fact]
    public async Task Get_ParameterisedContentType_ReachesTheMultipartBodyIntact()
    {
        var content = Serving() with { ContentType = "text/plain; charset=utf-8" };

        await using var server = await RangeServer.StartAsync(content);

        var request = new HttpRequestMessage(HttpMethod.Get, "/entity");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9,100-109");

        var response = await server.Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Content-Type: text/plain; charset=utf-8\r\n", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks: clamping used to be able to move the end of a range before its start. The smallest limit a
    /// caller can now state still has to describe one real byte.
    /// </summary>
    [Fact]
    public async Task Get_ClampedToTheSmallestAllowedLength_StillDescribesOneByte()
    {
        var content = Serving() with { Rules = RangeRules.Default with { MaxRangeLength = 1 } };

        await using var server = await RangeServer.StartAsync(content);

        var request = new HttpRequestMessage(HttpMethod.Get, "/entity");
        request.Headers.TryAddWithoutValidation("Range", "bytes=100-199");

        var response = await server.Client.SendAsync(request);

        Assert.Equal($"bytes 100-100/{EntityLength}", string.Concat(response.Content.Headers.GetValues("Content-Range")));
        Assert.Equal(1, response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// Breaks: the client left, so RequestAborted is signalled, but the writer surfaced an IOException
    /// rather than an OperationCanceledException. A catch filter keyed on the exception type logged that
    /// routine disconnect at Error — the very noise the rewrite was meant to remove.
    /// </summary>
    /// <remarks>
    /// The writer waits for the token before it fails, which is the real order of events: Kestrel signals
    /// the abort, and only then does the next write report it. Aborting from inside the writer instead
    /// races the token, because Kestrel fires it on a queued callback rather than inline.
    /// </remarks>
    [Fact]
    public async Task Get_ClientLeavesAndTheWriterReportsIo_IsNotLoggedAsAnError()
    {
        var logs = new RecordingLogs();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await RangeServer.StartAsync(
            _ => Serving() with
            {
                Write = async (_, _, _, cancellationToken) =>
                {
                    var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    await using var registration = cancellationToken.Register(left.SetResult);
                    writing.TrySetResult();
                    await left.Task;

                    throw new IOException("the connection went away under the writer");
                }
            },
            logs);

        using var leaving = new CancellationTokenSource();
        var abandoned = server.Client.GetAsync("/entity", leaving.Token);

        await writing.Task;
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        Assert.True(await IsLoggedAsync(logs, "The client left"), "the disconnect was never recognised");
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("Writing the body failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A guard rather than a reproduction: the protocol is stateless now, and this fails the moment anything
    /// per-request creeps back into a field.
    /// </summary>
    [Fact]
    public void Parse_UnderParallelLoad_AnswersEachCallerItsOwnRanges()
    {
        Parallel.For(0, 2000, index =>
        {
            long from = index % 1000;

            var parsed = ByteRanges.Parse($"bytes={from}-{from + 10}", EntityLength, maxRanges: 5);

            Assert.Equal(new ByteRange(from, from + 10), Assert.Single(parsed.Ranges));
        });
    }

    /// <summary>
    /// A guard rather than a reproduction: one shared RangeContent answering many requests at once must give
    /// every caller exactly the bytes it asked for.
    /// </summary>
    [Fact]
    public async Task Get_ManyConcurrentRangeRequests_EachReceivesItsOwnBytes()
    {
        await using var server = await RangeServer.StartAsync(Serving());

        await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
        {
            int from = index * 16;

            var request = new HttpRequestMessage(HttpMethod.Get, "/entity");
            request.Headers.TryAddWithoutValidation("Range", $"bytes={from}-{from + 15}");

            var response = await server.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(Entity[from..(from + 16)], await response.Content.ReadAsByteArrayAsync());
        }));
    }

    /// <summary>
    /// A guard rather than a reproduction: extremes of the header grammar must not throw out of the parser.
    /// </summary>
    [Theory]
    [InlineData("bytes=-9223372036854775807")]
    [InlineData("bytes=9223372036854775807-")]
    [InlineData("bytes=0-9223372036854775807")]
    [InlineData("bytes=9223372036854775807-9223372036854775807")]
    [InlineData("bytes=,,,,")]
    [InlineData("bytes=-")]
    [InlineData("bytes=🙂-🙂")]
    [InlineData("BYTES=0-1")]
    public void Parse_ExtremeHeader_AnswersWithoutThrowing(string header)
    {
        var parsed = ByteRanges.Parse(header, EntityLength, maxRanges: 5);

        Assert.NotNull(parsed.Ranges);
    }

    /// <summary>
    /// Breaks: the canonical form was whatever <c>MediaTypeHeaderValue</c> parsed, and its quoted-string reader
    /// looks only for the closing quote, so CR and LF inside a parameter survived into the stored type and were
    /// written into every multipart part as a forged header line.
    /// </summary>
    [Theory]
    [InlineData("text/plain; charset=\"utf-8\r\nX-Injected: yes\"")]
    [InlineData("text/plain; name=\"a\rb\"")]
    [InlineData("text/plain; name=\"a\nb\"")]
    public void RangeContent_ContentTypeWithCrlfInsideAQuotedParameter_IsRefused(string contentType)
        => Assert.Throws<ArgumentException>(() => Serving() with { ContentType = contentType });

    /// <summary>
    /// Breaks: <c>EntityTagHeaderValue</c> reads its quoted string the same way, so a tag carrying CRLF between
    /// the quotes was stored and refused by Kestrel on every request for the entity, as a 500.
    /// </summary>
    [Fact]
    public void RangeContent_ETagWithCrlfInsideTheQuotes_IsRefused()
        => Assert.Throws<ArgumentException>(() => Serving() with { ETag = "\"v1\r\nX-Injected: yes\"" });

    /// <summary>
    /// FileName is the one member taken on trust, and it is safe to be: the framework encodes a name a header
    /// cannot carry as RFC 5987 asks, so it reaches the client percent-encoded rather than as a header line.
    /// </summary>
    [Fact]
    public async Task Get_FileNameWithCrlf_IsEncodedRatherThanRefused()
    {
        await using var server = await RangeServer.StartAsync(Serving() with { FileName = "a\r\nX-Injected: yes.bin" });

        var response = await server.Client.GetAsync("/entity");
        string disposition = string.Join(' ', response.Content.Headers.GetValues("Content-Disposition"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("filename*=UTF-8''a%0D%0A", disposition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaks: <see cref="RangeRules.MaxRanges"/> refused a cap below one, but the public parser behind it did
    /// not — a cap of zero made every header longer than allowed and silently disabled range serving.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Parse_NonPositiveMaxRanges_IsRefused(int maxRanges)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ByteRanges.Parse("bytes=0-9", EntityLength, maxRanges));

    /// <summary>
    /// A guard rather than a reproduction: offsets are long end to end, so the last byte of the longest possible
    /// entity is described exactly.
    /// </summary>
    [Fact]
    public async Task Get_SuffixRangeOfTheLongestEntity_DescribesItsLastByte()
    {
        var context = Requesting();
        context.Request.Headers.Range = "bytes=-1";

        await new RangeFileHttpResult(Longest()).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal($"bytes {long.MaxValue - 1}-{long.MaxValue - 1}/{long.MaxValue}", context.Response.Headers.ContentRange);
        Assert.Equal(1, context.Response.ContentLength);
    }

    /// <summary>
    /// A guard rather than a reproduction: the multipart framing measures room for twenty-digit numbers, and a
    /// nineteen-digit offset takes the framework's parser rather than the plain one; the promised length must
    /// still be the written one.
    /// </summary>
    [Fact]
    public async Task Get_TwoRangesOfTheLongestEntity_WritesExactlyTheLengthItPromised()
    {
        var context = Requesting();
        context.Request.Headers.Range = $"bytes=0-0,{long.MaxValue - 1}-";

        await new RangeFileHttpResult(Longest()).ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal(context.Response.ContentLength, ((MemoryStream)context.Response.Body).Length);
    }

    /// <summary>
    /// A guard rather than a reproduction: a writer that writes one byte more than its range must not hand the
    /// client a body under a Content-Length it does not match. Kestrel refuses the excess and the connection is
    /// aborted.
    /// </summary>
    [Fact]
    public async Task Get_WriterOverWritesItsRange_IsCaughtRatherThanServed()
    {
        await using var server = await RangeServer.StartAsync(OverWriting());

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ReadFullyAsync(server, "bytes=0-99"));
    }

    /// <summary>
    /// A guard rather than a reproduction: in a multipart body the excess of one part is only noticed when the
    /// closing delimiter overflows the promise, after the other parts have gone out; the client must still not
    /// take the body as complete.
    /// </summary>
    [Fact]
    public async Task Get_WriterOverWritesOnePartOfAMultipartBody_IsCaughtRatherThanServed()
    {
        await using var server = await RangeServer.StartAsync(OverWriting());

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => ReadFullyAsync(server, "bytes=0-9,100-109"));
    }

    /// <summary>
    /// A guard rather than a reproduction: a request whose token is already signalled when the result runs is a
    /// client that left, not a failed body — nothing is thrown and nothing is logged at Error.
    /// </summary>
    [Fact]
    public async Task Get_RequestAbortedBeforeTheBody_IsLoggedAsTheClientLeavingAndNotThrown()
    {
        var logs = new RecordingLogs();
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var context = Requesting(logs);
        context.RequestAborted = aborted.Token;
        var honouring = Serving() with { Write = (_, _, _, cancellationToken) => Task.FromCanceled(cancellationToken) };

        await new RangeFileHttpResult(honouring).ExecuteAsync(context);

        Assert.Contains(logs.Entries, entry => entry.Message.Contains("The client left", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// A guard rather than a reproduction: the validators are worked out on the first request and kept, so
    /// sixty-four first requests at once must all answer with the one tag a sequential request would.
    /// </summary>
    [Fact]
    public async Task Get_FirstRequestsInParallelOnAFreshContent_AllSendTheSameTag()
    {
        var content = Serving();
        var expected = Requesting();
        await new RangeFileHttpResult(Serving()).ExecuteAsync(expected);

        DefaultHttpContext[] contexts = [.. Enumerable.Range(0, 64).Select(_ => Requesting())];

        await Task.WhenAll(contexts.Select(context => Task.Run(() => new RangeFileHttpResult(content).ExecuteAsync(context))));

        Assert.All(contexts, context => Assert.Equal(expected.Response.Headers.ETag, context.Response.Headers.ETag));
    }

    private static async Task<bool> IsLoggedAsync(RecordingLogs logs, string fragment)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            if (logs.Entries.Any(entry => entry.Message.Contains(fragment, StringComparison.Ordinal)))
                return true;

            await Task.Delay(10);
        }

        return false;
    }

    private static async Task ReadFullyAsync(RangeServer server, string range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/entity");
        request.Headers.TryAddWithoutValidation("Range", range);

        using var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await response.Content.ReadAsByteArrayAsync();
    }

    private static DefaultHttpContext Requesting(RecordingLogs? logs = null)
    {
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            if (logs is not null)
                logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace);
        });

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static RangeContent OverWriting() => Serving() with
    {
        Write = (body, offset, length, cancellationToken) =>
            body.WriteAsync(Entity.AsMemory(checked((int)offset), checked((int)length) + 1), cancellationToken).AsTask()
    };

    private static RangeContent Longest() => Serving() with
    {
        Write = (body, _, length, cancellationToken) => body.WriteAsync(new byte[length], cancellationToken).AsTask(),
        Length = long.MaxValue
    };

    private static RangeContent Serving() => new()
    {
        Write = (body, offset, length, cancellationToken) =>
            body.WriteAsync(Entity.AsMemory(checked((int)offset), checked((int)length)), cancellationToken).AsTask(),
        Length = EntityLength,
        ContentType = "application/octet-stream",
        LastModified = LastModified,
        FileName = "sample.bin"
    };
}
