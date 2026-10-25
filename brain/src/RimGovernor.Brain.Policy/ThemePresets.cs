namespace RimGovernor.Brain.Policy;

/// <summary>The kinds of definition a preset member can require the installed game to have.</summary>
public enum PresetDefClass
{
    Thing,
    Research,
    Precept,
    Meme,
    RoomRole,
}

/// <summary>A definition the installed game must carry for a preset member to apply.</summary>
public readonly record struct PresetDefRef(PresetDefClass Class, string Name);

/// <summary>What the installed game carries: the presence check <see cref="PresetRules"/> resolves against.</summary>
public interface IPresetCatalog
{
    bool Has(PresetDefClass kind, string name);
}

/// <summary>The one standing preference a preset member sets.</summary>
public abstract record PresetPreference
{
    private PresetPreference()
    {
    }

    /// <summary>The risk profile the member sets.</summary>
    public sealed record Risk(RiskProfile Profile) : PresetPreference;

    /// <summary>The methods the member forbids.</summary>
    public sealed record Forbid(IReadOnlyList<ForbiddenMethod> Methods) : PresetPreference;

    /// <summary>The diet overrides the member sets.</summary>
    public sealed record Diet(IReadOnlyList<DietOverride> Overrides) : PresetPreference;

    /// <summary>The apparel preference the member sets.</summary>
    public sealed record Apparel(ApparelPreference Preference) : PresetPreference;
}

/// <summary>
/// One member of a preset under a stable key: a goal template (importance, deadline and theme are filled in on
/// resolution) or a preference. A goal's required defs follow from its kind.
/// </summary>
public sealed record PresetMember(string Key, GoalRecord? Goal = null, PresetPreference? Preference = null);

/// <summary>A named theme the player can adopt, referenced by <see cref="Id"/>.</summary>
public sealed record ThemePreset(string Id, string Name, IReadOnlyList<PresetMember> Members);

/// <summary>The player's replacement of one preset member's importance or deadline.</summary>
public abstract record MemberOverride(string Key)
{
    /// <summary>The member's importance, replacing the theme's.</summary>
    public sealed record ImportanceOf(string Key, Importance Importance) : MemberOverride(Key);

    /// <summary>The member's deadline day, replacing the theme's.</summary>
    public sealed record DeadlineOf(string Key, int Day) : MemberOverride(Key);
}

/// <summary>One preset member as it applies to a theme. <see cref="UnavailableReason"/> names the missing defs; empty when available.</summary>
public sealed record ResolvedMember(string Key, GoalRecord? Goal, PresetPreference? Preference, string UnavailableReason)
{
    public bool Available => UnavailableReason.Length == 0;
}

/// <summary>A preset expanded for one theme: every member in preset order, the unavailable ones kept and reasoned.</summary>
public sealed record ResolvedPreset(IReadOnlyList<ResolvedMember> Members)
{
    /// <summary>The goals of the available members.</summary>
    public IEnumerable<GoalRecord> Goals() => Members.Where(m => m.Available && m.Goal is not null).Select(m => m.Goal!);

    /// <summary>The members the installed game cannot apply, in preset order.</summary>
    public IEnumerable<ResolvedMember> Unavailable() => Members.Where(m => !m.Available);
}

/// <summary>Expands a preset over the installed catalog (#3493); pure.</summary>
public static class PresetRules
{
    /// <summary>
    /// <paramref name="preset"/> for <paramref name="theme"/>: members the theme removed are gone, the rest take the
    /// theme's importance and deadline unless <paramref name="overrides"/> replace them, and a member whose defs
    /// <paramref name="catalog"/> lacks is kept as unavailable with the missing defs named.
    /// </summary>
    public static ResolvedPreset Resolve(ThemePreset preset, IPresetCatalog catalog, ThemeRecord theme, IReadOnlyList<MemberOverride>? overrides = null)
    {
        var members = new List<ResolvedMember>();
        foreach (var member in preset.Members)
        {
            if (theme.RemovedPresetMembers.Contains(member.Key, StringComparer.Ordinal))
            {
                continue;
            }

            var mine = overrides?.Where(o => string.Equals(o.Key, member.Key, StringComparison.Ordinal)).ToList() ?? [];
            var importance = mine.OfType<MemberOverride.ImportanceOf>().Select(o => o.Importance).Cast<Importance?>().FirstOrDefault() ?? theme.Importance;
            var deadline = mine.OfType<MemberOverride.DeadlineOf>().Select(o => GoalDeadline.Of(o.Day)).FirstOrDefault() ?? theme.DeadlineDay;
            var goal = member.Goal is { } template
                ? template with
                {
                    Importance = importance,
                    DeadlineDay = deadline,
                    Theme = ThemeMembership.Of(theme.Id),
                }
                : null;
            var missing = goal is null ? [] : Requirements(goal).Where(r => !catalog.Has(r.Class, r.Name)).Select(r => $"{r.Class} {r.Name}").ToList();
            members.Add(new ResolvedMember(member.Key, goal, member.Preference, missing.Count == 0 ? "" : "missing " + string.Join(", ", missing)));
        }

        return new ResolvedPreset(members);
    }

    /// <summary>The defs a goal needs the installed game to carry; a kind that names none (counts, tiers, events) needs none.</summary>
    public static IEnumerable<PresetDefRef> Requirements(GoalRecord goal)
    {
        if (DefClassOf(goal.Kind) is { } kind)
        {
            yield return new PresetDefRef(kind, goal.Target);
        }

        if (goal.Kind == GoalKind.WornCount && goal.Qualifier.Length > 0)
        {
            yield return new PresetDefRef(PresetDefClass.Thing, goal.Qualifier);
        }
    }

    /// <summary>The class of def a goal kind's target names; null for a kind whose target is no def.</summary>
    private static PresetDefClass? DefClassOf(GoalKind kind)
    {
        switch (kind)
        {
            case GoalKind.ResearchProject:
                return PresetDefClass.Research;
            case GoalKind.StockCount or GoalKind.BuiltCount or GoalKind.WornCount or GoalKind.Animals or GoalKind.VeneratedAnimal:
                return PresetDefClass.Thing;
            case GoalKind.RoomRole:
                return PresetDefClass.RoomRole;
            case GoalKind.Precept:
                return PresetDefClass.Precept;
            case GoalKind.Meme:
                return PresetDefClass.Meme;
            case GoalKind.ResearchTier or GoalKind.Colonists or GoalKind.PowerSurplus or GoalKind.Believers or GoalKind.NamedPrisoner
                or GoalKind.NamedQuest or GoalKind.Ritual or GoalKind.Skill or GoalKind.Health or GoalKind.Territory
                or GoalKind.Ending or GoalKind.Caravan:
                return null;
        }

        throw new DomainException($"unlisted goal kind {kind}");
    }
}
