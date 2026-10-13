namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Decides which non-root plans may dispatch under the root plan's authority: routine methods (when enabled)
/// and player submissions for the same world. It satisfies the executor's rounds scope so the executor recheck
/// before every write uses the same rule as the worker (Go <c>planAuthorizer</c>).
/// </summary>
internal sealed class PlanAuthorizer(JournalStore journal, bool routine) : IRoundsScope
{
    /// <summary>
    /// Throws <see cref="UnauthorizedPlanException"/> for a plan that is neither a routine method bound under the
    /// current review nor a player submission for the root world: a retired method, one whose goal has recovered
    /// or re-epoched, or one admitted under an earlier root.
    /// </summary>
    public async Task AuthorizeRoundsPlanAsync(GenerationSnapshot root, GenerationSnapshot target, CancellationToken ct)
    {
        if (routine)
        {
            // A player plan has no method row, so any routine refusal falls through to the player check.
            try
            {
                await journal.AuthorizeRoundsPlanAsync(root, target, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // fall through to the player check
            }
        }
        try
        {
            await journal.AuthorizePlayerPlanAsync(root, target, ct).ConfigureAwait(false);
        }
        catch (ConflictException)
        {
            throw new UnauthorizedPlanException();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ConflictException(e.Message, e);
        }
    }
}

/// <summary>
/// Go <c>ErrUnauthorizedPlan</c>: the plan is not authorized under the root plan. It is a <see cref="ConflictException"/>,
/// the sentinel every authorization path reports, so callers keep matching it, and names the refusal instead of
/// the sentinel's identity-collision text.
/// </summary>
public sealed class UnauthorizedPlanException : ConflictException
{
    public UnauthorizedPlanException()
        : base("plan is not authorized under the root plan") { }
}
