namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// One satisfiable byte range, both ends inclusive.
/// </summary>
/// <remarks>
/// Offsets are <see cref="long"/> throughout. An <see cref="int"/> offset truncates silently past 2 GB,
/// which is the one size class range requests exist for.
/// </remarks>
public readonly record struct ByteRange(long From, long To)
{
    /// <summary>
    /// How many bytes the range covers.
    /// </summary>
    public long Length => To - From + 1;
}
