using Microsoft.Net.Http.Headers;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Reads the byte-range-set of a Range header against an entity of known length.
/// </summary>
public static class ByteRanges
{
    /// <summary>
    /// Measures what the client asked for against an entity of <paramref name="entityLength"/> bytes.
    /// </summary>
    /// <remarks>
    /// An unreadable header is ignored rather than rejected, as RFC 7233 §3.1 requires, and the framework's
    /// own <see cref="RangeHeaderValue"/> decides what unreadable means: a parser that splits on characters
    /// and parses with <c>int.Parse</c> answers <c>bytes=100</c> with an exception and
    /// <c>bytes=0-3000000000</c> with an overflow, turning a client's bad header into the server's 500.
    /// Overlapping and adjacent ranges are coalesced, which RFC 7233 §6.1 recommends: without it a client
    /// can ask for the same bytes many times in one request.
    /// </remarks>
    public static ByteRangeSet Parse(string? header, long entityLength, int maxRanges)
    {
        if (entityLength <= 0 || header is null)
            return ByteRangeSet.Ignored;

        if (IsLongerThanAllowed(header, maxRanges) || !RangeHeaderValue.TryParse(header, out var requested))
            return ByteRangeSet.Ignored;

        if (!requested.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase))
            return ByteRangeSet.Ignored;

        List<ByteRange> satisfiable = [];

        foreach (var range in requested.Ranges)
        {
            if (Measure(range, entityLength) is { } measured)
                satisfiable.Add(measured);
        }

        return satisfiable.Count == 0
            ? ByteRangeSet.Unsatisfiable
            : new ByteRangeSet(RangeSatisfaction.Satisfiable, Coalesce(satisfiable));
    }

    /// <summary>
    /// Whether the header names more ranges than are allowed, decided before any of them is materialised.
    /// </summary>
    /// <remarks>
    /// A byte-range-set holds no commas of its own, so one more than the separator count is the number of
    /// ranges. Counting them costs a scan of at most the 32 KB Kestrel allows a header and allocates
    /// nothing; parsing first and checking the count afterwards costs a measured 515 456 bytes per request
    /// that is then thrown away — which is what a flood of ranges is sent to buy.
    /// </remarks>
    private static bool IsLongerThanAllowed(string header, int maxRanges)
        => header.AsSpan().Count(',') >= maxRanges;

    private static ByteRange? Measure(RangeItemHeaderValue requested, long entityLength)
    {
        long last = entityLength - 1;

        if (requested.From is not { } from)
        {
            long suffix = requested.To ?? 0;

            return suffix <= 0 ? null : new ByteRange(Math.Max(0, entityLength - suffix), last);
        }

        if (from > last)
            return null;

        long to = Math.Min(requested.To ?? last, last);

        return to < from ? null : new ByteRange(from, to);
    }

    private static List<ByteRange> Coalesce(List<ByteRange> satisfiable)
    {
        satisfiable.Sort(static (left, right) => left.From.CompareTo(right.From));

        List<ByteRange> merged = [satisfiable[0]];

        for (int index = 1; index < satisfiable.Count; index++)
        {
            var range = satisfiable[index];
            var previous = merged[^1];

            if (range.From > previous.To + 1)
                merged.Add(range);
            else
                merged[^1] = previous with { To = Math.Max(previous.To, range.To) };
        }

        return merged;
    }
}
