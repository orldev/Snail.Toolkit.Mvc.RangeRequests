using Microsoft.Net.Http.Headers;
using Xunit;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

public class ByteRangesTests
{
    private const long EntityLength = 1000;

    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=100-", 100, 999)]
    [InlineData("bytes=-100", 900, 999)]
    [InlineData("bytes=0-100000", 0, 999)]
    [InlineData("bytes=999-999", 999, 999)]
    [InlineData("bytes=-100000", 0, 999)]
    public void Parse_SatisfiableRange_CoversTheRequestedBytes(string header, long from, long to)
    {
        var parsed = ByteRanges.Parse(header, EntityLength, maxRanges: 5);

        Assert.Equal(RangeSatisfaction.Satisfiable, parsed.Satisfaction);
        Assert.Equal(new ByteRange(from, to), Assert.Single(parsed.Ranges));
    }

    /// <summary>
    /// Every one of these reached the previous parser as an exception thrown outside its try block: a
    /// missing dash indexed past the end of the split, and a number past int gave an overflow. Both
    /// answered 500, where RFC 7233 §3.1 asks for the header to be ignored.
    /// </summary>
    [Theory]
    [InlineData("bytes=100")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=0-99999999999999999999999")]
    [InlineData("items=0-99")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_UnreadableHeader_IsIgnoredRatherThanRejected(string? header)
    {
        var parsed = ByteRanges.Parse(header, EntityLength, maxRanges: 5);

        Assert.Equal(RangeSatisfaction.Ignored, parsed.Satisfaction);
        Assert.Empty(parsed.Ranges);
    }

    [Theory]
    [InlineData("bytes=1000-2000")]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=-0")]
    public void Parse_RangeOutsideTheEntity_IsUnsatisfiable(string header)
    {
        var parsed = ByteRanges.Parse(header, EntityLength, maxRanges: 5);

        Assert.Equal(RangeSatisfaction.Unsatisfiable, parsed.Satisfaction);
    }

    [Fact]
    public void Parse_RangeThatWouldHaveOverflowedInt_IsClampedToTheEntity()
    {
        var parsed = ByteRanges.Parse("bytes=0-3000000000", EntityLength, maxRanges: 5);

        Assert.Equal(new ByteRange(0, 999), Assert.Single(parsed.Ranges));
    }

    [Fact]
    public void Parse_EntityBeyondTwoGigabytes_KeepsFullOffsets()
    {
        const long fourGigabytes = 4L * 1024 * 1024 * 1024;

        var parsed = ByteRanges.Parse("bytes=3221225472-3221225473", fourGigabytes, maxRanges: 5);

        Assert.Equal(new ByteRange(3221225472, 3221225473), Assert.Single(parsed.Ranges));
    }

    /// <summary>
    /// The mitigation RFC 7233 §6.1 asks for. Unbounded, one request asks for the entity many times over.
    /// </summary>
    [Fact]
    public void Parse_MoreRangesThanAllowed_IgnoresTheHeaderEntirely()
    {
        var parsed = ByteRanges.Parse("bytes=0-1,2-3,4-5,6-7,8-9,10-11", EntityLength, maxRanges: 5);

        Assert.Equal(RangeSatisfaction.Ignored, parsed.Satisfaction);
    }

    [Theory]
    [InlineData("bytes=0-99,50-199")]
    [InlineData("bytes=0-99,100-199")]
    [InlineData("bytes=100-199,0-99")]
    public void Parse_OverlappingOrAdjacentRanges_AreCoalescedIntoOne(string header)
    {
        var parsed = ByteRanges.Parse(header, EntityLength, maxRanges: 5);

        Assert.Equal(new ByteRange(0, 199), Assert.Single(parsed.Ranges));
    }

    [Fact]
    public void Parse_DisjointRanges_AreKeptInAscendingOrder()
    {
        var parsed = ByteRanges.Parse("bytes=500-599,0-99", EntityLength, maxRanges: 5);

        Assert.Equal([new ByteRange(0, 99), new ByteRange(500, 599)], parsed.Ranges);
    }

    /// <summary>
    /// Plain headers are read without <see cref="RangeHeaderValue"/>, and the framework still decides what
    /// unreadable means: every header here, plain or not, must read as the framework reads it.
    /// </summary>
    [Theory]
    [InlineData("bytes=5-3")]
    [InlineData("bytes=0-9,5-3")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=0-0")]
    [InlineData("bytes=007-009")]
    [InlineData("bytes=0-9,")]
    [InlineData("bytes=,0-9")]
    [InlineData("bytes=0-9,,20-29")]
    [InlineData("bytes=0-9, 20-29")]
    [InlineData("bytes= 0-9")]
    [InlineData("bytes=0-9 ")]
    [InlineData("Bytes=0-9")]
    [InlineData("bytes=--9")]
    [InlineData("bytes=0--9")]
    [InlineData("bytes=+1-2")]
    [InlineData("bytes=٣-٤")]
    [InlineData("bytes=123456789012345678-")]
    [InlineData("bytes=1234567890123456789-")]
    [InlineData("bytes=0-1234567890123456789")]
    [InlineData("bytes=00000000000000000000001-2")]
    [InlineData("bytes=9223372036854775807-")]
    [InlineData("bytes=9223372036854775808-")]
    public void Parse_EdgeOfThePlainShape_ReadsAsTheFrameworkDoes(string header)
    {
        Assert.Equal(FrameworkReadingOf(header), ReadingOf(header));
    }

    [Fact]
    public void Parse_GeneratedHeaders_ReadAsTheFrameworkDoes()
    {
        var random = new Random(7233);
        string[] pieces = ["bytes=", "Bytes=", "bytes", "=", "-", ",", " ", "0", "1", "9", "42", "999", "1000", "123456789012345678", "9999999999999999999", "x", ";", "\t"];

        for (int attempt = 0; attempt < 50_000; attempt++)
        {
            string header = attempt % 2 == 0 ? PlainHeaderOf(random) : string.Concat(Enumerable.Range(0, random.Next(1, 9)).Select(_ => pieces[random.Next(pieces.Length)]));

            Assert.True(FrameworkReadingOf(header) == ReadingOf(header), $"'{header}' was read otherwise than the framework reads it.");
        }
    }

    [Fact]
    public void Parse_EmptyEntity_IgnoresAnyRange()
    {
        var parsed = ByteRanges.Parse("bytes=0-99", 0, maxRanges: 5);

        Assert.Equal(RangeSatisfaction.Ignored, parsed.Satisfaction);
    }

    private static string PlainHeaderOf(Random random)
    {
        string[] ranges = [.. Enumerable.Range(0, random.Next(1, 6)).Select(_ => PlainRangeOf(random))];

        return "bytes=" + string.Join(',', ranges);
    }

    private static string PlainRangeOf(Random random)
    {
        long from = random.Next(0, 3) == 0 ? random.NextInt64(0, 2000) : random.Next(0, 1200);
        long to = random.Next(0, 4) == 0 ? from - random.Next(1, 5) : from + random.Next(0, 300);

        return random.Next(0, 6) switch
        {
            0 => $"{from}-",
            1 => $"-{to}",
            _ => $"{from}-{to}",
        };
    }

    private static string ReadingOf(string header)
    {
        var parsed = ByteRanges.Parse(header, EntityLength, maxRanges: 5);

        return $"{parsed.Satisfaction} {string.Join(' ', parsed.Ranges)}";
    }

    /// <summary>
    /// The one place the library departs from the framework on purpose: a header naming more ranges than the cap
    /// is ignored before it is read (RFC 7233 §6.1), so the oracle applies the same policy and judges the reading.
    /// </summary>
    private static bool IsLongerThanTheLibraryAllows(string header) => header.AsSpan().Count(',') >= 5;

    /// <summary>
    /// What the header reads as through <see cref="RangeHeaderValue"/>, measured and coalesced as RFC 7233 says.
    /// </summary>
    private static string FrameworkReadingOf(string header)
    {
        if (IsLongerThanTheLibraryAllows(header) || !RangeHeaderValue.TryParse(header, out var requested) || !requested.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase))
            return $"{RangeSatisfaction.Ignored} ";

        List<ByteRange> satisfiable = [];

        foreach (var range in requested.Ranges)
        {
            if (range.From is null && range.To > 0)
                satisfiable.Add(new ByteRange(Math.Max(0, EntityLength - range.To.Value), EntityLength - 1));
            else if (range.From < EntityLength)
                satisfiable.Add(new ByteRange(range.From.Value, Math.Min(range.To ?? EntityLength - 1, EntityLength - 1)));
        }

        if (satisfiable.Count == 0)
            return $"{RangeSatisfaction.Unsatisfiable} ";

        List<ByteRange> merged = [];

        foreach (var range in satisfiable.OrderBy(range => range.From))
        {
            if (merged.Count > 0 && range.From <= merged[^1].To + 1)
                merged[^1] = merged[^1] with { To = Math.Max(merged[^1].To, range.To) };
            else
                merged.Add(range);
        }

        return $"{RangeSatisfaction.Satisfiable} {string.Join(' ', merged)}";
    }
}
