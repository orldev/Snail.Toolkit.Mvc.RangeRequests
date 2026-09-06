using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Snail.Toolkit.Mvc.RangeRequests.Extensions;
using Xunit;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

/// <summary>
/// What an endpoint tells the outside world about itself, and how an author reaches these results.
/// </summary>
public class EndpointContractTests
{
    private static readonly int[] AnsweredStatuses =
    [
        StatusCodes.Status200OK,
        StatusCodes.Status206PartialContent,
        StatusCodes.Status304NotModified,
        StatusCodes.Status412PreconditionFailed,
        StatusCodes.Status413PayloadTooLarge,
        StatusCodes.Status416RangeNotSatisfiable
    ];

    /// <summary>
    /// A generated client told about 200 alone treats the 206 it will actually receive as a failure, so
    /// every status the protocol can answer with has to reach the endpoint's metadata.
    /// </summary>
    [Fact]
    public void MapGet_ReturningARangeResult_DescribesEveryStatusItAnswersWith()
    {
        var application = WebApplication.CreateSlimBuilder().Build();

        application.MapGet("/media", () => new RangeFileHttpResult(Content()));

        Assert.All(AnsweredStatuses, status => Assert.Contains(status, DescribedStatusesOf(application)));
    }

    /// <summary>
    /// The extension returns the result type rather than <c>IResult</c>, which is what routing reads the
    /// metadata from: behind an interface the endpoint describes nothing.
    /// </summary>
    [Fact]
    public void MapGet_ReturningTheResultsExtension_DescribesTheSameStatuses()
    {
        var application = WebApplication.CreateSlimBuilder().Build();

        application.MapGet("/media", () => Results.Extensions.RangeFile(Content()));

        Assert.All(AnsweredStatuses, status => Assert.Contains(status, DescribedStatusesOf(application)));
    }

    /// <summary>
    /// The control for the two tests above: routing describes none of this by itself, so what they assert
    /// comes from the result type and would go missing if it stopped providing metadata.
    /// </summary>
    [Fact]
    public void MapGet_ReturningAnOrdinaryResult_DescribesNoneOfTheRangeStatuses()
    {
        var application = WebApplication.CreateSlimBuilder().Build();

        application.MapGet("/media", () => Results.Ok());

        var described = DescribedStatusesOf(application);

        Assert.DoesNotContain(StatusCodes.Status206PartialContent, described);
        Assert.DoesNotContain(StatusCodes.Status416RangeNotSatisfiable, described);
    }

    [Fact]
    public void RangeFile_OnAController_ServesTheContentItWasGiven()
    {
        var content = Content();

        var result = new MediaController().RangeFile(content);

        Assert.Same(content, result.Content);
    }

    [Fact]
    public void RangeFile_OnResultsExtensions_ServesTheContentItWasGiven()
    {
        var content = Content();

        var result = Results.Extensions.RangeFile(content);

        Assert.Same(content, result.Content);
    }

    private static IReadOnlyList<int> DescribedStatusesOf(WebApplication application)
        => [.. ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .SelectMany(endpoint => endpoint.Metadata.OfType<IProducesResponseTypeMetadata>())
            .Select(metadata => metadata.StatusCode)];

    private static RangeContent Content() => new()
    {
        Write = (body, _, length, cancellationToken) =>
            body.WriteAsync(new byte[length], cancellationToken).AsTask(),
        Length = 1000,
        ContentType = "application/octet-stream",
        LastModified = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero)
    };

    private sealed class MediaController : ControllerBase;
}
