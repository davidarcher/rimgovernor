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

/// <summary>The standing preferences a preset member sets; a member names only the ones it changes.</summary>
public sealed record PreferenceFragment
{
    public RiskProfile? Risk { get; init; }

    public Pace? Pace { get; init; }

    public IReadOnlyList<ForbiddenMethod> Forbidden { get; init; } = [];

    public IReadOnlyList<DietOverride> Diet { get; init; } = [];

    public ApparelPreference? Apparel { get; init; }

    public DrugPolicyPreference? DrugPolicy { get; init; }
}

/// <summary>
/// One member of a preset under a stable key: a goal template (importance, deadline and theme are filled in on
/// resolution) or a preference fragment. A goal's required defs follow from its kind.
/// </summary>
public sealed record PresetMember(string Key, GoalRecord? Goal = null, PreferenceFragment? Preference = null);

/// <summary>A named theme the player can adopt, referenced by <see cref="Id"/>.</summary>
public sealed record ThemePreset(string Id, string Name, IReadOnlyList<PresetMember> Members);

/// <summary>The player's replacement of one preset member's importance or deadline.</summary>
public sealed record MemberOverride(string Key, Importance? Importance = null, int? DeadlineDay = null);

/// <summary>One preset member as it applies to a theme. <see cref="UnavailableReason"/> names the missing defs; empty when available.</summary>
public sealed record ResolvedMember(string Key, GoalRecord? Goal, PreferenceFragment? Preference, string UnavailableReason)
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

            var over = overrides?.FirstOrDefault(o => string.Equals(o.Key, member.Key, StringComparison.Ordinal));
            var goal = member.Goal is { } template
                ? template with
                {
                    Importance = over?.Importance ?? theme.Importance,
                    DeadlineDay = over?.DeadlineDay ?? theme.DeadlineDay,
                    Theme = theme.Id,
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
        var target = goal.Kind switch
        {
            GoalKind.ResearchProject => (PresetDefClass?)PresetDefClass.Research,
            GoalKind.StockCount or GoalKind.BuiltCount or GoalKind.WornCount or GoalKind.Animals or GoalKind.VeneratedAnimal => PresetDefClass.Thing,
            GoalKind.RoomRole => PresetDefClass.RoomRole,
            GoalKind.Precept => PresetDefClass.Precept,
            GoalKind.Meme => PresetDefClass.Meme,
            _ => null,
        };
        if (target is { } kind)
        {
            yield return new PresetDefRef(kind, goal.Target);
        }

        if (goal.Kind == GoalKind.WornCount && goal.Qualifier.Length > 0)
        {
            yield return new PresetDefRef(PresetDefClass.Thing, goal.Qualifier);
        }
    }
}
