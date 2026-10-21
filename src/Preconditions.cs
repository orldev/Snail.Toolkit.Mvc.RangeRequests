using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Evaluates the conditional request headers of RFC 7232.
/// </summary>
internal static class Preconditions
{
    /// <summary>
    /// The status this request must answer with, or <see langword="null"/> when it may proceed.
    /// </summary>
    /// <remarks>
    /// The order is the one RFC 7232 §6 prescribes, and it is load-bearing: evaluating Range first answers
    /// a stale If-Match with 416 — "fix your offsets" — where the client needs 412, "your copy is gone".
    /// A header that does not parse is absent here, not false; read as <c>DateTime.MinValue</c>, an
    /// unparseable If-Unmodified-Since would answer 412 for as long as the client keeps sending it.
    /// The headers are parsed as <c>GetTypedHeaders</c> parses them, each once: its properties parse on every
    /// read, and the list headers were read twice, once to count them and once to match them.
    /// </remarks>
    internal static int? Evaluate(HttpRequest request, Validators validators)
    {
        var headers = request.Headers;
        var lastModified = validators.LastModified;
        bool isReadOnly = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

        if (MatchOf(headers.IfMatch, validators, useStrongComparison: true) is { } ifMatch)
            return ifMatch ? null : StatusCodes.Status412PreconditionFailed;

        if (DateOf(headers.IfUnmodifiedSince) is { } unmodifiedSince && lastModified > unmodifiedSince)
            return StatusCodes.Status412PreconditionFailed;

        if (MatchOf(headers.IfNoneMatch, validators, useStrongComparison: false) is { } ifNoneMatch)
        {
            if (!ifNoneMatch)
                return null;

            return isReadOnly ? StatusCodes.Status304NotModified : StatusCodes.Status412PreconditionFailed;
        }

        if (isReadOnly && DateOf(headers.IfModifiedSince) is { } modifiedSince && lastModified <= modifiedSince)
            return StatusCodes.Status304NotModified;

        return null;
    }

    /// <summary>
    /// Whether a list header of entity tags names this entity, or <see langword="null"/> when it names no tag at all.
    /// </summary>
    /// <remarks>
    /// A header that is the wildcard or this entity's own tag, as the ETag header sent it, matches without being
    /// parsed: it is what a cache revalidating its copy sends, and parsing it would only find the tag that is
    /// already known — the entity's tag is strong, so either comparison agrees.
    /// </remarks>
    private static bool? MatchOf(StringValues values, Validators validators, bool useStrongComparison)
    {
        if (values.Count == 1 && values[0] is { } only && (only == "*" || only == validators.TagText))
            return true;

        if (TagsOf(values) is not { Count: > 0 } candidates)
            return null;

        return Matches(candidates, validators.Tag, useStrongComparison);
    }

    /// <summary>
    /// The date a conditional header names, or <see langword="null"/> when it is absent or does not parse.
    /// </summary>
    /// <remarks>
    /// Read as <c>RequestHeaders</c> reads it: the values joined into one string, then parsed as an HTTP date.
    /// </remarks>
    private static DateTimeOffset? DateOf(StringValues values)
        => HeaderUtilities.TryParseDate((string?)values, out var date) ? date : null;

    /// <summary>
    /// The entity tags a list header names, or <see langword="null"/> when it is absent or names nothing readable.
    /// </summary>
    /// <remarks>
    /// An absent header is answered without parsing, which is every request that is not conditional, and
    /// without an empty list: <c>[]</c> typed as <see cref="IList{T}"/> is a new list on every call.
    /// </remarks>
    private static IList<EntityTagHeaderValue>? TagsOf(StringValues values)
    {
        if (StringValues.IsNullOrEmpty(values) || !EntityTagHeaderValue.TryParseList(values, out var tags))
            return null;

        return tags;
    }

    /// <summary>
    /// Whether any candidate the client sent names this entity.
    /// </summary>
    /// <remarks>
    /// An index loop rather than <c>Any</c> with a predicate or a <c>foreach</c> over the interface: this runs on
    /// every conditional request, and the closure over the tag, or the boxed enumerator, is an allocation
    /// bought for nothing.
    /// </remarks>
    private static bool Matches(IList<EntityTagHeaderValue> candidates, EntityTagHeaderValue tag, bool useStrongComparison)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];

            if (candidate.Tag.Equals("*", StringComparison.Ordinal) || candidate.Compare(tag, useStrongComparison))
                return true;
        }

        return false;
    }
}
