using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;

namespace Snail.Toolkit.Mvc.RangeRequests.Extensions;

/// <summary>
/// Range serving written the way a controller writes every other file response.
/// </summary>
public static class ControllerBaseExtensions
{
    extension(ControllerBase controller)
    {
        /// <summary>
        /// Serves the entity range by range.
        /// </summary>
        /// <remarks>
        /// It sits next to <c>File</c>, which is where an action author looks for this: a result type that
        /// has to be discovered and constructed by hand is a result type nobody finds.
        /// </remarks>
        [SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "A static member of an extension block extends the type, not an instance of it, so controller.RangeFile(content) would stop compiling.")]
        public RangeFileResult RangeFile(RangeContent content) => new(content);
    }
}
