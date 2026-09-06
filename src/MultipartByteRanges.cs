using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Lays out a <c>multipart/byteranges</c> body and states its exact length before a byte is written.
/// </summary>
/// <remarks>
/// The length is the UTF-8 byte count of the very strings written later, never a character count against
/// a hand-summed constant: a single non-ASCII character in the content type then promises a Content-Length
/// the body cannot match, and Kestrel breaks the connection. The boundary comes from
/// <see cref="RandomNumberGenerator"/> rather than from the clock — two requests in one tick would share a
/// delimiter, and a predictable one invites a client to plant it in the entity it uploads.
/// </remarks>
internal sealed class MultipartByteRanges
{
    private const string Crlf = "\r\n";

    private readonly IReadOnlyList<string> _partHeaders;
    private readonly string _epilogue;

    internal MultipartByteRanges(IReadOnlyList<ByteRange> ranges, string entityContentType, long entityLength)
    {
        Boundary = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        _partHeaders =
        [
            .. ranges.Select(range =>
                $"--{Boundary}{Crlf}Content-Type: {entityContentType}{Crlf}Content-Range: bytes {range.From}-{range.To}/{entityLength}{Crlf}{Crlf}")
        ];

        _epilogue = $"--{Boundary}--";

        ContentLength = _partHeaders.Sum(header => (long)Encoding.UTF8.GetByteCount(header))
                        + ranges.Sum(range => range.Length + Encoding.UTF8.GetByteCount(Crlf))
                        + Encoding.UTF8.GetByteCount(_epilogue);
    }

    /// <summary>
    /// The delimiter token, without the leading dashes that precede it on the wire.
    /// </summary>
    internal string Boundary { get; }

    /// <summary>
    /// The media type the response must carry for this body to be readable.
    /// </summary>
    internal string ContentType => $"multipart/byteranges; boundary={Boundary}";

    /// <summary>
    /// Exactly how many bytes <see cref="WritePartHeaderAsync"/>, the ranges and the epilogue will produce.
    /// </summary>
    internal long ContentLength { get; }

    /// <summary>
    /// Writes the delimiter and headers that introduce one part.
    /// </summary>
    internal Task WritePartHeaderAsync(HttpResponse response, int index, CancellationToken cancellationToken)
        => response.WriteAsync(_partHeaders[index], cancellationToken);

    /// <summary>
    /// Closes one part's payload.
    /// </summary>
    internal static Task WritePartTrailerAsync(HttpResponse response, CancellationToken cancellationToken)
        => response.WriteAsync(Crlf, cancellationToken);

    /// <summary>
    /// Writes the closing delimiter.
    /// </summary>
    internal Task WriteEpilogueAsync(HttpResponse response, CancellationToken cancellationToken)
        => response.WriteAsync(_epilogue, cancellationToken);
}
