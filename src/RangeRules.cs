namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// What this server is willing to serve in answer to a Range header.
/// </summary>
/// <remarks>
/// Each limit refuses the values that would turn it into its own opposite: a cap of zero on the number of
/// ranges disables range serving without saying so, and a cap of zero on their length describes a
/// Content-Range that ends before it starts.
/// </remarks>
public sealed record RangeRules
{
    /// <summary>
    /// The rules applied when a caller states none.
    /// </summary>
    public static RangeRules Default { get; } = new();

    /// <summary>
    /// Largest entity served at all; anything longer answers 413.
    /// </summary>
    public long? MaxFileLength
    {
        get;
        init => field = value is null or > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxFileLength), value, "A limit of zero or less refuses every entity.");
    }

    /// <summary>
    /// How many ranges one request may ask for before the header is ignored altogether.
    /// </summary>
    /// <remarks>
    /// Five covers every download manager and media player seen in practice. An unbounded set turns one
    /// request into arbitrary egress: <c>bytes=0-,0-,0-…</c> repeated ten thousand times over a 1 GB entity
    /// promises 10 TB and calls the writer ten thousand times. RFC 7233 §6.1 asks servers to impose exactly
    /// this limit, and the unbounded form is the shape of CVE-2011-3192. A header naming more is ignored
    /// rather than refused, so the client gets the entity whole instead of an error.
    /// </remarks>
    public int MaxRanges
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxRanges), value, "A limit below one refuses every range request.");
    } = 5;

    /// <summary>
    /// Longest single range served; a longer one is truncated, which RFC 7233 §4.1 permits.
    /// </summary>
    public long? MaxRangeLength
    {
        get;
        init => field = value is null or > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxRangeLength), value, "A limit of zero or less describes a range that ends before it starts.");
    }
}
