using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The sparring numbers (#2711; Go <c>sparring_measure.go</c>): the stop rule's limits, the practice weapons' powers and the
/// practice apparel's armor are tuned from what these cases measure over many bouts, not guessed. The fixture runs in
/// measure mode: it keeps the bouts going, tallies what every ended spar session left (injuries, destroyed parts, scars,
/// gear wear, nakedness, carried mass, vanilla XP) and makes the pawn unhurt again the same tick, so each bout is an
/// independent sample. The report carries every number; the assertions are the epic's stakes (no deaths, no destroyed
/// vital or protected part, nobody naked, real armor untouched).
/// </summary>
internal static class LabSparringMeasure
{
    // How many ended sessions a risk stage tallies. A death or a destroyed protected part must not appear in this many;
    // the report gives the observed rates, which are the tuning data.
    private const int Sessions = 100;

    private const string SuffixScope = " A Go snapshot test cannot see the verb's hits, the heal, the vanilla XP or the formation over a running ring.";

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return Register(
            "lab/sparring-risk",
            "The sparring risk measured over many bouts (#2711), per practice tier and outfit (a tribal garment and spacer recon armor): zero deaths, "
            + "no destroyed head, neck or torso, no lost eye, ear, nose or finger, bruises at tier 0 and cuts at the sharp tiers, scars counted by a simulated heal, practice gear wear, "
            + "real armor never worn, nobody naked after the swap, carried mass under capacity, and a Royalty title requirement that does not pull a fighter out of its job.",
            8,
            RunRiskAsync);
        yield return Register(
            "lab/sparring-xp",
            "Vanilla melee XP per swing and the day's saturation (xpSinceMidnight, the 0.2 factor) over a day of bouts (#2711).",
            3,
            RunXpAsync);
        yield return Register(
            "lab/sparring-scale",
            "Bout formation for 2 to 12 eligible colonists (every one seated, no double booking, opponents rotating), the partner shortage at levels 16 to 20, "
            + "and a hostile attacker mid-bout (#2711).",
            12,
            RunScaleAsync);
    }

    private static AcceptanceCase Register(string name, string scope, int colonists, Func<ISession, CancellationToken, Task> run) => new()
    {
        Name = name,
        Scope = scope + SuffixScope,
        Start = new LabStart { Colonists = colonists },
        RequiredOps = LabSparring.Ops,
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Run = run,
    };

    private sealed record MeasureStage
    {
        public string Name { get; init; } = "";

        public int Tier { get; init; }

        public string Outfit { get; init; } = "";

        public string Levels { get; init; } = "";

        public int Eligible { get; init; }

        public int Sessions { get; init; }

        public bool Title { get; init; }

        public bool XpDay { get; init; }
    }

    private static async Task RunRiskAsync(ISession s, CancellationToken cancellationToken)
    {
        string[] tierLevels = ["7,6,5,4,3,2,5,4", "11,9,7,5,4,3,8,6", "15,12,9,7,5,4,10,8", "19,17,14,10,7,5,12,9"];
        var stages = new List<MeasureStage>();

        // Tiers only unlock, so they run in ascending order.
        for (var tier = 0; tier < 4; tier++)
        {
            foreach (var outfit in new[] { "tribal", "spacer" })
            {
                stages.Add(new MeasureStage
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"risk-t{tier}-{outfit}"),
                    Tier = tier,
                    Outfit = outfit,
                    Levels = tierLevels[tier],
                    Eligible = 8,
                    Sessions = Sessions,
                    Title = tier == 1 && outfit == "spacer",
                });
            }
        }

        var report = new JsonObject();
        try
        {
            // Every stage runs even after a failed check: the numbers of all of them are the tuning data.
            AcceptanceException? first = null;
            foreach (var st in stages)
            {
                var run = await RunStageAsync(s, st, cancellationToken).ConfigureAwait(false);
                report[st.Name] = Summarize(run.Last);
                if (run.Error is not null)
                {
                    throw new AcceptanceException($"{st.Name}: {run.Error.Message}", run.Error);
                }

                try
                {
                    CheckRisk(st, run.Last ?? []);
                }
                catch (AcceptanceException e) when (first is null)
                {
                    first = new AcceptanceException($"{st.Name}: {e.Message}", e);
                }
                catch (AcceptanceException later)
                {
                    // Only the first failed check is reported; later ones are logged.
                    Tolerated.Note("later sparring check", later);
                }
            }

            if (first is not null)
            {
                throw first;
            }
        }
        finally
        {
            s.Report["sparring-risk"] = report;
        }
    }

    // Measures vanilla melee XP per swing and the day's saturation.
    private static async Task RunXpAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = new JsonObject();
        try
        {
            var st = new MeasureStage { Name = "xp-day", Tier = 3, Outfit = "tribal", Levels = "10,10", Eligible = 2, Sessions = 12, XpDay = true };
            var run = await RunStageAsync(s, st, cancellationToken).ConfigureAwait(false);
            report[st.Name] = Summarize(run.Last);
            report[st.Name + "-xp"] = XpDaySummary(run.Last ?? []);
            if (run.Error is not null)
            {
                throw run.Error;
            }
        }
        finally
        {
            s.Report["sparring-xp"] = report;
        }
    }

    private static async Task RunScaleAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = new JsonObject();
        try
        {
            // Formation at scale and the partner shortage, at the top tier (ceiling 20).
            var scale = new List<MeasureStage>();
            foreach (var n in new[] { 2, 3, 4, 5, 6, 8, 10, 12 })
            {
                scale.Add(new MeasureStage
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"scale-{n}"),
                    Tier = 3,
                    Levels = "9,7,5,4,3,2,12,10,8,6,11,13",
                    Eligible = n,
                    Sessions = 2 * n,
                });
            }

            scale.Add(new MeasureStage { Name = "shortage-4-high-2-low", Tier = 3, Levels = "19,18,17,16,3,2", Eligible = 6, Sessions = 12 });
            scale.Add(new MeasureStage { Name = "shortage-1-high-2-low", Tier = 3, Levels = "19,3,2", Eligible = 3, Sessions = 6 });
            scale.Add(new MeasureStage { Name = "shortage-pair", Tier = 3, Levels = "19,3", Eligible = 2, Sessions = 4 });
            scale.Add(new MeasureStage { Name = "shortage-all-high", Tier = 3, Levels = "19,18,17,16", Eligible = 4, Sessions = 8 });
            var failures = new List<string>();
            foreach (var st in scale)
            {
                var run = await RunStageAsync(s, st, cancellationToken).ConfigureAwait(false);
                report[st.Name] = SummarizeFormation(run.Last ?? []);
                if (run.Error is not null)
                {
                    throw new AcceptanceException($"{st.Name}: {run.Error.Message}", run.Error);
                }

                try
                {
                    CheckFormation(st, run.Last ?? []);
                }
                catch (AcceptanceException e)
                {
                    failures.Add($"{st.Name}: {e.Message}");
                }
            }

            // A real attacker mid-bout: the spar job's override mode is Never, so what the struck pawn does is measured,
            // not assumed.
            var raid = new LabSparring.Stage { Name = "fight-raid", Eligible = 3, Levels = "6,5,4", Sizes = [3], Scenario = "raid" };
            var raided = await LabSparring.RunStageAsync(s, raid, cancellationToken).ConfigureAwait(false);
            var got = raided.Last;
            report[raid.Name] = new JsonObject
            {
                ["raid"] = got?["raid"]?.DeepClone(),
                ["abandoned"] = got?["abandoned"]?.DeepClone(),
                ["pawns"] = got?["pawns"]?.DeepClone(),
            };
            if (raided.Error is not null)
            {
                failures.Add($"{raid.Name}: {raided.Error.Message}");
            }

            if (failures.Count > 0)
            {
                throw new AcceptanceException(string.Join('\n', failures));
            }
        }
        finally
        {
            s.Report["sparring-scale"] = report;
        }
    }

    private static async Task<LabSparring.StageRun> RunStageAsync(ISession s, MeasureStage st, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        JsonObject? last = null;
        try
        {
            _ = await LabSparring.PrepareAsync(
                s,
                new JsonObject
                {
                    ["eligible"] = st.Eligible,
                    ["levels"] = st.Levels,
                    ["markers"] = Math.Max(st.Eligible, 6),
                    ["tier"] = st.Tier,
                    ["outfit"] = st.Outfit,
                    ["measure"] = true,
                    ["xpDay"] = st.XpDay,
                    ["title"] = st.Title,
                },
                cancellationToken).ConfigureAwait(false);
            _ = await RunAt.RunUntilAsync(
                h,
                "sparring-" + st.Name,
                1200000,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var got = await h.CallAsync("sparring-inspect", "test/sparring_inspect", [], ct).ConfigureAwait(false);
                    last = got;
                    var n = TallyNumber(got, "sessions");
                    return (Wait.Signature(n, got["active"]), (int)n >= st.Sessions);
                },
                cancellationToken).ConfigureAwait(false);
            return new LabSparring.StageRun(last, null);
        }
        catch (AcceptanceException e)
        {
            return new LabSparring.StageRun(last, e);
        }
    }

    private static double TallyNumber(JsonObject got, string key)
    {
        var n = got["tally"].AsMap()?["n"].AsMap();
        return Math.Max(0, n?[key].AsNumber() ?? -1);
    }

    private static SortedDictionary<string, int> TallyHist(JsonObject got, string name)
    {
        var row = got["tally"].AsMap()?["hist"].AsMap()?[name].AsMap();
        var hist = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in row ?? [])
        {
            hist[key] = (int)value.AsNumber();
        }

        return hist;
    }

    private static JsonObject Summarize(JsonObject? got)
    {
        got ??= [];
        var tally = got["tally"].AsMap();
        return new JsonObject
        {
            ["n"] = tally?["n"]?.DeepClone(),
            ["hist"] = tally?["hist"]?.DeepClone(),
            ["bouts"] = LabSparring.Bouts(got).Count,
            ["abandoned"] = got["abandoned"]?.DeepClone(),
            ["attackJobs"] = got["attackJobs"]?.DeepClone(),
        };
    }

    // Asserts the stakes and leaves the rates to the report.
    private static void CheckRisk(MeasureStage st, JsonObject got)
    {
        double N(string key) => TallyNumber(got, key);
        string Tally() => got["tally"].ToJson();
        if (N("sessions") < st.Sessions)
        {
            throw new AcceptanceException($"{LabSparring.Num(N("sessions"))} sessions, want {st.Sessions}: {got.ToJson()}");
        }

        if (N("deaths") != 0)
        {
            throw new AcceptanceException($"{LabSparring.Num(N("deaths"))} deaths in {LabSparring.Num(N("sessions"))} sessions: {Tally()}");
        }

        var protectedParts = new HashSet<string>(["Head", "Neck", "Torso", "Skull", "Brain"], StringComparer.Ordinal);
        if (st.Tier >= 1)
        {
            foreach (var part in new[] { "Eye", "Ear", "Nose", "Jaw", "Finger", "Hand" })
            {
                protectedParts.Add(part);
            }
        }

        foreach (var (part, count) in TallyHist(got, "destroyedPart"))
        {
            if (protectedParts.Contains(part))
            {
                throw new AcceptanceException($"{count} {part} destroyed in {LabSparring.Num(N("sessions"))} sessions: {Tally()}");
            }
        }

        var injuries = TallyHist(got, "injury");
        var cuts = injuries.GetValueOrDefault("Cut");
        var bruises = injuries.GetValueOrDefault("Bruise");
        if (st.Tier == 0 && cuts > 0)
        {
            throw new AcceptanceException($"the blunt club cut {cuts} times: {Tally()}");
        }

        if (st.Tier == 0 && bruises == 0)
        {
            throw new AcceptanceException($"no bruise in {LabSparring.Num(N("sessions"))} sessions at tier 0: {Tally()}");
        }

        if (st.Tier >= 1 && cuts == 0)
        {
            throw new AcceptanceException($"no cut in {LabSparring.Num(N("sessions"))} sessions at tier {st.Tier}: {Tally()}");
        }

        if (N("nudeTicks") != 0)
        {
            throw new AcceptanceException($"a pawn was naked for {LabSparring.Num(N("nudeTicks"))} ticks wearing the practice set: {Tally()}");
        }

        if (N("realArmorLost") != 0)
        {
            throw new AcceptanceException($"real armor lost {LabSparring.Num(N("realArmorLost"))} (worn {LabSparring.Num(N("realArmorWorn"))}): {Tally()}");
        }

        if (N("maxMassFraction") >= 1)
        {
            throw new AcceptanceException($"carried mass reached {LabSparring.Fixed(N("maxMassFraction"), 2)} of capacity: {Tally()}");
        }

        if (N("swapped") == 0)
        {
            throw new AcceptanceException($"no session swapped into practice gear: {Tally()}");
        }

        var abandoned = got["abandoned"].AsNumber();
        if (abandoned != 0)
        {
            throw new AcceptanceException($"a fighter dropped its spar job {LabSparring.Num(abandoned)} times: {got.ToJson()}");
        }
    }

    // Reads the trace (pawn index | exchanges | xp | xpSinceMidnight after) of one pawn: the XP a swing paid before and
    // after the day's saturation.
    private static JsonObject XpDaySummary(JsonObject got)
    {
        var trace = got["tally"].AsMap()?["trace"];
        var by = new SortedDictionary<int, List<(int Exchanges, double Xp, double Mid)>>();
        foreach (var raw in trace.AsSlice())
        {
            var parts = raw.AsString().Split('|');
            if (parts.Length == 4
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exchanges)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var xp)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var mid))
            {
                if (!by.TryGetValue(index, out var rows))
                {
                    rows = [];
                    by[index] = rows;
                }

                rows.Add((exchanges, xp, mid));
            }
        }

        var summary = new JsonObject();
        foreach (var (index, rows) in by)
        {
            var swings = 0;
            double firstXp = 0;
            var sessions = new JsonArray();
            for (var i = 0; i < rows.Count; i++)
            {
                if (i == 0 && rows[i].Exchanges > 0)
                {
                    firstXp = rows[i].Xp / rows[i].Exchanges;
                }

                swings += rows[i].Exchanges;
                sessions.Add(new JsonObject { ["exchanges"] = rows[i].Exchanges, ["xp"] = rows[i].Xp, ["xpSinceMidnight"] = rows[i].Mid });
            }

            summary[string.Create(CultureInfo.InvariantCulture, $"pawn{index}")] = new JsonObject
            {
                ["sessions"] = sessions,
                ["swings"] = swings,
                ["firstSessionXpPerSwing"] = firstXp,
            };
        }

        return summary;
    }

    private static JsonObject SummarizeFormation(JsonObject got)
    {
        var bouts = new JsonArray();
        foreach (var b in LabSparring.Bouts(got))
        {
            if (b.Fighting is not { } f)
            {
                continue;
            }

            var lo = f.Melee[0];
            var hi = f.Melee[0];
            foreach (var m in f.Melee)
            {
                lo = Math.Min(lo, m);
                hi = Math.Max(hi, m);
            }

            bouts.Add(new JsonObject
            {
                ["id"] = b.Id,
                ["members"] = new JsonArray([.. f.Members.Select(m => (JsonNode?)m)]),
                ["melee"] = new JsonArray([.. f.Melee.Select(m => (JsonNode?)m)]),
                ["teams"] = new JsonArray([.. f.Teams.Select(t => (JsonNode?)t)]),
                ["gap"] = hi - lo,
            });
        }

        return new JsonObject
        {
            ["bouts"] = bouts,
            ["seated"] = got["seated"]?.DeepClone(),
            ["doubleBooked"] = got["doubleBooked"]?.DeepClone(),
            ["peakActive"] = got["peakActive"]?.DeepClone(),
            ["sessions"] = TallyNumber(got, "sessions"),
        };
    }

    // Every eligible pawn is seated in some bout of 2 to 4, none twice at once, and from six pawns up the second round is
    // a different grouping.
    private static void CheckFormation(MeasureStage st, JsonObject got)
    {
        var doubleBooked = got["doubleBooked"].AsNumber();
        if (doubleBooked != 0)
        {
            throw new AcceptanceException($"a pawn or marker was in two bouts {LabSparring.Num(doubleBooked)} times: {got.ToJson()}");
        }

        var seated = LabCases.StringList(got["seated"]).Count;
        if (seated < st.Eligible)
        {
            throw new AcceptanceException($"{seated} of {st.Eligible} eligible pawns were ever seated: {got.ToJson()}");
        }

        var rounds = new List<List<List<string>>>();
        var round = new List<List<string>>();
        var size = 0;
        foreach (var b in LabSparring.Bouts(got))
        {
            if (b.Fighting is not { } f)
            {
                continue;
            }

            if (f.Members.Count < 2 || f.Members.Count > 4)
            {
                throw new AcceptanceException($"bout {b.Id} has {f.Members.Count} fighters");
            }

            round.Add([.. f.Members]);
            size += f.Members.Count;
            if (size >= st.Eligible)
            {
                rounds.Add(round);
                round = [];
                size = 0;
            }
        }

        if (rounds.Count == 0)
        {
            throw new AcceptanceException($"no complete first round: {got.ToJson()}");
        }

        // Rotation: some bout of the second round is a grouping the first did not have. The shortage stages are exempt:
        // with one high partner left the same trio is right.
        if (st.Eligible >= 6 && rounds.Count >= 2 && !st.Name.StartsWith("shortage", StringComparison.Ordinal))
        {
            var fresh = false;
            foreach (var b in rounds[1])
            {
                var repeat = false;
                foreach (var a in rounds[0])
                {
                    repeat = repeat || SameSet(a, b);
                }

                fresh = fresh || !repeat;
            }

            if (!fresh)
            {
                throw new AcceptanceException($"the second round only repeated first-round bouts: {got.ToJson()}");
            }
        }
    }

    private static bool SameSet(List<string> a, List<string> b) =>
        a.Count == b.Count && a.All(x => b.Contains(x, StringComparer.Ordinal));
}
