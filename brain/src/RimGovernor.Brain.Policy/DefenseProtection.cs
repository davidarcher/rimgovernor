using System.Globalization;
using Facts = RimGovernor.Brain.GameFacts.Observations;
using Orders = RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One protection component: assessed from read facts (a known zero included), or unassessed with the fact named. 7.3 never compares an unassessed term as a number.</summary>
public abstract record DefenseTerm
{
    private DefenseTerm() { }

    public sealed record Assessed(double Value) : DefenseTerm;

    public sealed record Unassessed(string Fact) : DefenseTerm;
}

/// <summary>A planned turret's draw on its power network: the network and the watts it adds.</summary>
public sealed record DefenseDraw(string Network, double Watts);

/// <summary>The routine positions (DefenseGround holders) within raider weapon range of a ground raid's observed walk, without the candidate and with its walls.</summary>
public sealed record DefenseCivilians(IReadOnlyList<string> Before, IReadOnlyList<string> After);

/// <summary>
/// What one defense candidate protects, from the batched probe (RG-HABITAT-009, task 7.2), beside RaidExposure's
/// points-to-capacity bin (which it extends, not replaces). Every component is assessed from read facts or unassessed
/// with the fact named; nothing is guessed:
/// <list type="bullet">
/// <item><see cref="Covered"/>: the fraction of the probed approach cells a staffed firing cell or a ready turret reaches
/// (distance within its verb's [minRange, range], line of sight where the verb needs it).</item>
/// <item><see cref="Shots"/>: shots per second of the staffed shooters and ready turrets that reach an approach cell, each
/// burstShotCount over its cycle (turret burst warmup + verb warmup + cooldown + burst seconds).</item>
/// <item><see cref="Cover"/>: the mean block chance the staffed firing cells' cover gives against the approach cells they see.</item>
/// <item><see cref="Staffing"/>: the shooters the candidate seats (its firing cells and mannable turrets); a shortfall is
/// <c>staffing:&lt;needed&gt;/&lt;available&gt;</c>.</item>
/// <item><see cref="Ammunition"/>: 0 when nothing loads any (small arms and gun turrets fire without it), else the shells
/// in stock; a mortar with none loaded or stocked is <c>shells:&lt;turret&gt;</c>.</item>
/// <item><see cref="Power"/>: the powered turrets; an unpowered one protects nothing and is named <c>power:&lt;turret&gt;</c>.</item>
/// <item><see cref="Exposure"/>: the routine positions within the raiders' weapon range of the raid walk with the candidate's
/// walls (<see cref="Civilians"/> holds both walks); melee raiders (range 0) expose only the walk's own cells.</item>
/// </list>
/// </summary>
public sealed record DefenseProtection
{
    public string Candidate { get; init; } = "";

    public DefenseTerm Covered { get; init; } = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);

    public DefenseTerm Shots { get; init; } = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);

    public DefenseTerm Cover { get; init; } = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);

    public DefenseTerm Staffing { get; init; } = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);

    public DefenseTerm Ammunition { get; init; } = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);

    public DefenseTerm Power { get; init; } = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);

    public DefenseTerm Exposure { get; init; } = new DefenseTerm.Unassessed(DefenseProtectionRules.ProbeUnread);

    public DefenseCivilians Civilians { get; init; } = new([], []);

    /// <summary>The planned turrets' draws on their networks, which the comparison charges their network's unserved share (task 7.3).</summary>
    public IReadOnlyList<DefenseDraw> Draws { get; init; } = [];

    /// <summary>The named states the components carry (staffing, power, shells, fog), in finding order.</summary>
    public IReadOnlyList<string> States { get; init; } = [];
}

/// <summary>
/// The defense candidates' probe and protection (task 7.2): builds the batched probe rows (milestone 1.2's pattern: one row
/// per standing candidate, its id the candidate's key, bounded and in the generator's order), refuses a reply row no
/// request asked for, and assesses each candidate's <see cref="DefenseProtection"/> from the joined rows. No combat is
/// simulated and no raid is spawned or predicted beyond the walks raids were observed taking.
/// </summary>
public static class DefenseProtectionRules
{
    /// <summary>The candidate was asked for and the read is absent.</summary>
    public const string ProbeUnread = "defense_probe:unread";

    /// <summary>No ground raid was observed since load: there is no walk to probe.</summary>
    public const string ArrivalUnobserved = "arrival_edges:none_observed_since_load";

    public const string Fogged = "sight:fogged";

    /// <summary>The walks probed: the newest raids', each down to its last cells before the colony or the candidate's barrier.</summary>
    internal const int ProbedRaids = 3;

    /// <summary>One probed approach cell every this many walked cells, back from the walk's end.</summary>
    internal const int ApproachStride = 3;

    /// <summary>The refusal of a reply row for a candidate the request did not ask for.</summary>
    public static string UnknownCandidate(string id) => "defense_probe:unknown_candidate:" + id;

    /// <summary>
    /// A raid's walk, cell by cell along its samples (the game's integer line between consecutive samples), up to the first
    /// barrier cell it reaches: past a wall or a door the raid does not walk on unbroken.
    /// </summary>
    public static IReadOnlyList<Cell> Walk(IReadOnlyList<Cell> trail, IReadOnlySet<Cell> barriers)
    {
        var output = new List<Cell>();
        for (var i = 0; i < trail.Count; i++)
        {
            var segment = i == 0 ? [trail[0]] : Line(trail[i - 1], trail[i]).Skip(1);
            foreach (var c in segment)
            {
                if (barriers.Contains(c))
                {
                    return output;
                }
                output.Add(c);
            }
        }
        return output;
    }

    private static IEnumerable<Cell> Line(Cell a, Cell b)
    {
        int dx = Math.Abs(b.X - a.X), dz = -Math.Abs(b.Z - a.Z);
        int sx = a.X < b.X ? 1 : -1, sz = a.Z < b.Z ? 1 : -1;
        int err = dx + dz, x = a.X, z = a.Z;
        while (true)
        {
            yield return new Cell(x, z);
            if (x == b.X && z == b.Z)
            {
                yield break;
            }
            var e2 = 2 * err;
            if (e2 >= dz)
            {
                err += dz;
                x += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                z += sz;
            }
        }
    }

    /// <summary>
    /// The approach cells probed for a candidate: of the newest <see cref="ProbedRaids"/> walks cut at its barriers, every
    /// <see cref="ApproachStride"/>th cell back from each walk's end, at most <see cref="Orders.DefenseProbeRequest.MaxApproach"/>
    /// in all, distinct, in walk order.
    /// </summary>
    public static IReadOnlyList<Cell> Approach(DefenseThreat threat, DefenseCandidate c)
    {
        var barriers = c.Barriers.ToHashSet();
        var per = Orders.DefenseProbeRequest.MaxApproach / ProbedRaids;
        var output = new List<Cell>();
        foreach (var trail in threat.Trails.TakeLast(ProbedRaids))
        {
            var walk = Walk(trail, barriers);
            var picked = new List<Cell>();
            for (var i = walk.Count - 1; i >= 0 && picked.Count < per; i -= ApproachStride)
            {
                picked.Add(walk[i]);
            }
            picked.Reverse();
            output.AddRange(picked);
        }
        return [.. output.Distinct().Take(Orders.DefenseProbeRequest.MaxApproach)];
    }

    /// <summary>
    /// The probe rows: one per candidate not excluded, with its key as id, its first firing cells, its approach and its
    /// pieces (one per cell, the first structure there); none while no ground raid was observed.
    /// </summary>
    public static IReadOnlyList<Orders.DefenseCandidateProbe> Probes(IReadOnlyList<DefenseCandidate> candidates)
    {
        var output = new List<Orders.DefenseCandidateProbe>();
        foreach (var c in candidates)
        {
            if (c.Standing is DefenseCandidateStanding.Excluded || c.Threat.Trails.Count == 0 || output.Count == Orders.DefenseProbeRequest.MaxCandidates)
            {
                continue;
            }
            var approach = Approach(c.Threat, c);
            if (approach.Count == 0)
            {
                continue;
            }
            output.Add(new Orders.DefenseCandidateProbe
            {
                Id = c.Key,
                Firing = [.. c.Firing.Take(Orders.DefenseProbeRequest.MaxFiring)],
                Approach = [.. approach],
                Pieces = [.. c.Structures.DistinctBy(s => s.Cell).Take(Orders.DefenseProbeRequest.MaxPieces).Select(s => new Orders.DefenseProbePiece(s.Cell, s.Definition))],
            });
        }
        return output;
    }

    /// <summary>
    /// Every candidate's protection from the probe it was asked and the read's rows joined by candidate id. A reply row for
    /// a candidate the request did not ask for is refused (<see cref="RequiredFactException"/> naming it). A candidate with
    /// no observed walk carries <see cref="ArrivalUnobserved"/> in every term; one excluded or not asked carries its reason;
    /// one asked but absent from the reply carries <c>defense_probe:&lt;id&gt;</c>.
    /// </summary>
    public static IReadOnlyList<DefenseProtection> Protections(IReadOnlyList<DefenseCandidate> candidates, IReadOnlyList<Orders.DefenseCandidateProbe> asked,
        Facts.DefenseProbeSnapshot? read, DefenseGround ground)
    {
        var probes = asked.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var answers = new Dictionary<string, Facts.DefenseProbeAnswer>(StringComparer.Ordinal);
        foreach (var a in read?.Candidates ?? [])
        {
            if (!probes.ContainsKey(a.Id))
            {
                throw new RequiredFactException(UnknownCandidate(a.Id));
            }
            answers[a.Id] = a;
        }
        var output = new List<DefenseProtection>(candidates.Count);
        foreach (var c in candidates)
        {
            if (c.Threat.Trails.Count == 0)
            {
                output.Add(Unassessed(c.Key, ArrivalUnobserved));
                continue;
            }
            var civilians = Civilians(c, ground);
            if (!probes.TryGetValue(c.Key, out var probe))
            {
                var reason = c.Standing is DefenseCandidateStanding.Excluded x ? x.Reason : "defense_probe:no_approach";
                output.Add(Unassessed(c.Key, reason) with { Civilians = civilians });
                continue;
            }
            if (read is null)
            {
                output.Add(Unassessed(c.Key, ProbeUnread) with { Civilians = civilians });
                continue;
            }
            if (!answers.TryGetValue(c.Key, out var answer))
            {
                output.Add(Unassessed(c.Key, "defense_probe:" + c.Key) with { Civilians = civilians });
                continue;
            }
            output.Add(Assess(c.Key, probe, answer, read, civilians));
        }
        return output;
    }

    private static DefenseProtection Unassessed(string candidate, string fact)
    {
        var term = new DefenseTerm.Unassessed(fact);
        return new DefenseProtection
        {
            Candidate = candidate,
            Covered = term,
            Shots = term,
            Cover = term,
            Staffing = term,
            Ammunition = term,
            Power = term,
            Exposure = term,
            States = [fact],
        };
    }

    /// <summary>
    /// The routine positions within the raiders' weapon range (<see cref="DefenseThreat.Range"/>; 0, melee, is the walk
    /// itself) of the observed walks, uncut and cut at the candidate's barriers.
    /// </summary>
    public static DefenseCivilians Civilians(DefenseCandidate c, DefenseGround ground)
    {
        var barriers = c.Barriers.ToHashSet();
        var before = ground.Routine(InRange(c.Threat.Trails.SelectMany(t => Walk(t, new HashSet<Cell>())), c.Threat.Range));
        var after = ground.Routine(InRange(c.Threat.Trails.SelectMany(t => Walk(t, barriers)), c.Threat.Range));
        return new DefenseCivilians(before, after);
    }

    /// <summary>The cells within <paramref name="range"/> (Euclidean, the game's verb range) of any walked cell, each once.</summary>
    public static IReadOnlySet<Cell> InRange(IEnumerable<Cell> walk, double range)
    {
        var cells = walk.ToHashSet();
        if (range <= 0)
        {
            return cells;
        }
        var r = (int)Math.Floor(range);
        var squared = range * range;
        var output = new HashSet<Cell>(cells);
        foreach (var c in cells)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                for (var dz = -r; dz <= r; dz++)
                {
                    if (dx * dx + dz * dz <= squared)
                    {
                        output.Add(new Cell(c.X + dx, c.Z + dz));
                    }
                }
            }
        }
        return output;
    }

    /// <summary>Whether a verb held at <paramref name="line"/>'s firing cell reaches its approach cell: in its range band, with sight where it needs it.</summary>
    private static bool Reaches(Facts.DefenseVerb verb, Facts.DefenseSightLine.Traced line) =>
        line.Distance <= verb.Range && line.Distance >= verb.MinRange && (!verb.LineOfSight || line.LineOfSight);

    /// <summary>Shots per second of a verb: its burst over its whole cycle, or null when the cycle the read gives is not positive.</summary>
    private static double? Rate(Facts.DefenseVerb verb, double burstWarmup)
    {
        var cycle = burstWarmup + verb.Warmup + verb.Cooldown + verb.BurstSeconds;
        return cycle > 0 && verb.BurstShots > 0 ? verb.BurstShots / cycle : null;
    }

    private static DefenseProtection Assess(string candidate, Orders.DefenseCandidateProbe probe, Facts.DefenseProbeAnswer answer, Facts.DefenseProbeSnapshot read, DefenseCivilians civilians)
    {
        var states = new List<string>();
        var shooters = read.Shooters.Where(s => s.Verb is not null && !s.ViolentDisabled && !s.Downed)
            .OrderByDescending(s => s.Shooting).ThenBy(s => s.Pawn.Value, StringComparer.Ordinal).ToList();
        var prefix = candidate + "/";
        var turrets = read.Turrets.Where(t => !t.Planned || t.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var mannable = turrets.Where(t => t.Mannable).ToList();
        var needed = probe.Firing.Count + mannable.Count;
        DefenseTerm staffing = new DefenseTerm.Assessed(needed);
        if (needed > shooters.Count)
        {
            var fact = string.Create(CultureInfo.InvariantCulture, $"staffing:{needed}/{shooters.Count}");
            staffing = new DefenseTerm.Unassessed(fact);
            states.Add(fact);
        }

        // Shooters seat the firing cells in order, then man the mannable turrets.
        var seated = new List<(Cell Cell, Facts.DefenseVerb Verb)>();
        var next = 0;
        foreach (var cell in probe.Firing)
        {
            if (next < shooters.Count)
            {
                seated.Add((cell, shooters[next++].Verb!));
            }
        }
        var manned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in mannable)
        {
            if (next < shooters.Count)
            {
                manned.Add(t.Id);
                next++;
            }
        }

        var powered = 0;
        var drawn = new Dictionary<string, double>(StringComparer.Ordinal);
        var ready = new List<Facts.DefenseTurretProbe>();
        var shellTurrets = 0;
        string? shellsMissing = null;
        var draws = new List<DefenseDraw>();
        foreach (var t in turrets)
        {
            if (t.Planned && t.Power is Facts.BenchPower.Connected d)
            {
                draws.Add(new DefenseDraw(d.Network, d.AdditionalWatts));
            }
            // A standing turret is on when it adds no draw (CompPowerTrader.PowerOn); planned ones share their network's surplus in
            // order; an unconnected one is off.
            var on = t.Power is Facts.BenchPower.Unneeded
                || (t.Power is Facts.BenchPower.Connected k && (!t.Planned ? k.AdditionalWatts == 0
                    : (drawn[k.Network] = drawn.GetValueOrDefault(k.Network) + k.AdditionalWatts) <= k.SurplusWatts));
            if (!on)
            {
                states.Add("power:" + t.Id);
                continue;
            }
            powered++;
            if (t.Ammo is Facts.DefenseTurretAmmo.Shells s)
            {
                shellTurrets++;
                if (s.Loaded is null && read.Shells <= 0)
                {
                    shellsMissing ??= "shells:" + t.Id;
                    states.Add("shells:" + t.Id);
                    continue;
                }
            }
            if (t.Mannable && !manned.Contains(t.Id))
            {
                continue;
            }
            ready.Add(t);
        }
        DefenseTerm ammunition = shellsMissing is { } shells ? new DefenseTerm.Unassessed(shells)
            : new DefenseTerm.Assessed(shellTurrets == 0 ? 0 : read.Shells);

        var exposure = new DefenseTerm.Assessed(civilians.After.Count);
        if (answer.Lines.Any(l => l is Facts.DefenseSightLine.Fogged))
        {
            var fog = new DefenseTerm.Unassessed(Fogged);
            states.Add(Fogged);
            return new DefenseProtection
            {
                Candidate = candidate, Covered = fog, Shots = fog, Cover = fog, Staffing = staffing, Ammunition = ammunition,
                Power = new DefenseTerm.Assessed(powered), Exposure = exposure, Civilians = civilians, Draws = draws, States = states,
            };
        }

        var lines = answer.Lines.OfType<Facts.DefenseSightLine.Traced>().ToLookup(l => l.From);
        var covered = new HashSet<Cell>();
        var shots = 0.0;
        var cover = new List<double>();
        foreach (var (cell, verb) in seated)
        {
            var reached = lines[cell].Where(l => Reaches(verb, l)).ToList();
            cover.AddRange(lines[cell].Where(l => l.LineOfSight).Select(l => l.Cover));
            if (reached.Count == 0)
            {
                continue;
            }
            covered.UnionWith(reached.Select(l => l.To));
            if (Rate(verb, 0) is not { } rate)
            {
                states.Add(string.Create(CultureInfo.InvariantCulture, $"verb_cycle:{cell.X},{cell.Z}"));
                continue;
            }
            shots += rate;
        }
        foreach (var t in ready)
        {
            var reached = lines[t.Cell].Where(l => Reaches(t.Verb, l)).ToList();
            if (reached.Count == 0)
            {
                continue;
            }
            covered.UnionWith(reached.Select(l => l.To));
            if (Rate(t.Verb, t.BurstWarmup) is not { } rate)
            {
                states.Add("verb_cycle:" + t.Id);
                continue;
            }
            shots += rate;
        }
        return new DefenseProtection
        {
            Candidate = candidate,
            Covered = new DefenseTerm.Assessed(probe.Approach.Count == 0 ? 0 : (double)covered.Count(probe.Approach.Contains) / probe.Approach.Count),
            Shots = new DefenseTerm.Assessed(shots),
            Cover = new DefenseTerm.Assessed(cover.Count == 0 ? 0 : cover.Average()),
            Staffing = staffing,
            Ammunition = ammunition,
            Power = new DefenseTerm.Assessed(powered),
            Exposure = exposure,
            Civilians = civilians,
            Draws = draws,
            States = states,
        };
    }
}
