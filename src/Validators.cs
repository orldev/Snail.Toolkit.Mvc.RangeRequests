using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// The validators of one entity, as every request for it is answered with them.
/// </summary>
/// <param name="LastModified">When the entity last changed, truncated to the whole second an HTTP date carries.</param>
/// <param name="LastModifiedText">The same date as the Last-Modified header writes it.</param>
/// <param name="Tag">The entity tag the store gave, or the one derived from the date and the length.</param>
/// <param name="TagText">The same tag as the ETag header writes it.</param>
/// <param name="Disposition">The Content-Disposition header, or <see langword="null"/> when the content offers no name.</param>
/// <remarks>
/// The disposition is no validator, but it is as much a function of the content and costs more to write: a
/// header object, a parameter list and the RFC 5987 encoding of the name, built on every request it was asked for.
/// </remarks>
internal sealed record Validators(
    DateTimeOffset LastModified,
    string LastModifiedText,
    EntityTagHeaderValue Tag,
    string TagText,
    string? Disposition)
{
    public static Validators Of(RangeContent content)
    {
        var lastModified = DateTimeOffset.FromUnixTimeSeconds(content.LastModified.ToUnixTimeSeconds());
        var tag = content.EntityTag ?? EntityTags.For(lastModified, content.Length);
        string? disposition = content.FileName is { Length: > 0 } fileName ? DispositionOf(fileName) : null;

        return new Validators(lastModified, lastModified.ToString("r", CultureInfo.InvariantCulture), tag, tag.ToString(), disposition);
    }

    private static string DispositionOf(string fileName)
    {
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(fileName);

        return disposition.ToString();
    }
}
