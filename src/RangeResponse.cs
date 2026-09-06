using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Answers one request for an entity that can be written range by range.
/// </summary>
internal static class RangeResponse
{
    /// <summary>
    /// Runs the whole exchange: preconditions, ranges, headers, body.
    /// </summary>
    /// <remarks>
    /// The category is the result the caller actually used, so an operator filtering on
    /// <see cref="RangeFileHttpResult"/> sees Minimal API traffic rather than nothing. It is resolved as
    /// <see cref="ILogger{TCategoryName}"/> rather than through <c>ILoggerFactory.CreateLogger</c>, which
    /// takes a lock on every call and would put a contention point on the hot path of a media server.
    /// </remarks>
    internal static async Task WriteAsync<TResult>(HttpContext context, RangeContent content)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<TResult>>();
        var response = context.Response;

        if (content.Rules.MaxFileLength is { } maxFileLength && content.Length > maxFileLength)
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            Log.EntityTooLarge(logger, content.Length, maxFileLength);

            return;
        }

        var lastModified = ToWholeSecond(content.LastModified);
        var tag = content.EntityTag ?? EntityTags.For(lastModified, content.Length);

        if (Preconditions.Evaluate(context.Request, tag, lastModified) is { } precondition)
        {
            response.Headers.ETag = tag.ToString();
            response.StatusCode = precondition;
            Log.PreconditionAnswered(logger, precondition);

            return;
        }

        var served = Clamp(
            ByteRanges.Parse(RangeHeaderOf(context.Request, tag, lastModified), content.Length, content.Rules.MaxRanges),
            content.Rules.MaxRangeLength);

        response.Headers.LastModified = lastModified.ToString("r", CultureInfo.InvariantCulture);
        response.Headers.ETag = tag.ToString();
        response.Headers.AcceptRanges = "bytes";

        if (content.FileName is { Length: > 0 } fileName)
            response.Headers.ContentDisposition = DispositionOf(fileName);

        if (served.Satisfaction is RangeSatisfaction.Unsatisfiable)
        {
            response.Headers.ContentRange = $"bytes */{content.Length}";
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            Log.RangeNotSatisfiable(logger, content.Length);

            return;
        }

        await ServeAsync(context, content, served.Ranges, logger);
    }

    private static async Task ServeAsync(
        HttpContext context,
        RangeContent content,
        IReadOnlyList<ByteRange> ranges,
        ILogger logger)
    {
        var response = context.Response;
        var cancellationToken = context.RequestAborted;

        if (ranges.Count == 0)
        {
            response.ContentType = content.ContentType;
            response.ContentLength = content.Length;
            response.StatusCode = StatusCodes.Status200OK;

            await SendAsync(context, logger,
                () => content.Write(response.Body, 0, content.Length, cancellationToken));

            return;
        }

        if (ranges.Count == 1)
        {
            var only = ranges[0];

            response.ContentType = content.ContentType;
            response.Headers.ContentRange = $"bytes {only.From}-{only.To}/{content.Length}";
            response.ContentLength = only.Length;
            response.StatusCode = StatusCodes.Status206PartialContent;

            await SendAsync(context, logger,
                () => content.Write(response.Body, only.From, only.Length, cancellationToken));

            return;
        }

        var multipart = new MultipartByteRanges(ranges, content.ContentType, content.Length);

        response.ContentType = multipart.ContentType;
        response.ContentLength = multipart.ContentLength;
        response.StatusCode = StatusCodes.Status206PartialContent;

        await SendAsync(context, logger,
            () => WriteMultipartAsync(context, multipart, ranges, content.Write, cancellationToken));
    }

    /// <summary>
    /// Writes the body unless the method forbids one, and never lets a failure look like success.
    /// </summary>
    /// <remarks>
    /// Content-Length has already been promised by the time this runs, so swallowing an exception here
    /// hands the client a truncated body under a 200 or a 206. Aborting is the only way left to say "this
    /// response is not what it claims". A client that left is told apart by the state of its own token
    /// rather than by the type it surfaced: Kestrel raises ConnectionAbortedException, but a writer
    /// wrapping a network stream raises IOException, and keying on the type logs a routine disconnect as
    /// an error.
    /// </remarks>
    private static async Task SendAsync(HttpContext context, ILogger logger, Func<Task> body)
    {
        if (HttpMethods.IsHead(context.Request.Method))
            return;

        try
        {
            await body();
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            Log.ClientLeft(logger);
        }
        catch (Exception exception)
        {
            Log.BodyFailed(logger, exception);
            context.Abort();

            throw;
        }
    }

    private static async Task WriteMultipartAsync(
        HttpContext context,
        MultipartByteRanges multipart,
        IReadOnlyList<ByteRange> ranges,
        WriteRange write,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < ranges.Count; index++)
        {
            await multipart.WritePartHeaderAsync(context.Response, index, cancellationToken);
            await write(context.Response.Body, ranges[index].From, ranges[index].Length, cancellationToken);
            await MultipartByteRanges.WritePartTrailerAsync(context.Response, cancellationToken);
        }

        await multipart.WriteEpilogueAsync(context.Response, cancellationToken);
    }

    /// <summary>
    /// The Range header, unless If-Range says the client is holding a different version of the entity.
    /// </summary>
    private static string? RangeHeaderOf(HttpRequest request, EntityTagHeaderValue tag, DateTimeOffset lastModified)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return null;

        if (request.GetTypedHeaders().IfRange is not { } ifRange)
            return request.Headers.Range;

        bool isCurrent = ifRange.EntityTag is { } candidate
            ? candidate.Compare(tag, useStrongComparison: true)
            : ifRange.LastModified == lastModified;

        if (!isCurrent)
            return null;

        return request.Headers.Range;
    }

    /// <summary>
    /// Truncates ranges to the longest the rules allow, which RFC 7233 §4.1 permits.
    /// </summary>
    /// <remarks>
    /// A request whose ranges already fit keeps the list it arrived with: rebuilding it unconditionally
    /// allocated a second list and a boxed enumerator on every ranged request, and almost every request
    /// fits.
    /// </remarks>
    private static ByteRangeSet Clamp(ByteRangeSet served, long? maxRangeLength)
    {
        if (maxRangeLength is not { } limit || served.Satisfaction is not RangeSatisfaction.Satisfiable)
            return served;

        if (!IsAnyLongerThan(served.Ranges, limit))
            return served;

        return served with
        {
            Ranges = [.. served.Ranges.Select(range => range with { To = Math.Min(range.To, range.From + limit - 1) })]
        };
    }

    private static bool IsAnyLongerThan(IReadOnlyList<ByteRange> ranges, long limit)
    {
        foreach (var range in ranges)
        {
            if (range.Length > limit)
                return true;
        }

        return false;
    }

    private static string DispositionOf(string fileName)
    {
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(fileName);

        return disposition.ToString();
    }

    private static DateTimeOffset ToWholeSecond(DateTimeOffset moment)
        => DateTimeOffset.FromUnixTimeSeconds(moment.ToUnixTimeSeconds());
}
