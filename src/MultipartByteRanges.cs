using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Lays out a <c>multipart/byteranges</c> body and states its exact length before a byte is written.
/// </summary>
/// <remarks>
/// <para>
/// The length is the count of the very bytes written later, never a character count against a hand-summed
/// constant: a single non-ASCII character in the content type then promises a Content-Length the body cannot
/// match, and Kestrel breaks the connection. The boundary comes from <see cref="RandomNumberGenerator"/> rather
/// than from the clock — two requests in one tick would share a delimiter, and a predictable one invites a
/// client to plant it in the entity it uploads.
/// </para>
/// <para>
/// Everything the body carries besides the entity — each part's delimiter and headers, the line that closes a
/// part and the closing delimiter — is encoded once, into one array, as the segments written between the
/// parts: the first part's headers, then each closing line with the next part's headers, then the last closing
/// line with the closing delimiter. A string per part, a list of them and a write and a flush per string
/// through the response's pipe writer cost more than the rest of a request. The array is rented from the shared
/// pool and goes back when the response is done with it, written or not.
/// </para>
/// </remarks>
internal sealed class MultipartByteRanges : IDisposable
{
    private const int BoundaryLength = 32;

    private readonly IReadOnlyList<ByteRange> _ranges;
    private readonly int[] _segmentEnds;

    private byte[]? _framing;

    internal MultipartByteRanges(IReadOnlyList<ByteRange> ranges, string entityContentType, long entityLength)
    {
        Span<byte> random = stackalloc byte[BoundaryLength / 2];
        Span<char> boundary = stackalloc char[BoundaryLength];

        RandomNumberGenerator.Fill(random);
        Convert.TryToHexStringLower(random, boundary, out _);

        _ranges = ranges;
        _segmentEnds = new int[ranges.Count + 1];
        _framing = FramingOf(ranges, boundary, entityContentType, entityLength, _segmentEnds);

        ContentType = $"multipart/byteranges; boundary={boundary}";
        ContentLength = _segmentEnds[^1] + LengthOf(ranges);
    }

    /// <summary>
    /// The media type the response must carry for this body to be readable.
    /// </summary>
    internal string ContentType { get; }

    /// <summary>
    /// Exactly how many bytes <see cref="WriteAsync"/> will produce.
    /// </summary>
    internal long ContentLength { get; }

    /// <summary>
    /// Writes the body: each part's delimiter and headers, its range of the entity, and the closing delimiter.
    /// </summary>
    internal async Task WriteAsync(Stream body, WriteRange write, CancellationToken cancellationToken)
    {
        byte[] framing = _framing ?? throw new ObjectDisposedException(nameof(MultipartByteRanges));
        int start = 0;

        for (int index = 0; index < _ranges.Count; index++)
        {
            await body.WriteAsync(framing.AsMemory(start.._segmentEnds[index]), cancellationToken);

            start = _segmentEnds[index];

            var range = _ranges[index];

            await write(body, range.From, range.Length, cancellationToken);
        }

        await body.WriteAsync(framing.AsMemory(start.._segmentEnds[^1]), cancellationToken);
    }

    /// <summary>
    /// Gives the framing back to the pool.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _framing, null) is { } framing)
            ArrayPool<byte>.Shared.Return(framing);
    }

    private static byte[] FramingOf(
        IReadOnlyList<ByteRange> ranges,
        ReadOnlySpan<char> boundary,
        string entityContentType,
        long entityLength,
        Span<int> segmentEnds)
    {
        const int longestNumber = 20;
        const int partOverhead = 64 + BoundaryLength + (3 * longestNumber);

        int capacity = (ranges.Count * (partOverhead + Encoding.UTF8.GetByteCount(entityContentType))) + partOverhead;
        byte[] framing = ArrayPool<byte>.Shared.Rent(capacity);

        try
        {
            var free = framing.AsSpan();
            int written = 0;

            for (int index = 0; index < ranges.Count; index++)
            {
                var range = ranges[index];
                string lineEnd = index == 0 ? "" : "\r\n";

                if (!Utf8.TryWrite(free[written..], $"{lineEnd}--{boundary}\r\nContent-Type: {entityContentType}\r\nContent-Range: bytes {range.From}-{range.To}/{entityLength}\r\n\r\n", out int part))
                    throw new InvalidOperationException("A part's headers outgrew the room measured for them.");

                written += part;
                segmentEnds[index] = written;
            }

            if (!Utf8.TryWrite(free[written..], $"\r\n--{boundary}--", out int epilogue))
                throw new InvalidOperationException("The closing delimiter outgrew the room measured for it.");

            written += epilogue;
            segmentEnds[ranges.Count] = written;

            return framing;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(framing);

            throw;
        }
    }

    private static long LengthOf(IReadOnlyList<ByteRange> ranges)
    {
        long length = 0;

        for (int index = 0; index < ranges.Count; index++)
            length += ranges[index].Length;

        return length;
    }
}
