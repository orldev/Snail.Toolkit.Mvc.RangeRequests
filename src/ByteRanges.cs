using Microsoft.Net.Http.Headers;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Reads the byte-range-set of a Range header against an entity of known length.
/// </summary>
public static class ByteRanges
{
    private const string PlainUnit = "bytes=";

    /// <summary>
    /// Measures what the client asked for against an entity of <paramref name="entityLength"/> bytes.
    /// </summary>
    /// <remarks>
    /// An unreadable header is ignored rather than rejected, as RFC 7233 §3.1 requires, and the framework's
    /// own <see cref="RangeHeaderValue"/> decides what unreadable means: a parser that splits on characters
    /// and parses with <c>int.Parse</c> answers <c>bytes=100</c> with an exception and
    /// <c>bytes=0-3000000000</c> with an overflow, turning a client's bad header into the server's 500.
    /// Overlapping and adjacent ranges are coalesced, which RFC 7233 §6.1 recommends: without it a client
    /// can ask for the same bytes many times in one request. A cap below one is refused as
    /// <see cref="RangeRules.MaxRanges"/> refuses it: taken as given, it made every header longer than allowed
    /// and disabled range serving without a word.
    /// </remarks>
    public static ByteRangeSet Parse(string? header, long entityLength, int maxRanges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRanges, 1);

        if (entityLength <= 0 || header is null)
            return ByteRangeSet.Ignored;

        if (IsLongerThanAllowed(header, maxRanges))
            return ByteRangeSet.Ignored;

        if (PlainRangesOf(header, entityLength) is { } plain)
            return plain;

        if (!RangeHeaderValue.TryParse(header, out var requested))
            return ByteRangeSet.Ignored;

        if (!requested.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase))
            return ByteRangeSet.Ignored;

        List<ByteRange> satisfiable = [];

        foreach (var range in requested.Ranges)
        {
            if (Measure(range.From, range.To, entityLength) is { } measured)
                satisfiable.Add(measured);
        }

        return satisfiable.Count == 0
            ? ByteRangeSet.Unsatisfiable
            : new ByteRangeSet(RangeSatisfaction.Satisfiable, Coalesce(satisfiable));
    }

    /// <summary>
    /// The header read without the framework's header objects, or <see langword="null"/> when it is not plain.
    /// </summary>
    /// <remarks>
    /// Plain is the shape every client sends — <c>bytes=</c>, then ranges of at most eighteen digits a side
    /// joined by bare commas, each ending no lower than it starts — and on that shape the framework's reading
    /// is not in doubt, so it is read here without the header, the collection and the item per range that
    /// <see cref="RangeHeaderValue"/> allocates. Anything else, a space, another case of the unit, an empty
    /// item, a range that ends before it starts or a number that could overflow, is left to the framework,
    /// which keeps deciding what unreadable means. One satisfiable range, almost every request, is kept in a
    /// read-only list of one; several in a list sized to the ranges still unread, so it never grows.
    /// </remarks>
    private static ByteRangeSet? PlainRangesOf(string header, long entityLength)
    {
        if (!header.StartsWith(PlainUnit, StringComparison.Ordinal))
            return null;

        var rest = header.AsSpan(PlainUnit.Length);
        ByteRange first = default;
        List<ByteRange>? satisfiable = null;
        int count = 0;

        while (true)
        {
            if (!TryReadPlainRange(ref rest, out long? from, out long? to))
                return null;

            if (Measure(from, to, entityLength) is { } measured)
            {
                if (count == 0)
                    first = measured;
                else
                    (satisfiable ??= new List<ByteRange>(rest.Count(',') + 2) { first }).Add(measured);

                count++;
            }

            if (rest.IsEmpty)
                break;

            if (rest[0] != ',')
                return null;

            rest = rest[1..];
        }

        if (count == 0)
            return ByteRangeSet.Unsatisfiable;

        if (satisfiable is null)
            return new ByteRangeSet(RangeSatisfaction.Satisfiable, [first]);

        return new ByteRangeSet(RangeSatisfaction.Satisfiable, Coalesce(satisfiable));
    }

    /// <summary>
    /// Reads one plain range off the front of <paramref name="rest"/>: <c>from-to</c>, <c>from-</c> or <c>-suffix</c>.
    /// </summary>
    private static bool TryReadPlainRange(ref ReadOnlySpan<char> rest, out long? from, out long? to)
    {
        from = null;
        to = null;

        if (TryReadDigits(ref rest, out long start))
            from = start;

        if (rest.IsEmpty || rest[0] != '-')
            return false;

        rest = rest[1..];

        if (TryReadDigits(ref rest, out long end))
            to = end;

        if (from is null && to is null)
            return false;

        return from is not { } first || to is not { } last || first <= last;
    }

    /// <summary>
    /// Reads a run of one to eighteen ASCII digits, which no <see cref="long"/> overflows.
    /// </summary>
    private static bool TryReadDigits(ref ReadOnlySpan<char> rest, out long value)
    {
        value = 0;
        int length = 0;

        while (length < rest.Length && char.IsAsciiDigit(rest[length]))
        {
            if (length == 18)
                return false;

            value = (value * 10) + (rest[length] - '0');
            length++;
        }

        rest = rest[length..];

        return length > 0;
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

    private static ByteRange? Measure(long? requestedFrom, long? requestedTo, long entityLength)
    {
        long last = entityLength - 1;

        if (requestedFrom is not { } from)
        {
            long suffix = requestedTo ?? 0;

            return suffix <= 0 ? null : new ByteRange(Math.Max(0, entityLength - suffix), last);
        }

        if (from > last)
            return null;

        long to = Math.Min(requestedTo ?? last, last);

        return to < from ? null : new ByteRange(from, to);
    }

    /// <summary>The ranges in order, the overlapping and adjacent ones joined.</summary>
    /// <remarks>
    /// One range is already in order and has nothing to join, and it is what almost every request asks for, so
    /// it is answered as it came. Several are sorted and joined where they stand, each kept range written over
    /// the slot after the last one kept: the list is the reader's own, so a second one bought nothing.
    /// </remarks>
    private static List<ByteRange> Coalesce(List<ByteRange> satisfiable)
    {
        if (satisfiable.Count == 1)
            return satisfiable;

        satisfiable.Sort(static (left, right) => left.From.CompareTo(right.From));

        int kept = 0;

        for (int index = 1; index < satisfiable.Count; index++)
        {
            var range = satisfiable[index];
            var previous = satisfiable[kept];

            if (range.From > previous.To + 1)
                satisfiable[++kept] = range;
            else
                satisfiable[kept] = previous with { To = Math.Max(previous.To, range.To) };
        }

        satisfiable.RemoveRange(kept + 1, satisfiable.Count - kept - 1);

        return satisfiable;
    }
}
