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

        var validators = content.Validators;

        if (Preconditions.Evaluate(context.Request, validators) is { } precondition)
        {
            response.Headers.ETag = validators.TagText;
            response.StatusCode = precondition;
            Log.PreconditionAnswered(logger, precondition);

            return;
        }

        var served = Clamp(
            ByteRanges.Parse(RangeHeaderOf(context.Request, validators), content.Length, content.Rules.MaxRanges),
            content.Rules.MaxRangeLength);

        response.Headers.LastModified = validators.LastModifiedText;
        response.Headers.ETag = validators.TagText;
        response.Headers.AcceptRanges = "bytes";

        if (validators.Disposition is { } disposition)
            response.Headers.ContentDisposition = disposition;

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

        if (ranges.Count == 0)
        {
            response.ContentType = content.ContentType;
            response.ContentLength = content.Length;
            response.StatusCode = StatusCodes.Status200OK;

            await SendAsync(context, logger, content.Write, 0, content.Length);

            return;
        }

        if (ranges.Count == 1)
        {
            var only = ranges[0];

            response.ContentType = content.ContentType;
            response.Headers.ContentRange = $"bytes {only.From}-{only.To}/{content.Length}";
            response.ContentLength = only.Length;
            response.StatusCode = StatusCodes.Status206PartialContent;

            await SendAsync(context, logger, content.Write, only.From, only.Length);

            return;
        }

        using var multipart = new MultipartByteRanges(ranges, content.ContentType, content.Length);

        response.ContentType = multipart.ContentType;
        response.ContentLength = multipart.ContentLength;
        response.StatusCode = StatusCodes.Status206PartialContent;

        await SendAsync(context, logger, content.Write, 0, multipart.ContentLength, multipart);
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
    /// an error. The body is one stretch of the entity, or the multipart body when one is given; either
    /// way the writer and the stretch are passed rather than a closure over them, which was allocated on
    /// every request.
    /// </remarks>
    private static async Task SendAsync(
        HttpContext context,
        ILogger logger,
        WriteRange write,
        long offset,
        long length,
        MultipartByteRanges? multipart = null)
    {
        if (HttpMethods.IsHead(context.Request.Method))
            return;

        var body = context.Response.Body;
        var cancellationToken = context.RequestAborted;

        try
        {
            if (multipart is null)
                await write(body, offset, length, cancellationToken);
            else
                await multipart.WriteAsync(body, write, cancellationToken);
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

    /// <summary>
    /// The Range header, unless If-Range says the client is holding a different version of the entity.
    /// </summary>
    /// <remarks>
    /// If-Range is read as <c>GetTypedHeaders</c> reads it, the values joined and parsed, without the
    /// <c>RequestHeaders</c> wrapper built for one property. A condition that is this entity's tag or date as
    /// the ETag and Last-Modified headers sent them — what a client resuming a download sends back — is
    /// current without being parsed.
    /// </remarks>
    private static string? RangeHeaderOf(HttpRequest request, Validators validators)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return null;

        string? condition = request.Headers.IfRange;

        if (condition == validators.TagText || condition == validators.LastModifiedText)
            return request.Headers.Range;

        if (!RangeConditionHeaderValue.TryParse(condition, out var ifRange))
            return request.Headers.Range;

        bool isCurrent = ifRange.EntityTag is { } candidate
            ? candidate.Compare(validators.Tag, useStrongComparison: true)
            : ifRange.LastModified == validators.LastModified;

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
    /// fits. The truncation is a loop in a method of its own: a lambda over the limit made the compiler build
    /// its closure on entry here, for every request, the ones with no limit and no range among them.
    /// </remarks>
    private static ByteRangeSet Clamp(ByteRangeSet served, long? maxRangeLength)
    {
        if (maxRangeLength is not { } limit || served.Satisfaction is not RangeSatisfaction.Satisfiable)
            return served;

        if (!IsAnyLongerThan(served.Ranges, limit))
            return served;

        return served with { Ranges = Truncated(served.Ranges, limit) };
    }

    private static ByteRange[] Truncated(IReadOnlyList<ByteRange> ranges, long limit)
    {
        var truncated = new ByteRange[ranges.Count];

        for (int index = 0; index < truncated.Length; index++)
        {
            var range = ranges[index];
            truncated[index] = range with { To = Math.Min(range.To, range.From + limit - 1) };
        }

        return truncated;
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
}
