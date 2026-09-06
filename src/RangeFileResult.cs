using Microsoft.AspNetCore.Mvc;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// Serves an entity range by range from an MVC action.
/// </summary>
/// <remarks>
/// It carries nothing but the description of what to serve. A result that also held the outcome of parsing
/// the request would be single-use and unsafe to share, and one <see cref="RangeContent"/> is meant to
/// serve every request for its entity.
/// </remarks>
public sealed class RangeFileResult(RangeContent content) : ActionResult
{
    /// <summary>
    /// What is being served.
    /// </summary>
    public RangeContent Content { get; } = content;

    /// <inheritdoc />
    public override Task ExecuteResultAsync(ActionContext context)
        => RangeResponse.WriteAsync<RangeFileResult>(context.HttpContext, Content);
}
