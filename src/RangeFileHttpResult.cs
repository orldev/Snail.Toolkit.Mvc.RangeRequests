using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Serves an entity range by range from a Minimal API endpoint.
/// </summary>
/// <remarks>
/// The protocol lives in one place and both results delegate to it, so a Minimal API endpoint answers
/// exactly what an MVC action does.
/// </remarks>
public sealed class RangeFileHttpResult(RangeContent content) : IResult, IEndpointMetadataProvider
{
    /// <summary>
    /// What is being served.
    /// </summary>
    public RangeContent Content { get; } = content;

    /// <summary>
    /// Declares every status a range endpoint answers with.
    /// </summary>
    /// <remarks>
    /// The status is decided per request from headers no build-time description can see, so
    /// <c>IStatusCodeHttpResult</c> could only answer <see langword="null"/> and describe nothing. Metadata
    /// is where the endpoint can be honest: without it a generated client is told about 200 alone and
    /// treats the 206 it will actually receive as a failure.
    /// </remarks>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, typeof(void), ["application/octet-stream"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status206PartialContent, typeof(void), ["application/octet-stream", "multipart/byteranges"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status304NotModified));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status412PreconditionFailed));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status413PayloadTooLarge));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status416RangeNotSatisfiable));
    }

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
        => RangeResponse.WriteAsync<RangeFileHttpResult>(httpContext, Content);
}
