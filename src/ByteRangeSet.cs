namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// What reading a Range header amounted to, and what it obliges the response to be.
/// </summary>
/// <remarks>
/// Three outcomes carry three different statuses — 200, 206 and 416 — so the caller is made to tell them
/// apart. Reporting an unreadable header as an empty list would collapse "the client asked for nothing"
/// into "the client asked for the impossible", which are opposite answers.
/// </remarks>
public readonly record struct ByteRangeSet(RangeSatisfaction Satisfaction, IReadOnlyList<ByteRange> Ranges)
{
    private readonly IReadOnlyList<ByteRange>? _ranges = Ranges;

    /// <summary>
    /// The ranges to serve, in ascending order and never overlapping.
    /// </summary>
    /// <remarks>
    /// Read through the backing field rather than declared positionally, because <c>default(ByteRangeSet)</c>
    /// bypasses the constructor: without this the property would answer <see langword="null"/> from behind a
    /// non-nullable reference type, and the nullable analysis would never say so.
    /// </remarks>
    public IReadOnlyList<ByteRange> Ranges
    {
        get => _ranges ?? [];
        init => _ranges = value;
    }

    /// <summary>
    /// The header said nothing this server can act on; serve the entity whole.
    /// </summary>
    public static ByteRangeSet Ignored { get; } = new(RangeSatisfaction.Ignored, []);

    /// <summary>
    /// Nothing the client asked for exists in the entity.
    /// </summary>
    public static ByteRangeSet Unsatisfiable { get; } = new(RangeSatisfaction.Unsatisfiable, []);
}
