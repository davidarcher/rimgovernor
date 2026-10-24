namespace RimGovernor.Brain.Policy;


public static class BillWantedRules
{
    /// <summary>
    /// Whether any product of a bill is in <paramref name="wanted"/>: a finite bill matching none of an
    /// owner's current demand is stale whatever the owner's finding.
    /// </summary>
    public static bool BillWanted(IEnumerable<Resource> products, IReadOnlySet<Resource> wanted) => products.Any(wanted.Contains);

    /// <summary>
    /// The set of apparel and armor definitions a gear bill may still make: every replacement a pawn's
    /// loadout model names, blocked pawns included (a busy pawn still wants its garment), and for an armor
    /// definition every rung of its family. Stored stock is not netted, so the set only over-states what is
    /// wanted. Known is false while the census is unread.
    /// </summary>
    public static (HashSet<Resource>? Wanted, bool Known) GearBillsWanted(Fact<GearObservation> gear)
    {
        if (!gear.TryGetValue(out var v))
        {
            return (null, false);
        }

        var review = GearRules.ReviewGear(v);
        var wanted = new HashSet<Resource>();
        if (review.Recovered)
        {
            return (wanted, true);
        }

        foreach (var p in GearLoadoutRules.ModeledGearPawns(v, review.Loadouts))
        {
            foreach (var n in p.Replacements)
            {
                var (rungs, rungsOk) = GearArmorRules.ArmoryArmorRungs(n.Definition, ArmoryTier.Fabrication);
                if (rungsOk)
                {
                    foreach (var rung in rungs)
                    {
                        wanted.Add(rung);
                    }

                    continue;
                }

                wanted.Add(n.Definition);
            }
        }

        return (wanted, true);
    }

    /// <summary>The set of items the part demand names: a part bill making none of them has no operation waiting on it.</summary>
    public static HashSet<Resource> SurgeryPartsWanted(IEnumerable<SurgeryPart> parts)
    {
        var wanted = new HashSet<Resource>();
        foreach (var part in parts)
        {
            foreach (var item in part.Items)
            {
                wanted.Add(item);
            }
        }

        return wanted;
    }

    /// <summary>The set of weapon definitions the armory demand names.</summary>
    public static HashSet<Resource> WeaponsWanted(params IEnumerable<Amount>[] demand)
    {
        var wanted = new HashSet<Resource>();
        foreach (var amounts in demand)
        {
            foreach (var a in amounts)
            {
                if (a.Count > 0)
                {
                    wanted.Add(a.Resource);
                }
            }
        }

        return wanted;
    }
}
