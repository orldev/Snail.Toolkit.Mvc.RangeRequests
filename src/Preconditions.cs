using Microsoft.AspNetCore.Http;
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
    /// </remarks>
    internal static int? Evaluate(HttpRequest request, EntityTagHeaderValue tag, DateTimeOffset lastModified)
    {
        var headers = request.GetTypedHeaders();
        bool isReadOnly = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

        if (headers.IfMatch.Count > 0)
            return Matches(headers.IfMatch, tag, useStrongComparison: true)
                ? null
                : StatusCodes.Status412PreconditionFailed;

        if (headers.IfUnmodifiedSince is { } unmodifiedSince && lastModified > unmodifiedSince)
            return StatusCodes.Status412PreconditionFailed;

        if (headers.IfNoneMatch.Count > 0)
        {
            if (!Matches(headers.IfNoneMatch, tag, useStrongComparison: false))
                return null;

            return isReadOnly ? StatusCodes.Status304NotModified : StatusCodes.Status412PreconditionFailed;
        }

        if (isReadOnly && headers.IfModifiedSince is { } modifiedSince && lastModified <= modifiedSince)
            return StatusCodes.Status304NotModified;

        return null;
    }

    /// <summary>
    /// Whether any candidate the client sent names this entity.
    /// </summary>
    /// <remarks>
    /// A loop rather than <c>Any</c> with a predicate: this runs on every conditional request, and the
    /// closure over the tag and the comparison mode is an allocation bought for nothing.
    /// </remarks>
    private static bool Matches(IList<EntityTagHeaderValue> candidates, EntityTagHeaderValue tag, bool useStrongComparison)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Tag.Equals("*", StringComparison.Ordinal) || candidate.Compare(tag, useStrongComparison))
                return true;
        }

        return false;
    }
}
