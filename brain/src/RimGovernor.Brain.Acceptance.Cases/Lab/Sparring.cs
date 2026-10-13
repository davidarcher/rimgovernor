using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/sparring</c> (Go <c>sparring.go</c>): bout formation (#2708) and the spar job (#2709): who spars with whom at the
/// ring, and what the fight does to the gear. The bout registry, matchmaking, the work giver and the job driver are native;
/// the fixture's watcher samples the registry every tick (the game runs thousands of ticks between two reads) and records
/// what each spar job left behind the tick it ended, so the case asserts what the watcher saw, not a poll.
/// </summary>
internal static class LabSparring
{
    internal static readonly string[] Ops = ["test/sparring_prepare", "test/sparring_inspect"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/sparring",
        Scope = "Bout formation at the sparring ring (#2708): with no order from Go the vanilla work scan offers eligible colonists a marker, and "
            + "2, 3, 4 and 5 eligible colonists form bouts of 2; 3 (free-for-all); 4 (2v2, highest with lowest); and 3 + 2 at once. Every pawn and every "
            + "marker is in at most one bout, a colonist above the ceiling, a drafted one and one whose chosen skill is Shooting are never seated, a pawn drafted while its bout gathers is "
            + "dropped and the rest fight on, and every fighter's opponent is a member of another team. The bouts then run end to end (#2709): each "
            + "fighter swaps at its marker into the practice weapon and apparel set (its own gear held in its inventory), swings through the vanilla "
            + "melee verb and is paid vanilla melee XP, the struck pawn neither flees nor fights back nor drops its job, and the gear is fully restored "
            + "after a normal end (the exchange cap), a pawn leaving for pain or bleeding while the rest fight on, a pawn drafted mid-bout and a pawn "
            + "killed mid-bout. Every member whose session completed holds the shared trained-with thought (#2710); a pawn drafted or killed mid-bout does not. Each fighter's Joy and Social tolerance rise over the bout, and a fighter that began with a full Joy need still fights to the exchange cap (#2731). A Go snapshot test cannot see the work giver, the registry, the walk, the swap or the vanilla verb.",
        Start = new LabStart { Colonists = 8 },
        RequiredOps = Ops,
        QuietWorld = true,
        Keep = ["Joy"],
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    internal sealed record Stage
    {
        public string Name { get; init; } = "";

        public int Eligible { get; init; }

        public string Levels { get; init; } = "";

        /// <summary>The first round's bout sizes, largest first.</summary>
        public int[] Sizes { get; init; } = [];

        public bool Ineligible { get; init; }

        /// <summary>Adds a trainee whose chosen skill is Shooting (level above its Melee): it must never be seated while the melee-chosen ones are.</summary>
        public bool Shooter { get; init; }

        public bool Draft { get; init; }

        /// <summary>Names a fight stage (#2709): the fixture's mid-bout event, and the stage waits for every spar job to end instead of the bouts to finish.</summary>
        public string Scenario { get; init; } = "";
    }

    /// <summary>A stage's last inspect reply and its failure, if any (Go returned both).</summary>
    internal sealed record StageRun(JsonObject? Last, AcceptanceException? Error);

    internal sealed record Pawn
    {
        public string Id { get; init; } = "";

        public bool Ended { get; init; }

        public string Stop { get; init; } = "";

        public int Exchanges { get; init; }

        public bool Swapped { get; init; }

        public bool SwapSeen { get; init; }

        public bool AtMarker { get; init; }

        public bool Held { get; init; }

        public bool Dead { get; init; }

        public bool Drafted { get; init; }

        public bool Restored { get; init; }

        public bool NoPractice { get; init; }

        public bool Originals { get; init; }

        public bool ThreatNull { get; init; }

        public bool Xp { get; init; }

        public bool Trained { get; init; }

        // #2731: Joy and Social tolerance when the fight began and when the session ended.
        public double JoyStart { get; init; }

        public double JoyEnd { get; init; }

        public double TolStart { get; init; }

        public double TolEnd { get; init; }
    }

    internal sealed record Fight(List<string> Members, List<int> Teams, List<int> Melee, List<string> Opponents, List<string> Markers);

    internal sealed record Bout(int Id, bool Finished, List<string> Formed, Fight? Fighting);

    /// <summary>A number as the invariant text Go's <c>%v</c> printed.</summary>
    internal static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A number to <paramref name="digits"/> decimals, invariant (Go <c>%.nf</c>).</summary>
    internal static string Fixed(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    internal static async Task<JsonObject> PrepareAsync(ISession s, JsonObject args, CancellationToken cancellationToken)
    {
        var (cx, cz) = CaseSupport.Center(s);
        args["x"] = cx;
        args["z"] = cz;
        var prepared = await s.Harness.CallAsync("sparring-prepare", "test/sparring_prepare", args, cancellationToken).ConfigureAwait(false);
        return prepared["success"].AsBool() == true ? prepared : throw new AcceptanceException($"sparring_prepare: {prepared.ToJson()}");
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        Stage[] stages =
        [
            new() { Name = "pair", Eligible = 2, Levels = "5,3", Sizes = [2] },
            new() { Name = "three", Eligible = 3, Levels = "6,4,2", Sizes = [3] },
            new() { Name = "four", Eligible = 4, Levels = "7,5,4,2", Sizes = [4] },
            new() { Name = "five-two-bouts", Eligible = 5, Levels = "7,6,5,4,3", Sizes = [3, 2] },
            new() { Name = "ineligible", Eligible = 2, Levels = "5,4", Sizes = [2], Ineligible = true },
            new() { Name = "shooter-never-seated", Eligible = 3, Levels = "6,4,2", Sizes = [3], Shooter = true },
            new() { Name = "drafted-while-gathering", Eligible = 4, Levels = "7,5,4,2", Sizes = [3], Draft = true },
            new() { Name = "fight-cap", Eligible = 2, Levels = "5,4", Sizes = [2], Scenario = "cap" },
            new() { Name = "fight-pain", Eligible = 3, Levels = "6,5,4", Sizes = [3], Scenario = "pain" },
            new() { Name = "fight-bleed", Eligible = 2, Levels = "5,4", Sizes = [2], Scenario = "bleed" },
            new() { Name = "fight-draft", Eligible = 3, Levels = "6,5,4", Sizes = [3], Scenario = "draft" },
            new() { Name = "fight-kill", Eligible = 3, Levels = "6,5,4", Sizes = [3], Scenario = "kill" },
        ];

        // A freshly loaded world has no bouts: the registry is runtime only.
        var fresh = await h.CallAsync("sparring-inspect-fresh", "test/sparring_inspect", [], cancellationToken).ConfigureAwait(false);
        if (fresh["active"].AsNumber() != 0)
        {
            throw new AcceptanceException($"registry not empty on a fresh world: {fresh.ToJson()}");
        }

        var report = new JsonObject();
        try
        {
            foreach (var stage in stages)
            {
                var run = await RunStageAsync(s, stage, cancellationToken).ConfigureAwait(false);
                report[stage.Name] = run.Last?.DeepClone();
                if (run.Error is not null)
                {
                    throw new AcceptanceException($"{stage.Name}: {run.Error.Message}", run.Error);
                }
            }
        }
        finally
        {
            s.Report["sparring"] = report;
        }
    }

    internal static async Task<StageRun> RunStageAsync(ISession s, Stage stage, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        JsonObject? last = null;
        try
        {
            var prepared = await PrepareAsync(
                s,
                new JsonObject
                {
                    ["eligible"] = stage.Eligible,
                    ["levels"] = stage.Levels,
                    ["ineligible"] = stage.Ineligible,
                    ["shooter"] = stage.Shooter,
                    ["draftOnGather"] = stage.Draft,
                    ["scenario"] = stage.Scenario,
                },
                cancellationToken).ConfigureAwait(false);
            _ = await RunAt.RunUntilAsync(
                h,
                "sparring-" + stage.Name,
                20000,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var got = await h.CallAsync("sparring-inspect", "test/sparring_inspect", [], ct).ConfigureAwait(false);
                    last = got;
                    if (stage.Scenario.Length > 0)
                    {
                        var ended = Pawns(got).Count(p => p.Ended);
                        var finished = ended >= stage.Eligible;
                        if (stage.Scenario == "raid")
                        {
                            // The attacker is removed a few hundred ticks after the victim's job ends.
                            var raid = got["raid"].AsMap();
                            finished = finished && raid?["raiderGone"].AsBool() == true;
                        }

                        return (Wait.Signature(got["active"], ended), finished);
                    }

                    var done = Bouts(got).Count(b => b.Fighting is not null && b.Finished);
                    return (Wait.Signature(got["active"], done), done >= stage.Sizes.Length);
                },
                cancellationToken).ConfigureAwait(false);
            CheckStage(stage, prepared, last ?? []);
            if (stage.Scenario == "raid")
            {
                CheckRaid(last ?? []);
            }
            else if (stage.Scenario.Length > 0)
            {
                CheckFight(stage, last ?? []);
            }

            return new StageRun(last, null);
        }
        catch (AcceptanceException e)
        {
            return new StageRun(last, e);
        }
    }

    // A real attacker beside a sparring pawn (#2711). The victim's spar job ends (the stop rule, or it is downed) and its
    // own gear is back; the numbers it saw go to the report.
    private static void CheckRaid(JsonObject got)
    {
        var raid = got["raid"].AsMap() ?? throw new AcceptanceException($"no attacker was ever spawned: {got.ToJson()}");
        var victim = raid["victim"].AsString();
        foreach (var p in Pawns(got))
        {
            if (!p.Ended)
            {
                throw new AcceptanceException($"{p.Id}'s spar job never ended: {got.ToJson()}");
            }

            if (!p.NoPractice || !p.Originals)
            {
                throw new AcceptanceException($"{p.Id} left practice gear behind or lost a piece: {got.ToJson()}");
            }

            if (!p.Dead && !p.Restored)
            {
                throw new AcceptanceException($"{p.Id}'s gear is not restored: {got.ToJson()}");
            }

            if (p.Id == victim && p.Dead)
            {
                throw new AcceptanceException($"the attacker killed {p.Id}: {got.ToJson()}");
            }
        }
    }

    internal static List<Pawn> Pawns(JsonObject got)
    {
        var pawns = new List<Pawn>();
        foreach (var raw in got["pawns"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            pawns.Add(new Pawn
            {
                Id = row["id"].AsString(),
                Ended = LabCases.Flag(row, "ended"),
                Stop = row["stop"].AsString(),
                Exchanges = (int)row["exchanges"].AsNumber(),
                Swapped = LabCases.Flag(row, "swapped"),
                SwapSeen = LabCases.Flag(row, "swapSeen"),
                AtMarker = LabCases.Flag(row, "swapAtMarker"),
                Held = LabCases.Flag(row, "originalsHeld"),
                Dead = LabCases.Flag(row, "dead"),
                Drafted = LabCases.Flag(row, "drafted"),
                Restored = LabCases.Flag(row, "restored"),
                NoPractice = LabCases.Flag(row, "noPractice"),
                Originals = LabCases.Flag(row, "originals"),
                ThreatNull = LabCases.Flag(row, "threatNull"),
                Xp = LabCases.Flag(row, "xp"),
                Trained = LabCases.Flag(row, "trainedWith"),
                JoyStart = row["joyStart"].AsNumber(),
                JoyEnd = row["joyEnd"].AsNumber(),
                TolStart = row["toleranceStart"].AsNumber(),
                TolEnd = row["toleranceEnd"].AsNumber(),
            });
        }

        return pawns;
    }

    // What the spar job did in a fight stage: the swap at the marker, the gear after the job ended, vanilla XP, and the
    // scenario's own claim. A pawn that never spars (the stage's eligible set is all of them) fails.
    private static void CheckFight(Stage stage, JsonObject got)
    {
        var pawns = Pawns(got);
        if (pawns.Count != stage.Eligible)
        {
            throw new AcceptanceException($"{pawns.Count} pawns tracked, want {stage.Eligible}: {got.ToJson()}");
        }

        var abandoned = got["abandoned"].AsNumber();
        if (abandoned != 0)
        {
            throw new AcceptanceException($"a fighter dropped its spar job {Num(abandoned)} times (the struck pawn must not flee or abandon): {got.ToJson()}");
        }

        var attackJobs = got["attackJobs"].AsNumber();
        if (attackJobs != 0)
        {
            throw new AcceptanceException($"a fighter took a real AttackMelee job {Num(attackJobs)} times (meleeThreat reaction): {got.ToJson()}");
        }

        var injured = got["injected"].AsString();
        var hit = got["hit"].AsString();
        foreach (var p in pawns)
        {
            var killed = stage.Scenario == "kill" && p.Id == hit;
            if (p.Dead != killed)
            {
                throw new AcceptanceException($"{p.Id} dead={p.Dead}, want {killed}: {got.ToJson()}");
            }

            if (!p.Swapped || !p.SwapSeen)
            {
                throw new AcceptanceException($"{p.Id} never swapped into practice gear: {got.ToJson()}");
            }

            if (!p.AtMarker)
            {
                throw new AcceptanceException($"{p.Id} did not swap at its marker: {got.ToJson()}");
            }

            if (!p.Held)
            {
                throw new AcceptanceException($"{p.Id}'s own weapon and apparel were not held in its inventory while it wore practice gear: {got.ToJson()}");
            }

            if (!p.NoPractice)
            {
                throw new AcceptanceException($"{p.Id} left practice gear behind: {got.ToJson()}");
            }

            if (!p.Originals)
            {
                throw new AcceptanceException($"{p.Id} lost an original weapon or apparel piece: {got.ToJson()}");
            }

            if (!killed && !p.Restored)
            {
                throw new AcceptanceException($"{p.Id}'s gear is not restored: {got.ToJson()}");
            }

            if (!p.ThreatNull)
            {
                throw new AcceptanceException($"{p.Id} still had a meleeThreat when its job ended: {got.ToJson()}");
            }

            // #2731: a fighter gains Social joy over the bout (tolerance rises with it); one that started with a full Joy
            // need keeps fighting to the normal end.
            if (p.JoyStart >= 0.99 && (p.Exchanges == 0 || !p.Swapped))
            {
                throw new AcceptanceException($"{p.Id} began with full Joy and did not fight (exchanges {p.Exchanges}): {got.ToJson()}");
            }

            if (!p.Dead && p.Exchanges > 0 && p.JoyStart >= 0 && p.JoyStart < 0.99 && p.JoyEnd <= p.JoyStart)
            {
                throw new AcceptanceException($"{p.Id} Joy did not rise over the bout ({Fixed(p.JoyStart, 3)} to {Fixed(p.JoyEnd, 3)}): {got.ToJson()}");
            }

            if (!p.Dead && p.Exchanges > 0 && p.JoyStart >= 0 && p.JoyStart < 0.99 && p.TolEnd <= p.TolStart)
            {
                throw new AcceptanceException($"{p.Id} Social tolerance did not rise ({Fixed(p.TolStart, 4)} to {Fixed(p.TolEnd, 4)}): {got.ToJson()}");
            }

            if (p.Exchanges > 10)
            {
                throw new AcceptanceException($"{p.Id} made {p.Exchanges} exchanges, past the cap: {got.ToJson()}");
            }

            if (p.Exchanges > 0 && !p.Xp)
            {
                throw new AcceptanceException($"{p.Id} swung {p.Exchanges} times and was paid no melee XP: {got.ToJson()}");
            }

            // #2710: a bout member whose session was completed (cap, stop rule, bout end) holds the trained-with thought;
            // one drafted or killed mid-bout does not.
            if (p.Trained != (!killed && !(stage.Scenario == "draft" && p.Id == hit)))
            {
                throw new AcceptanceException($"{p.Id} trained-with thought is {(p.Trained ? "true" : "false")} after a \"{stage.Scenario}\" stage: {got.ToJson()}");
            }
        }

        var by = pawns.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
        switch (stage.Scenario)
        {
            case "cap":
                var capped = 0;
                foreach (var p in pawns)
                {
                    if (p.Stop is "Pain" or "Bleeding")
                    {
                        throw new AcceptanceException($"{p.Id} stopped for {p.Stop} with no pain possible: {got.ToJson()}");
                    }

                    if (p.Stop == "Exchanges" && p.Exchanges == 10)
                    {
                        capped++;
                    }
                }

                if (capped == 0)
                {
                    throw new AcceptanceException($"no pawn ended at the 10-exchange cap: {got.ToJson()}");
                }

                if (got["struck"].AsNumber() == 0)
                {
                    throw new AcceptanceException($"no blow landed, so the struck pawn was never tested: {got.ToJson()}");
                }

                break;
            case "pain" or "bleed":
                var want = stage.Scenario == "pain" ? "Pain" : "Bleeding";
                if (!by.TryGetValue(injured, out var first) || first.Stop != want)
                {
                    throw new AcceptanceException($"injured \"{injured}\" did not leave for {want}: {got.ToJson()}");
                }

                var swings = pawns.Where(p => p.Id != injured).Sum(p => p.Exchanges);
                if (stage.Eligible > 2 && swings == 0)
                {
                    throw new AcceptanceException($"the rest of the bout did not fight on after {injured} left: {got.ToJson()}");
                }

                break;
            case "draft" or "kill":
                if (!by.TryGetValue(hit, out var victim) || victim.Exchanges < 2)
                {
                    throw new AcceptanceException($"\"{hit}\" was not hit mid-bout after swinging: {got.ToJson()}");
                }

                if (stage.Scenario == "draft" && !victim.Drafted)
                {
                    throw new AcceptanceException($"{hit} was not drafted: {got.ToJson()}");
                }

                var others = pawns.Where(p => p.Id != hit).Sum(p => p.Exchanges);
                if (others <= victim.Exchanges)
                {
                    throw new AcceptanceException($"the rest of the bout did not fight on after {hit} left: {got.ToJson()}");
                }

                break;
        }
    }

    internal static List<Bout> Bouts(JsonObject got)
    {
        var bouts = new List<Bout>();
        foreach (var raw in got["bouts"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            Fight? fighting = null;
            if (row["fighting"].AsMap() is { } f)
            {
                fighting = new Fight(
                    LabCases.StringList(f["members"]),
                    LabCases.IntList(f["teams"]),
                    LabCases.IntList(f["melee"]),
                    LabCases.StringList(f["opponents"]),
                    LabCases.StringList(f["markers"]));
            }

            bouts.Add(new Bout((int)row["id"].AsNumber(), LabCases.Flag(row, "finished"), LabCases.StringList(row["formed"]), fighting));
        }

        bouts.Sort((a, b) => a.Id.CompareTo(b.Id));
        return bouts;
    }

    private static void CheckStage(Stage stage, JsonObject prepared, JsonObject got)
    {
        var doubleBooked = got["doubleBooked"].AsNumber();
        if (doubleBooked != 0)
        {
            throw new AcceptanceException($"a pawn or marker was in two bouts at once {Num(doubleBooked)} times: {got.ToJson()}");
        }

        var eligible = LabCases.StringList(prepared["eligible"]);
        var seated = LabCases.StringList(got["seated"]);
        if (stage.Shooter)
        {
            var shooters = LabCases.StringList(prepared["shooter"]);
            if (shooters.Count != 1)
            {
                throw new AcceptanceException($"stage staged {shooters.Count} shooters, want 1: {prepared.ToJson()}");
            }

            foreach (var id in seated)
            {
                if (shooters.Contains(id, StringComparer.Ordinal))
                {
                    throw new AcceptanceException($"shooting-chosen {id} was seated in a bout: {got.ToJson()}");
                }
            }

            if (seated.Count < stage.Eligible)
            {
                throw new AcceptanceException($"the melee-chosen pawns were not all seated: {got.ToJson()}");
            }
        }

        foreach (var id in seated)
        {
            if (!eligible.Contains(id, StringComparer.Ordinal))
            {
                throw new AcceptanceException($"{id} was seated but is not eligible: {got.ToJson()}");
            }
        }

        var bouts = Bouts(got);
        if (bouts.Count < stage.Sizes.Length)
        {
            throw new AcceptanceException($"want {stage.Sizes.Length} bouts, saw {bouts.Count}: {got.ToJson()}");
        }

        // The first round is the lowest ids; the pawns re-form after it.
        var round = bouts.Take(stage.Sizes.Length).ToList();
        var sizes = new List<int>();
        var drafted = got["drafted"].AsString();
        if (stage.Draft && drafted.Length == 0)
        {
            throw new AcceptanceException($"no bout was seen gathering to draft from: {got.ToJson()}");
        }

        if (stage.Draft && got["peakActive"].AsNumber() < 1)
        {
            throw new AcceptanceException($"no bout formed: {got.ToJson()}");
        }

        var markers = new List<string>();
        foreach (var b in round)
        {
            if (b.Fighting is not { } f)
            {
                throw new AcceptanceException($"bout {b.Id} never fought: {got.ToJson()}");
            }

            sizes.Add(f.Members.Count);
            if (stage.Draft && f.Members.Contains(drafted, StringComparer.Ordinal))
            {
                throw new AcceptanceException($"drafted {drafted} still fought in bout {b.Id}: {got.ToJson()}");
            }

            if (f.Members.Count < 2 || f.Members.Count > 4)
            {
                throw new AcceptanceException($"bout {b.Id} has {f.Members.Count} fighters: {got.ToJson()}");
            }

            foreach (var m in f.Markers)
            {
                if (markers.Contains(m, StringComparer.Ordinal))
                {
                    throw new AcceptanceException($"marker {m} used by two bouts: {got.ToJson()}");
                }

                markers.Add(m);
            }

            try
            {
                CheckTeams(b, f);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{e.Message}: {got.ToJson()}", e);
            }
        }

        sizes.Sort((a, b) => b.CompareTo(a));
        if (!sizes.SequenceEqual(stage.Sizes))
        {
            throw new AcceptanceException($"first-round bout sizes [{string.Join(' ', sizes)}], want [{string.Join(' ', stage.Sizes)}]: {got.ToJson()}");
        }
    }

    private static void CheckTeams(Bout b, Fight f)
    {
        var count = new Dictionary<int, int>();
        foreach (var t in f.Teams)
        {
            count[t] = count.GetValueOrDefault(t) + 1;
        }

        var teams = $"[{string.Join(' ', f.Teams)}]";
        switch (f.Members.Count)
        {
            case 2 or 3:
                if (count.Count != f.Members.Count)
                {
                    throw new AcceptanceException($"bout {b.Id} of {f.Members.Count} is not free-for-all, teams {teams}");
                }

                break;
            case 4:
                if (count.Count != 2 || count.GetValueOrDefault(0) != 2 || count.GetValueOrDefault(1) != 2)
                {
                    throw new AcceptanceException($"bout {b.Id} of 4 is not 2v2, teams {teams}");
                }

                int hi = 0;
                int lo = 0;
                for (var i = 0; i < f.Melee.Count; i++)
                {
                    if (f.Melee[i] > f.Melee[hi])
                    {
                        hi = i;
                    }

                    if (f.Melee[i] < f.Melee[lo])
                    {
                        lo = i;
                    }
                }

                if (f.Teams[hi] != f.Teams[lo])
                {
                    throw new AcceptanceException($"bout {b.Id} of 4: highest and lowest Melee [{string.Join(' ', f.Melee)}] are not teammates, teams {teams}");
                }

                break;
        }

        for (var i = 0; i < f.Opponents.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < f.Members.Count; j++)
            {
                if (f.Members[j] == f.Opponents[i])
                {
                    at = j;
                }
            }

            if (at < 0 || f.Teams[at] == f.Teams[i])
            {
                throw new AcceptanceException($"bout {b.Id}: {f.Members[i]}'s opponent \"{f.Opponents[i]}\" is not on another team");
            }
        }
    }
}
