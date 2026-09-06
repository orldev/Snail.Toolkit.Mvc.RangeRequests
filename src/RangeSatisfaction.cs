namespace Snail.Toolkit.Mvc.RangeRequests;

/// <summary>
/// What a Range header amounted to once measured against the entity.
/// </summary>
public enum RangeSatisfaction
{
    /// <summary>
    /// No usable Range header, so the whole entity is served with 200.
    /// </summary>
    Ignored,

    /// <summary>
    /// At least one requested range exists in the entity, so 206 is served.
    /// </summary>
    Satisfiable,

    /// <summary>
    /// Every requested range lies outside the entity, so 416 is served.
    /// </summary>
    Unsatisfiable
}
