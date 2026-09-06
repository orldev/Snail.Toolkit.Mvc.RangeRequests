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

    [Fact]
    public void Parse_EmptyEntity_IgnoresAnyRange()
    {
        var parsed = ByteRanges.Parse("bytes=0-99", 0, maxRanges: 5);

        Assert.Equal(RangeSatisfaction.Ignored, parsed.Satisfaction);
    }
}
