using Microsoft.Net.Http.Headers;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// The entity to serve, and everything needed to validate a request for it.
/// </summary>
/// <remarks>
/// One shape shared by the MVC and Minimal API results, so the protocol is written once. Init properties
/// rather than a positional list: seven members of which three are numbers and dates are easy to misorder.
/// Every value is checked where it enters, because <c>required</c> guarantees that a member was set and
/// nothing more — a <c>required long Length</c> accepts -1 without complaint.
/// </remarks>
public sealed record RangeContent
{
    private readonly EntityTagHeaderValue? _entityTag;

    /// <summary>
    /// How the entity is written, range by range.
    /// </summary>
    public required WriteRange Write { get; init; }

    /// <summary>
    /// Bytes the entity holds in full.
    /// </summary>
    /// <remarks>
    /// A negative length would reach <c>Response.ContentLength</c> and the caller's own delegate as a
    /// negative count, so it is refused here instead.
    /// </remarks>
    public required long Length
    {
        get;
        init => field = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Length), value, "An entity cannot be shorter than nothing.");
    }

    /// <summary>
    /// Media type of the entity itself, not of the multipart envelope.
    /// </summary>
    /// <remarks>
    /// Parsed and stored in its canonical form rather than kept as given. A multipart response writes this
    /// value into the response <em>body</em>, where Kestrel's header validation does not reach, so a media
    /// type carrying CRLF — the shape an application gets when it stores the type an upload declared — would
    /// forge part headers. Round-tripping it through <see cref="MediaTypeHeaderValue"/> makes that
    /// unrepresentable rather than merely unlikely.
    /// </remarks>
    public required string ContentType
    {
        get;
        init => field = MediaTypeHeaderValue.TryParse(value, out var parsed)
            ? parsed.ToString()
            : throw new ArgumentException($"'{value}' is not a media type.", nameof(ContentType));
    }

    /// <summary>
    /// When the entity last changed.
    /// </summary>
    /// <remarks>
    /// It feeds both the entity tag and Last-Modified, so a value that drifts between requests for the same
    /// entity breaks resumed downloads: the client's If-Range stops matching and every resume restarts.
    /// HTTP dates carry whole seconds, so anything finer is truncated before it is used.
    /// </remarks>
    public required DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// The validator the store already holds, or <see langword="null"/> to derive one.
    /// </summary>
    /// <remarks>
    /// A derived tag is the modification date hashed with the length, and two entities of the same length
    /// stored within the same second — HTTP dates carry no finer resolution — share it, which serves a
    /// client a stale body out of its own cache. A store that versions its objects already knows better,
    /// so it is allowed to say so. Weak validators are refused: RFC 7232 §2.1 bars them from the strong
    /// comparison If-Range requires, so one would silently turn every resumed download into a fresh one.
    /// </remarks>
    public string? ETag
    {
        get => _entityTag?.ToString();
        init
        {
            if (value is null)
            {
                _entityTag = null;

                return;
            }

            if (!EntityTagHeaderValue.TryParse(value, out var parsed))
                throw new ArgumentException($"'{value}' is not an entity tag.", nameof(ETag));

            if (parsed.IsWeak)
                throw new ArgumentException($"'{value}' is a weak validator, which no range request can resume against.", nameof(ETag));

            _entityTag = parsed;
        }
    }

    /// <summary>
    /// Name offered to the client, or <see langword="null"/> to send no Content-Disposition.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// What the server is willing to serve.
    /// </summary>
    public RangeRules Rules { get; init; } = RangeRules.Default;

    internal EntityTagHeaderValue? EntityTag => _entityTag;
}
