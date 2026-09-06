namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Writes <paramref name="length"/> bytes of the entity, starting at <paramref name="offset"/>, to the body.
/// </summary>
/// <remarks>
/// <para>
/// This delegate is the reason the library exists: it lets an entity that is not a <see cref="Stream"/> —
/// an object store answering a ranged GET, a database blob, generated content — serve a range request
/// without being buffered into memory first. Everything else here ASP.NET Core already does through
/// <c>FileStreamResult.EnableRangeProcessing</c>.
/// </para>
/// <para>
/// The contract it must keep: write exactly <paramref name="length"/> bytes, because Content-Length has
/// already been sent and Kestrel resets a connection that does not match it; be safe to call concurrently
/// and re-entrantly, because one <see cref="RangeContent"/> normally serves every request for an entity and
/// a multipart response calls this once per part; and let cancellation surface rather than swallowing it.
/// </para>
/// </remarks>
public delegate Task WriteRange(Stream body, long offset, long length, CancellationToken cancellationToken);
