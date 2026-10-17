using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/sparring-*</c>: bout formation (#2708) and the spar job (#2709), one ring per case: who
/// spars with whom, and what the fight does to the gear. The bout registry, matchmaking, the work giver and the job driver
/// are native. test/sparring_prepare stays a bespoke op (violence-capable colonists at given Melee levels with a Minor
/// passion and training-only work, painstopper, a draft or a mid-bout event, which the stage primitives cannot express);
/// test/sparring_inspect is a per-tick recorder, because the game runs thousands of ticks between two reads, so each case
/// asserts what the recorder saw, not a poll.
/// </summary>
internal static class LabSparring
{
    internal const string PrepareTool = "test/sparring_prepare";
    internal const string InspectTool = "test/sparring_inspect";

    // The most exchanges a session may make (SparringStopRule).
    private const int ExchangeCap = 10;

    // The ring row sits this far into the site: the colonists start 30 cells south of it, the watch bystanders 12 north.
    internal const int RingZ = 31;

    /// <summary>The ring's site: markers 2 cells apart along the ring row, colonists in a row 30 cells south, spectators 12 cells north.</summary>
    internal static readonly Fx.StageSpec Site = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 12,
            Height = 46,
            Radius = 40,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged],
        },
    };

    internal static readonly FixtureOp<Ring, Fx.SparringPrepared> Prepare = new(
        PrepareTool,
        r => ServedOps.Args(
            ("x", r.At.X),
            ("z", r.At.Z),
            ("eligible", r.Eligible),
            ("levels", r.Levels),
            ("ineligible", r.Ineligible),
            ("shooter", r.Shooter),
            ("draftOnGather", r.Draft),
            ("scenario", r.Scenario)),
        ServedOps.Reply<Fx.SparringPrepared>(PrepareTool));

    internal static readonly FixtureOp<NoArgs, Fx.SparringRead> Inspect = ServedOps.Read<Fx.SparringRead>(InspectTool);

    /// <summary>One ring: eligible colonists at the Melee levels, the never-eligible pair, a shooting-chosen trainee, a draft while the first bout gathers, and the mid-bout scenario.</summary>
    internal sealed record Ring(Fx.StageCell At, int Eligible, string Levels, bool Ineligible = false, bool Shooter = false, bool Draft = false, string Scenario = "");

    /// <summary>
    /// One case's ring and what it must show. Sizes are the first round's bout sizes, largest first; a Scenario names a
    /// fight case (#2709), which waits for every spar job to end instead of the bouts to finish.
    /// </summary>
    private sealed record Row(string Name, int Eligible, string Levels, int[] Sizes, string Scope, bool Ineligible = false, bool Shooter = false, bool Draft = false, string Scenario = "");

    // Bout formation and teams. Two and three eligible colonists are covered by five-two-bouts (a free-for-all 3 and a
    // pair formed at once).
    private static readonly Row[] Rows =
    [
        new("five-two-bouts", 5, "7,6,5,4,3", [3, 2],
            "Five eligible colonists at the ring (#2708) form a free-for-all of 3 and a pair at once with no order from the brain; every pawn and marker sits in at most one bout and every fighter's opponent is on another team."),
        new("four", 4, "7,5,4,2", [4],
            "Four eligible colonists (#2708) form one 2v2 bout in which the highest and the lowest Melee are teammates."),
        new("ineligible", 2, "5,4", [2],
            "A colonist above the Melee ceiling and a drafted one (#2708) are never seated; the two eligible colonists form their pair.", Ineligible: true),
        new("shooter-never-seated", 3, "6,4,2", [3],
            "A trainee whose chosen skill is Shooting (#2708) is never seated while the three melee-chosen colonists form their bout.", Shooter: true),
        new("drafted-while-gathering", 4, "7,5,4,2", [3],
            "A colonist drafted while its bout gathers (#2708) is dropped and the remaining three fight on.", Draft: true),

        // The spar job end to end (#2709): swap at the marker into the practice set (own gear held in the inventory), the
        // vanilla melee verb and XP, the struck pawn neither flees nor fights back, and the gear restored however the job ends.
        new("fight-cap", 2, "5,4", [2],
            "A painless bout (#2709) ends at the 10-exchange cap: each fighter swapped at its marker into the practice set with its own gear held, "
            + "swung through the vanilla melee verb and was paid melee XP through the native learning callback, the struck pawn neither fled nor fought back, "
            + "the gear is restored, both hold the trained-with thought (#2710), Joy and Social tolerance rose, and the fighter that began with full Joy "
            + "still fought to the cap (#2731).", Scenario: "cap"),
        new("fight-pain", 3, "6,5,4", [3],
            "A fighter hurt past the pain stop rule (#2709) leaves the bout with its gear restored while the other two fight on.", Scenario: "pain"),
        new("fight-bleed", 2, "5,4", [2],
            "A fighter bleeding past the stop rule (#2709) leaves the bout with its gear restored.", Scenario: "bleed"),
        new("fight-draft", 3, "6,5,4", [3],
            "A fighter drafted mid-bout (#2709) leaves with its gear restored and no trained-with thought (#2710) while the rest fight on and earn it.", Scenario: "draft"),
        new("fight-kill", 3, "6,5,4", [3],
            "A fighter killed mid-bout (#2709) leaves no practice gear and loses no original piece; the rest fight on, and the dead one holds no trained-with thought (#2710).", Scenario: "kill"),
        new("fight-raid", 3, "6,5,4", [3],
            "A real attacker beside a sparring fighter (#2711) ends its spar job; every fighter's gear is restored with no practice gear left behind, and the attacker does not kill the victim.", Scenario: "raid"),
    ];

    internal static IEnumerable<TypedCase> Cases() => Rows.Select(row => new TypedCase
    {
        Name = "lab/sparring-" + row.Name,
        Scope = row.Scope + " A unit test cannot see the work giver, the registry, the walk, the swap or the vanilla verb.",
        Ticks = 20000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 8 },
        Stage = Site,
        Ops = row.Scenario == "cap" ? [PrepareTool, InspectTool, LabSkillLearning.VerifyTool] : [PrepareTool, InspectTool],
        QuietWorld = true,
        Keep = ["Joy"],
        Run = (s, ct) => RunAsync(s, row, ct),
    });

    /// <summary>The ring cell on the staged site.</summary>
    internal static Fx.StageCell RingCell(TypedSession s)
    {
        var site = s.Staged?.Site ?? throw new AcceptanceException("test/stage replied no site");
        return new Fx.StageCell(site.X, site.Z + RingZ);
    }

    private static async Task RunAsync(TypedSession s, Row row, CancellationToken cancellationToken)
    {
        // A freshly loaded world has no bouts: the registry is runtime only.
        var fresh = await s.FixtureAsync("sparring-inspect-fresh", Inspect, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        if (fresh.Active != 0)
        {
            throw new AcceptanceException($"registry not empty on a fresh world: {fresh.Active} bouts");
        }

        var learning = row.Scenario == "cap";
        if (learning)
        {
            _ = await s.FixtureAsync("learning-start", LabSkillLearning.Verify, "start", cancellationToken).ConfigureAwait(false);
        }

        var prepared = await s.FixtureAsync(
            "sparring-prepare",
            Prepare,
            new Ring(RingCell(s), row.Eligible, row.Levels, row.Ineligible, row.Shooter, row.Draft, row.Scenario),
            cancellationToken).ConfigureAwait(false);
        Fx.SparringRead? last = null;
        try
        {
            _ = await s.RunUntilAsync("sparring-" + row.Name, s.TicksLeft, async ct =>
            {
                var got = await s.FixtureAsync("sparring-inspect", Inspect, NoArgs.Value, ct).ConfigureAwait(false);
                last = got;
                if (row.Scenario.Length > 0)
                {
                    var ended = got.Pawns.Count(p => p.Ended);

                    // The attacker is removed a few hundred ticks after the victim's job ends.
                    var finished = ended >= row.Eligible && (row.Scenario != "raid" || got.Raid?.RaiderGone == true);
                    return (Wait.Signature(got.Active, ended), finished);
                }

                var done = got.Bouts.Count(b => b.Fighting is not null && b.Finished);
                return (Wait.Signature(got.Active, done), done >= row.Sizes.Length);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            s.Record("sparring", last is null ? null : ServedOps.Json(last));
        }

        CheckStage(row, prepared, last!);
        if (row.Scenario == "raid")
        {
            CheckRaid(last!);
        }
        else if (row.Scenario.Length > 0)
        {
            CheckFight(row, last!);
        }

        if (learning)
        {
            await LabSkillLearning.VerifyCallbacksAsync(s, "sparring", cancellationToken).ConfigureAwait(false);
        }
    }

    // A real attacker beside a sparring pawn (#2711). The victim's spar job ends (the stop rule, or it is downed) and its
    // own gear is back; the numbers it saw go to the report.
    private static void CheckRaid(Fx.SparringRead got)
    {
        var raid = got.Raid ?? throw new AcceptanceException("no attacker was ever spawned");
        foreach (var p in got.Pawns)
        {
            if (!p.Ended)
            {
                throw new AcceptanceException($"{p.Id}'s spar job never ended");
            }

            if (!p.NoPractice || !p.Originals)
            {
                throw new AcceptanceException($"{p.Id} left practice gear behind or lost a piece");
            }

            if (!p.Dead && !p.Restored)
            {
                throw new AcceptanceException($"{p.Id}'s gear is not restored: {p.GearDiff}");
            }

            if (p.Id == raid.Victim && p.Dead)
            {
                throw new AcceptanceException($"the attacker killed {p.Id}");
            }
        }
    }

    // What the spar job did in a fight case: the swap at the marker, the gear after the job ended, vanilla XP, and the
    // scenario's own claim. A pawn that never spars (the case's eligible set is all of them) fails.
    private static void CheckFight(Row row, Fx.SparringRead got)
    {
        var pawns = got.Pawns;
        if (pawns.Count != row.Eligible)
        {
            throw new AcceptanceException($"{pawns.Count} pawns tracked, want {row.Eligible}");
        }

        if (got.Abandoned != 0)
        {
            throw new AcceptanceException($"a fighter dropped its spar job {got.Abandoned} times (the struck pawn must not flee or abandon)");
        }

        if (got.AttackJobs != 0)
        {
            throw new AcceptanceException($"a fighter took a real AttackMelee job {got.AttackJobs} times (meleeThreat reaction)");
        }

        foreach (var p in pawns)
        {
            CheckFighter(row, got, p);
        }

        var by = pawns.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
        switch (row.Scenario)
        {
            case "cap":
                if (pawns.FirstOrDefault(p => p.Stop is "Pain" or "Bleeding") is { } hurt)
                {
                    throw new AcceptanceException($"{hurt.Id} stopped for {hurt.Stop} with no pain possible");
                }

                if (!pawns.Any(p => p.Stop == "Exchanges" && p.Exchanges == ExchangeCap))
                {
                    throw new AcceptanceException($"no pawn ended at the {ExchangeCap}-exchange cap");
                }

                if (got.Struck == 0)
                {
                    throw new AcceptanceException("no blow landed, so the struck pawn was never tested");
                }

                break;
            case "pain" or "bleed":
                var want = row.Scenario == "pain" ? "Pain" : "Bleeding";
                if (!by.TryGetValue(got.Injected, out var first) || first.Stop != want)
                {
                    throw new AcceptanceException($"injured \"{got.Injected}\" did not leave for {want}");
                }

                if (row.Eligible > 2 && pawns.Where(p => p.Id != got.Injected).Sum(p => p.Exchanges) == 0)
                {
                    throw new AcceptanceException($"the rest of the bout did not fight on after {got.Injected} left");
                }

                break;
            case "draft" or "kill":
                if (!by.TryGetValue(got.Hit, out var victim) || victim.Exchanges < 2)
                {
                    throw new AcceptanceException($"\"{got.Hit}\" was not hit mid-bout after swinging");
                }

                if (row.Scenario == "draft" && !victim.Drafted)
                {
                    throw new AcceptanceException($"{got.Hit} was not drafted");
                }

                if (pawns.Where(p => p.Id != got.Hit).Sum(p => p.Exchanges) <= victim.Exchanges)
                {
                    throw new AcceptanceException($"the rest of the bout did not fight on after {got.Hit} left");
                }

                break;
        }
    }

    private static void CheckFighter(Row row, Fx.SparringRead got, Fx.SparringPawn p)
    {
        var killed = row.Scenario == "kill" && p.Id == got.Hit;
        if (p.Dead != killed)
        {
            throw new AcceptanceException($"{p.Id} dead={p.Dead}, want {killed}");
        }

        if (!p.Swapped || !p.SwapSeen)
        {
            throw new AcceptanceException($"{p.Id} never swapped into practice gear: {p.Trail}");
        }

        if (!p.SwapAtMarker)
        {
            throw new AcceptanceException($"{p.Id} did not swap at its marker");
        }

        if (!p.OriginalsHeld)
        {
            throw new AcceptanceException($"{p.Id}'s own weapon and apparel were not held in its inventory while it wore practice gear");
        }

        if (!p.NoPractice)
        {
            throw new AcceptanceException($"{p.Id} left practice gear behind");
        }

        if (!p.Originals)
        {
            throw new AcceptanceException($"{p.Id} lost an original weapon or apparel piece");
        }

        if (!killed && !p.Restored)
        {
            throw new AcceptanceException($"{p.Id}'s gear is not restored: {p.GearDiff}");
        }

        if (!p.ThreatNull)
        {
            throw new AcceptanceException($"{p.Id} still had a meleeThreat when its job ended");
        }

        // #2731: a fighter gains Social joy over the bout (tolerance rises with it); one that started with a full Joy need
        // keeps fighting to the normal end.
        if (p.JoyStart >= 0.99 && (p.Exchanges == 0 || !p.Swapped))
        {
            throw new AcceptanceException($"{p.Id} began with full Joy and did not fight (exchanges {p.Exchanges})");
        }

        var rising = !p.Dead && p.Exchanges > 0 && p.JoyStart >= 0 && p.JoyStart < 0.99;
        if (rising && p.JoyEnd <= p.JoyStart)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{p.Id} Joy did not rise over the bout ({p.JoyStart:F3} to {p.JoyEnd:F3})"));
        }

        if (rising && p.ToleranceEnd <= p.ToleranceStart)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{p.Id} Social tolerance did not rise ({p.ToleranceStart:F4} to {p.ToleranceEnd:F4})"));
        }

        if (p.Exchanges > ExchangeCap)
        {
            throw new AcceptanceException($"{p.Id} made {p.Exchanges} exchanges, past the cap");
        }

        if (p.Exchanges > 0 && !p.Xp)
        {
            throw new AcceptanceException($"{p.Id} swung {p.Exchanges} times and was paid no melee XP");
        }

        // #2710: a member whose session was completed (cap, stop rule, bout end) holds the trained-with thought; one drafted
        // or killed mid-bout does not.
        if (p.TrainedWith != (!killed && !(row.Scenario == "draft" && p.Id == got.Hit)))
        {
            throw new AcceptanceException($"{p.Id} trained-with thought is {p.TrainedWith} after a \"{row.Scenario}\" case");
        }
    }

    private static void CheckStage(Row row, Fx.SparringPrepared prepared, Fx.SparringRead got)
    {
        if (got.DoubleBooked != 0)
        {
            throw new AcceptanceException($"a pawn or marker was in two bouts at once {got.DoubleBooked} times");
        }

        if (row.Shooter)
        {
            if (prepared.Shooter.Count != 1)
            {
                throw new AcceptanceException($"the ring staged {prepared.Shooter.Count} shooters, want 1");
            }

            if (got.Seated.FirstOrDefault(prepared.Shooter.Contains) is { } shooter)
            {
                throw new AcceptanceException($"shooting-chosen {shooter} was seated in a bout");
            }

            if (got.Seated.Count < row.Eligible)
            {
                throw new AcceptanceException($"the melee-chosen pawns were not all seated: {got.Seated.Count} of {row.Eligible}");
            }
        }

        if (got.Seated.FirstOrDefault(id => !prepared.Eligible.Contains(id)) is { } stranger)
        {
            throw new AcceptanceException($"{stranger} was seated but is not eligible");
        }

        var bouts = got.Bouts.OrderBy(b => b.Id).ToList();
        if (bouts.Count < row.Sizes.Length)
        {
            throw new AcceptanceException($"want {row.Sizes.Length} bouts, saw {bouts.Count}");
        }

        if (row.Draft && got.Drafted.Length == 0)
        {
            throw new AcceptanceException("no bout was seen gathering to draft from");
        }

        if (row.Draft && got.PeakActive < 1)
        {
            throw new AcceptanceException("no bout formed");
        }

        // The first round is the lowest ids; the pawns re-form after it.
        var sizes = new List<int>();
        var markers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in bouts.Take(row.Sizes.Length))
        {
            var f = b.Fighting ?? throw new AcceptanceException($"bout {b.Id} never fought");
            sizes.Add(f.Members.Count);
            if (row.Draft && f.Members.Contains(got.Drafted))
            {
                throw new AcceptanceException($"drafted {got.Drafted} still fought in bout {b.Id}");
            }

            if (f.Members.Count is < 2 or > 4)
            {
                throw new AcceptanceException($"bout {b.Id} has {f.Members.Count} fighters");
            }

            if (f.Markers.FirstOrDefault(m => !markers.Add(m)) is { } shared)
            {
                throw new AcceptanceException($"marker {shared} used by two bouts");
            }

            CheckTeams(b.Id, f);
        }

        sizes.Sort((a, b) => b.CompareTo(a));
        if (!sizes.SequenceEqual(row.Sizes))
        {
            throw new AcceptanceException($"first-round bout sizes [{string.Join(' ', sizes)}], want [{string.Join(' ', row.Sizes)}]");
        }
    }

    private static void CheckTeams(int id, Fx.SparringFight f)
    {
        var teams = $"[{string.Join(' ', f.Teams)}]";
        var count = f.Teams.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        switch (f.Members.Count)
        {
            case 2 or 3:
                if (count.Count != f.Members.Count)
                {
                    throw new AcceptanceException($"bout {id} of {f.Members.Count} is not free-for-all, teams {teams}");
                }

                break;
            case 4:
                if (count.Count != 2 || count.GetValueOrDefault(0) != 2 || count.GetValueOrDefault(1) != 2)
                {
                    throw new AcceptanceException($"bout {id} of 4 is not 2v2, teams {teams}");
                }

                var hi = 0;
                var lo = 0;
                for (var i = 0; i < f.Melee.Count; i++)
                {
                    hi = f.Melee[i] > f.Melee[hi] ? i : hi;
                    lo = f.Melee[i] < f.Melee[lo] ? i : lo;
                }

                if (f.Teams[hi] != f.Teams[lo])
                {
                    throw new AcceptanceException($"bout {id} of 4: highest and lowest Melee [{string.Join(' ', f.Melee)}] are not teammates, teams {teams}");
                }

                break;
        }

        for (var i = 0; i < f.Opponents.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < f.Members.Count; j++)
            {
                at = f.Members[j] == f.Opponents[i] ? j : at;
            }

            if (at < 0 || f.Teams[at] == f.Teams[i])
            {
                throw new AcceptanceException($"bout {id}: {f.Members[i]}'s opponent \"{f.Opponents[i]}\" is not on another team");
            }
        }
    }
}
