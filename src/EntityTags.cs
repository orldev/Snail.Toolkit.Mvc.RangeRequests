using Microsoft.Net.Http.Headers;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Derives the entity tag for content that carries none of its own.
/// </summary>
internal static class EntityTags
{
    /// <summary>
    /// The tag of an entity of this modification date and length.
    /// </summary>
    /// <remarks>
    /// Date and length only, so it costs nothing per request and stays stable while the entity does.
    /// Hashing the name instead buys nothing a conditional request can use and costs a hash per request;
    /// hashing the bytes would mean reading the entity to answer a request that may want 200 KB of it.
    /// The known limit is collision: HTTP dates carry whole seconds, so two entities of equal length
    /// stored within the same second share a tag, which is why <see cref="RangeContent.ETag"/> exists.
    /// </remarks>
    internal static EntityTagHeaderValue For(DateTimeOffset lastModified, long entityLength)
    {
        long hash = lastModified.UtcDateTime.ToBinary() ^ entityLength;

        return new EntityTagHeaderValue($"\"{hash:x}\"");
    }
}
