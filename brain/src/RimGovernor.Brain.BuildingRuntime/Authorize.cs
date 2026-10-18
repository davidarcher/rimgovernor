namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Decides which non-root plans may dispatch under the root plan's authority: routine methods, when enabled. It
/// satisfies the executor's rounds scope so the executor recheck before every write uses the same rule as the worker.
/// </summary>
internal sealed class PlanAuthorizer(JournalStore journal, bool routine) : IRoundsScope
{
    /// <summary>
    /// Throws <see cref="UnauthorizedPlanException"/> for a plan that is not a routine method bound under the
    /// current review: a retired method, one whose goal has recovered or re-epoched, or one admitted under an
    /// earlier root.
    /// </summary>
    public async Task AuthorizeRoundsPlanAsync(GenerationSnapshot root, GenerationSnapshot target, CancellationToken ct)
    {
        if (!routine)
        {
            throw new UnauthorizedPlanException();
        }
        try
        {
            await journal.AuthorizeRoundsPlanAsync(root, target, ct).ConfigureAwait(false);
        }
        catch (ConflictException)
        {
            throw new UnauthorizedPlanException();
        }
    }
}

/// <summary>
/// The plan is not authorized under the root plan. It is a <see cref="ConflictException"/>,
/// the sentinel every authorization path reports, so callers keep matching it, and names the refusal instead of
/// the sentinel's identity-collision text.
/// </summary>
public sealed class UnauthorizedPlanException : ConflictException
{
    public UnauthorizedPlanException()
        : base("plan is not authorized under the root plan") { }
}
