using RimWorld;
using Verse;

/// <summary>
/// Vanilla work types the game's own <see cref="WorkTypeDefOf"/> does not list. Each name is spelled once, here, and resolved on first
/// use (after defs load). The game's [DefOf] rebinding does not reach this assembly, so a [DefOf] class here stays null.
/// </summary>
public static class ExtraWorkTypeDefOf
{
    private static WorkTypeDef? cooking, tailoring, art;

    public static WorkTypeDef Cooking => cooking ??= DefDatabase<WorkTypeDef>.GetNamed("Cooking");
    public static WorkTypeDef Tailoring => tailoring ??= DefDatabase<WorkTypeDef>.GetNamed("Tailoring");
    public static WorkTypeDef Art => art ??= DefDatabase<WorkTypeDef>.GetNamed("Art");
}
