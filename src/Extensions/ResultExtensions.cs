using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace Snail.Toolkit.Mvc.RangeRequests.Extensions;

/// <summary>
/// Range serving reachable from <c>Results.Extensions</c>, where a Minimal API endpoint looks for it.
/// </summary>
public static class ResultExtensions
{
    extension(IResultExtensions results)
    {
        /// <summary>
        /// Serves the entity range by range.
        /// </summary>
        /// <remarks>
        /// The declared return type is the result itself rather than <see cref="IResult"/>, because that is
        /// what routing reads <see cref="RangeFileHttpResult.PopulateMetadata"/> from: behind an
        /// <see cref="IResult"/> the endpoint would describe none of the statuses it answers with.
        /// </remarks>
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "A static member of an extension block extends the type, not an instance of it, so Results.Extensions.RangeFile(content) would stop compiling.")]
        public RangeFileHttpResult RangeFile(RangeContent content) => new(content);
    }
}
