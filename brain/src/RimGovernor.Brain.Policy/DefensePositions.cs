namespace RimGovernor.Brain.Policy;

/// <summary>
/// What SelectDefensivePositions needs to know about one hostile: the native lord evidence that says how the
/// raid intends to arrive, and how close it already stands to a colonist.
/// </summary>
public sealed record DefensiveThreatFacts
{
    public PawnId Id { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Humanlike { get; init; }

    public Fact<string> LordJobClass { get; init; }

    public Fact<string> LordToilClass { get; init; }

    public Fact<double> NearestColonistDistance { get; init; }

    /// <summary>The hostile's cell; against the layout's firing line and corridor direction it says whether the raid is still in front of the line.</summary>
    public Fact<Cell> Position { get; init; }

    /// <summary>A mechanoid: a mechanoid assault lord holds like a humanlike raid.</summary>
    public bool Mech { get; init; }
}

/// <summary>Sends one ranged defender to one firing cell and names the opponent it engages from there.</summary>
public sealed record DefensivePosition(PawnId Defender, Cell Cell, PawnId Target);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The M3 rule: only when the layout stands and every live hostile is an ordinary edge-arriving assault still
    /// in front of the line does the colony hold the line. Otherwise the caller falls back to SelectSquadDefense.
    /// Ranged, healthy, idle defenders take firing cells in cell order, one each, and engage the lowest-ID live
    /// hostile; unarmed or melee-only colonists are never positioned.
    /// </summary>
    public static (IReadOnlyList<DefensivePosition> Positions, bool Ok) SelectDefensivePositions(IReadOnlyList<Cell> firing, Rotation toward, IReadOnlyList<DefensiveThreatFacts> threats, IReadOnlyList<SquadDefenderFacts> defenders)
    {
        var (positions, refusal) = ExplainDefensivePositions(firing, toward, threats, defenders);
        return (positions, refusal == "");
    }

    /// <summary>SelectDefensivePositions with the reason it refused the hold: empty when positions were chosen.</summary>
    public static (IReadOnlyList<DefensivePosition> Positions, string Refusal) ExplainDefensivePositions(IReadOnlyList<Cell> firing, Rotation toward, IReadOnlyList<DefensiveThreatFacts> threats, IReadOnlyList<SquadDefenderFacts> defenders) =>
        ExplainDefensivePositionsIn(firing, toward, null, null, threats, defenders);

    /// <summary>
    /// Takes <paramref name="inside"/>, which reports a cell in a standing room (a hostile there is engaged,
    /// whatever its distance), and <paramref name="held"/>, which reports a hostile cell the hold's blockers stop
    /// at the choke. A hostile outside the walls, however close, is not engaged; only position counts.
    /// </summary>
    internal static (IReadOnlyList<DefensivePosition> Positions, string Refusal) ExplainDefensivePositionsIn(IReadOnlyList<Cell> firing, Rotation toward, Func<Cell, bool>? inside, Func<Cell, bool>? held, IReadOnlyList<DefensiveThreatFacts> threats, IReadOnlyList<SquadDefenderFacts> defenders)
    {
        if (firing.Count == 0)
        {
            return ([], "no firing cells");
        }
        if (threats.Count == 0)
        {
            return ([], "no threats");
        }
        var live = new List<DefensiveThreatFacts>();
        foreach (var t in threats)
        {
            var dk = t.Dead.TryGetValue(out var dead);
            var wk = t.Downed.TryGetValue(out var downed);
            if (!dk || !wk)
            {
                return ([], $"hostile {t.Id}: dead/downed unknown");
            }
            if (dead || downed)
            {
                continue;
            }
            var hk = t.Humanlike.TryGetValue(out var humanlike);
            var jk = t.LordJobClass.TryGetValue(out var job);
            var tk = t.LordToilClass.TryGetValue(out var toil);
            var pk = t.Position.TryGetValue(out var position);
            if (!t.Mech && (!hk || !humanlike))
            {
                return ([], $"hostile {t.Id}: not known humanlike");
            }
            if (!jk || !tk)
            {
                return ([], $"hostile {t.Id}: lord unknown (job \"{job}\" known={Lower(jk)}, toil \"{toil}\" known={Lower(tk)})");
            }
            if (!EdgeAssault(job!, toil!))
            {
                return ([], $"hostile {t.Id}: not an edge assault ({job}/{toil})");
            }
            if (!pk)
            {
                return ([], $"hostile {t.Id}: position unknown");
            }
            if (inside is not null && inside(position) && !(held is not null && held(position)))
            {
                return ([], $"hostile {t.Id}: inside room at ({position.X},{position.Z})");
            }
            if (BehindFiringLine(firing, toward, position))
            {
                return ([], $"hostile {t.Id}: at ({position.X},{position.Z}), at or behind the line");
            }
            live.Add(t);
        }
        if (live.Count == 0)
        {
            return ([], "no live hostiles");
        }
        live = live.OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToList();
        var pool = defenders.Where(d => SquadDefenderEligible(d) && Positive(d.RangedEquipped)).ToList();
        if (pool.Count == 0)
        {
            var ranged = 0;
            var why = new List<string>();
            foreach (var d in defenders)
            {
                if (Positive(d.RangedEquipped))
                {
                    ranged++;
                    why.Add(d.Id + ": " + string.Join(", ", SquadDefenderReasons(d)));
                }
            }
            return ([], $"no eligible ranged defender among {defenders.Count} ({ranged} ranged-equipped: {string.Join("; ", why)})");
        }
        // Shooters take the firing cells before the line holders do.
        pool = pool
            .OrderBy(d => d.FrontLine)
            .ThenBy(d => d.Id.Value, StringComparer.Ordinal)
            .ToList();
        var seen = new HashSet<Cell>();
        var output = new List<DefensivePosition>();
        foreach (var cell in firing)
        {
            if (seen.Contains(cell) || output.Count == pool.Count || output.Count == MaxSquadDefenders)
            {
                continue;
            }
            seen.Add(cell);
            output.Add(new DefensivePosition(pool[output.Count].Id, cell, live[0].Id));
        }
        return (output, "");
    }

    /// <summary>
    /// Says whether a standing hold must be abandoned for squad defense: some live hostile is known to be at or
    /// behind the line's cover row, or its lord evidence is known to be anything but an edge assault. Unknown
    /// evidence never abandons a hold.
    /// </summary>
    public static bool HoldCompromised(IReadOnlyList<Cell> firing, Rotation toward, IReadOnlyList<DefensiveThreatFacts> threats)
    {
        foreach (var t in threats)
        {
            if (!t.Dead.TryGetValue(out var dead) || !t.Downed.TryGetValue(out var downed) || dead || downed)
            {
                continue;
            }
            if (t.LordJobClass.TryGetValue(out var job) && t.LordToilClass.TryGetValue(out var toil) && !EdgeAssault(job, toil))
            {
                return true;
            }
            if (t.Position.TryGetValue(out var position) && BehindFiringLine(firing, toward, position))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports whether the cell lies at or past the firing line's cover row along the corridor direction: the
    /// sandbags stand one cell in front of each firing cell, so anything projected onto toward at or beyond the
    /// nearest cover row has crossed the line. Without a corridor direction there is no line to be behind.
    /// </summary>
    public static bool BehindFiringLine(IReadOnlyList<Cell> firing, Rotation toward, Cell cell)
    {
        if (!TowardVector(toward, out var v) || firing.Count == 0)
        {
            return false;
        }
        int Project(Cell c) => (c.X * v.X) + (c.Z * v.Z);
        var cover = Project(firing[0]) - 1;
        for (var i = 1; i < firing.Count; i++)
        {
            var p = Project(firing[i]) - 1;
            if (p < cover)
            {
                cover = p;
            }
        }
        return Project(cell) >= cover;
    }

    internal static bool TowardVector(Rotation r, out Cell v)
    {
        switch (r)
        {
            case Rotation.North:
                v = Directions[0];
                return true;
            case Rotation.East:
                v = Directions[1];
                return true;
            case Rotation.South:
                v = Directions[2];
                return true;
            case Rotation.West:
                v = Directions[3];
                return true;
            default:
                v = default;
                return false;
        }
    }

    /// <summary>Recognises the native lord classes of a walk-in assault. Sapper, breach, siege and drop toils are excluded by name.</summary>
    internal static bool EdgeAssault(string job, string toil)
    {
        if (job != "LordJob_AssaultColony")
        {
            return false;
        }
        foreach (var bypass in new[] { "Sapper", "Breach", "Siege", "Drop" })
        {
            if (toil.Contains(bypass, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return toil.StartsWith("LordToil_", StringComparison.Ordinal);
    }

    private static string Lower(bool b) => b ? "true" : "false";
}
