using Sex = RimGovernor.Contracts.Wire.Defs.Gender;

namespace RimGovernor.Brain.Policy;

public static class HerdSterilizeRules
{
    /// <summary>
    /// The animals the plan wants sterilized now: of a race that is superseded (a better race will take its job;
    /// it keeps working but stops breeding) or the males beyond the plan's ratio (herdMalesPerFemales) of a race
    /// kept within its cap. Never a founder, a retiring race (culled instead), an animal designated for removal,
    /// or one with an unread sterilize fact; and never a sex below its breeding pair of fertile animals
    /// (herdPairMales, herdPairFemales), which must exist to begin with. A queued bill counts as sterile already
    /// so a bill cannot take a race below its pair.
    /// </summary>
    internal static HashSet<PawnId> SterilizeWanted(IReadOnlyList<UpkeepAnimal> rows, HerdPolicy herd)
    {
        var fertile = new Dictionary<Resource, List<UpkeepAnimal>>();
        foreach (var a in rows)
        {
            var qk = a.SterilizeQueued.TryGetValue(out var queued);
            if (!qk || a.Release || a.Slaughter || a.Sterilized || queued)
            {
                continue;
            }
            if (!fertile.TryGetValue(a.Definition, out var list))
            {
                list = [];
                fertile[a.Definition] = list;
            }
            list.Add(a);
        }
        var wanted = new HashSet<PawnId>();
        foreach (var (race, animals) in fertile)
        {
            var role = herd.Roles.GetValueOrDefault(race) ?? new HerdRole();
            if (role.Founder || role.Retiring || herd.Retired.GetValueOrDefault(race))
            {
                continue;
            }
            long males = 0;
            long females = 0;
            foreach (var a in animals)
            {
                switch (a.Gender)
                {
                    case Sex.Male:
                        males++;
                        break;
                    case Sex.Female:
                        females++;
                        break;
                }
            }
            if (males < HerdSurplusRules.HerdPairMales || females < HerdSurplusRules.HerdPairFemales)
            {
                continue;
            }
            var (keepMales, _, mk) = herd.MalesKept(race, females);
            if (!mk)
            {
                continue;
            }
            var excessMales = males - keepMales;
            foreach (var a in animals)
            {
                if (a.Gender == Sex.Male && males > HerdSurplusRules.HerdPairMales && (role.Superseded || excessMales > 0))
                {
                    wanted.Add(a.Id);
                    males--;
                    excessMales--;
                }
                else if (a.Gender == Sex.Female && females > HerdSurplusRules.HerdPairFemales && role.Superseded)
                {
                    wanted.Add(a.Id);
                    females--;
                }
            }
        }
        return wanted;
    }

    /// <summary>
    /// Sequences the sterilize surgery through the hospital ward, one write per cycle,
    /// from the animals' current allowed area and sterilize facts alone: let an animal wanted sterilized into the
    /// hospital (allowed_area), queue its bill (sterilize), and let it out once it is sterilized (allowed_area
    /// cleared). One animal is in the room at a time, and an animal with a bill queued is waited on. An animal in
    /// the area that is no longer wanted and has no bill is let out. An unready or unknown ward room chooses
    /// nothing.
    /// </summary>
    public static HusbandryChoice SterilizeChoice(Fact<EquatableArray<UpkeepAnimal>> animals, HerdPolicy herd, AnimalWard ward)
    {
        var none = new HusbandryChoice { Reason = HusbandryPlanReason.NoDeficit };
        var known = animals.TryGetValue(out var unsorted);
        var rk = ward.Ready.TryGetValue(out var ready);
        var ak = ward.Area.TryGetValue(out var area);
        if (!known || !rk || !ready || !ak || area!.Length == 0)
        {
            return none;
        }
        var rows = unsorted!.OrderBy(a => a.Id).ToList();
        var wanted = SterilizeWanted(rows, herd);
        foreach (var a in rows)
        {
            if (!a.AllowedArea.TryGetValue(out var inArea) || inArea != area)
            {
                continue;
            }
            var qk = a.SterilizeQueued.TryGetValue(out var queued);
            if (!qk || queued)
            {
                return none;
            }
            if (a.Sterilized || !wanted.Contains(a.Id))
            {
                return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.AllowedArea };
            }
            return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.Sterilize };
        }
        foreach (var a in rows)
        {
            if (wanted.Contains(a.Id) && a.SupportsAreas.TryGetValue(out var supports) && supports)
            {
                return new HusbandryChoice { Animal = a.Id, Method = HusbandryMethod.AllowedArea, Argument = area };
            }
        }
        return none;
    }
}
