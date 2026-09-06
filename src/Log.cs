using Microsoft.Extensions.Logging;

namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// What this library says about a request it could not serve in full.
/// </summary>
/// <remarks>
/// Levels are set by who has to act. A 304 means a browser cache is working and a 416 means a client sent
/// bad offsets; logged as errors they bury <see cref="BodyFailed"/>, the one line here that says a client
/// received a response that lied about its own length.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Entity of {EntityLength} bytes exceeds the {MaxFileLength} byte limit; answered 413.")]
    internal static partial void EntityTooLarge(ILogger logger, long entityLength, long maxFileLength);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "Precondition answered {StatusCode} without a body.")]
    internal static partial void PreconditionAnswered(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "No requested range exists in an entity of {EntityLength} bytes; answered 416.")]
    internal static partial void RangeNotSatisfiable(ILogger logger, long entityLength);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "The client left before the body was written.")]
    internal static partial void ClientLeft(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error,
        Message = "Writing the body failed after Content-Length was promised; the connection was aborted.")]
    internal static partial void BodyFailed(ILogger logger, Exception exception);
}
