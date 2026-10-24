using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

#pragma warning disable CA1720, CA1711

namespace RimGovernor.Brain.Policy;

// Unique short names: per-pawn policies are labelled by a pawn's short name, so every owned pawn (colonists,
// slaves, prisoners, named animals) holds one no other owned pawn holds. On a collision the oldest holder
// keeps it and every newer one is renamed; native draws the new name from the pawn's own name bank, never a
// numbered one.

public static partial class PawnRules
{
    /// <summary>The renames owed: every pawn whose short name an older owned pawn already holds, compared case-insensitively.</summary>
    public static IReadOnlyList<PawnSettings> NicknameChanges(IReadOnlyList<OwnedName> names)
    {
        var rows = names.OrderBy(r => r.ThingId).ToList();
        var held = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PawnSettings>();
        foreach (var row in rows)
        {
            var key = row.ShortName.Trim().ToLowerInvariant();
            if (key.Length == 0)
            {
                continue;
            }
            if (held.Add(key))
            {
                continue;
            }
            try
            {
                result.Add(PawnSettings.NewNicknameSetting(row.PawnId, row.ShortName));
            }
            catch (DomainException)
            {
                // An unusable name owes no rename.
            }
        }
        return result;
    }

    /// <summary>The review's NamesOwed fact: a rename is owed. An unknown census owes nothing.</summary>
    public static Fact<bool> NamesOwed(Fact<PrisonerCensus> population)
    {
        if (!population.TryGetValue(out var census))
        {
            return Unknown<bool>();
        }
        return Known(NicknameChanges(census.Names).Count > 0);
    }
}
