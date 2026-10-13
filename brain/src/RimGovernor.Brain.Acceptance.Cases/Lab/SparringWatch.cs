using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/sparring-watch</c> (Go <c>sparring_watch.go</c>): watching a sparring bout (#2732), the watch-sparring joy giver
/// and its driver. The fixture's recorder reads every watch job the tick it runs, so the case asserts what it saw: where
/// the spectator stood, that joy rose, that the watch ended with the bout and earned the thought.
/// </summary>
internal static class LabSparringWatch
{
    private static readonly string[] Ops = ["test/sparring_prepare", "test/sparring_inspect", "test/sparring_watch_prepare", "test/sparring_watch_inspect"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/sparring-watch",
        Scope = "The watch-sparring joy giver (#2732): with a fighting bout and a free colonist at joy time, vanilla's joy pick sends the bystander to a "
            + "spectator cell outside the ring, 3 to 8 cells from a fighter, in sight of the bout; Joy rises while it watches; the watch ends when the bout "
            + "ends and a watch of a few hundred ticks leaves the watched-a-sparring-match thought. A drafted colonist and one on an all-work schedule "
            + "never watch, no fighter is ever a spectator, and with no bout fighting nobody watches. A Go snapshot test cannot see the joy pick, the walk, the Joy need or the memory.",
        Start = new LabStart { Colonists = 8 },
        RequiredOps = Ops,
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(10),
        Keep = ["Joy"],
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed record Watcher
    {
        public string Id { get; init; } = "";

        public int Started { get; init; }

        public int Ended { get; init; }

        public double JoyStart { get; init; }

        public double JoyMax { get; init; }

        public bool CellOk { get; init; }

        public string CellNote { get; init; } = "";

        public double Dist { get; init; }

        public bool Fighter { get; init; }

        public int AtCell { get; init; }

        public bool FightingEnd { get; init; }

        public bool Thought { get; init; }
    }

    private static List<Watcher> Watchers(JsonObject got)
    {
        var watchers = new List<Watcher>();
        foreach (var raw in got["watchers"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            watchers.Add(new Watcher
            {
                Id = row["id"].AsString(),
                Started = (int)row["started"].AsNumber(),
                Ended = (int)row["ended"].AsNumber(),
                JoyStart = row["joyStart"].AsNumber(),
                JoyMax = row["joyMax"].AsNumber(),
                CellOk = LabCases.Flag(row, "cellOk"),
                CellNote = row["cellNote"].AsString(),
                Dist = row["dist"].AsNumber(),
                Fighter = LabCases.Flag(row, "fighter"),
                AtCell = (int)row["atCellTicks"].AsNumber(),
                FightingEnd = LabCases.Flag(row, "fightingAtEnd"),
                Thought = LabCases.Flag(row, "thought"),
            });
        }

        return watchers;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = new JsonObject();
        try
        {
            // Stage 1: a fighting bout, a free bystander, a drafted one and an all-work one.
            var prepared = await LabSparring.PrepareAsync(
                s,
                new JsonObject { ["eligible"] = 2, ["levels"] = "5,4", ["scenario"] = "cap" },
                cancellationToken).ConfigureAwait(false);
            var (cx, cz) = CaseSupport.Center(s);
            var watch = await h.CallAsync(
                "sparring-watch-prepare",
                "test/sparring_watch_prepare",
                new JsonObject { ["x"] = cx, ["z"] = cz, ["bystanders"] = 1, ["drafted"] = true, ["working"] = true },
                cancellationToken).ConfigureAwait(false);
            if (watch["success"].AsBool() != true)
            {
                throw new AcceptanceException($"sparring_watch_prepare: {watch.ToJson()}");
            }

            var free = LabCases.StringList(watch["free"]);
            if (free.Count == 0)
            {
                throw new AcceptanceException($"sparring_watch_prepare: no free bystander: {watch.ToJson()}");
            }

            JsonObject? last = null;
            try
            {
                _ = await RunAt.RunUntilAsync(
                    h,
                    "sparring-watch",
                    20000,
                    new Wait { Stall = Wait.StallBudget() },
                    async ct =>
                    {
                        var got = await h.CallAsync("sparring-watch-inspect", "test/sparring_watch_inspect", [], ct).ConfigureAwait(false);
                        last = got;
                        var done = false;
                        var count = 0;
                        foreach (var w in Watchers(got))
                        {
                            count++;

                            // A long watch ended, with time to read its thought.
                            if (w.Id == free[0] && w.Ended >= 0 && w.AtCell >= 350 && got["tick"].AsNumber() > w.Ended + 5.0)
                            {
                                done = true;
                            }
                        }

                        return (Wait.Signature(count), done);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                report["watch"] = last?.DeepClone();
                throw new AcceptanceException($"watch stage: {e.Message}", e);
            }

            report["watch"] = last?.DeepClone();
            CheckWatch(prepared, watch, last ?? []);

            // Stage 2: a ring with no bout (nobody to fight): a bystander at joy time watches nothing.
            _ = await LabSparring.PrepareAsync(s, new JsonObject { ["eligible"] = 0, ["levels"] = "5" }, cancellationToken).ConfigureAwait(false);
            _ = await h.CallAsync(
                "sparring-watch-prepare-idle",
                "test/sparring_watch_prepare",
                new JsonObject { ["x"] = cx, ["z"] = cz, ["bystanders"] = 1 },
                cancellationToken).ConfigureAwait(false);
            double start = -1;
            _ = await RunAt.RunUntilAsync(
                h,
                "sparring-watch-none",
                6000,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var got = await h.CallAsync("sparring-watch-inspect-wait", "test/sparring_watch_inspect", [], ct).ConfigureAwait(false);
                    var tick = got["tick"].AsNumber();
                    if (start < 0)
                    {
                        start = tick;
                    }

                    return ("", tick >= start + 2500);
                },
                cancellationToken).ConfigureAwait(false);
            var idle = await h.CallAsync("sparring-watch-inspect-idle", "test/sparring_watch_inspect", [], cancellationToken).ConfigureAwait(false);
            report["noBout"] = idle.DeepClone();
            var n = idle["watchJobs"].AsNumber();
            if (n != 0)
            {
                throw new AcceptanceException($"a colonist watched for {LabSparring.Num(n)} ticks with no bout fighting: {idle.ToJson()}");
            }
        }
        finally
        {
            s.Report["sparringWatch"] = report;
        }
    }

    private static void CheckWatch(JsonObject prepared, JsonObject watch, JsonObject got)
    {
        var free = LabCases.StringList(watch["free"]);
        var fighters = LabCases.StringList(prepared["eligible"]);
        var fighterWatch = got["fighterWatchTicks"].AsNumber();
        if (fighterWatch != 0)
        {
            throw new AcceptanceException($"a pawn in a bout was on a watch job for {LabSparring.Num(fighterWatch)} ticks: {got.ToJson()}");
        }

        var drafted = watch["drafted"].AsString();
        var working = watch["working"].AsString();
        var longWatches = 0;
        foreach (var w in Watchers(got))
        {
            if (w.Id == drafted || w.Id == working)
            {
                throw new AcceptanceException($"{w.Id} (drafted or on an all-work schedule) watched: {got.ToJson()}");
            }

            if (fighters.Contains(w.Id, StringComparer.Ordinal) || w.Fighter)
            {
                throw new AcceptanceException($"fighter {w.Id} watched: {got.ToJson()}");
            }

            if (w.Id != free[0])
            {
                continue;
            }

            if (!w.CellOk)
            {
                throw new AcceptanceException($"spectator cell is not valid ({w.CellNote}, {LabSparring.Fixed(w.Dist, 1)} cells from a fighter): {got.ToJson()}");
            }

            if (w.Ended < 0)
            {
                continue;
            }

            if (w.FightingEnd)
            {
                throw new AcceptanceException($"the watch ended at tick {w.Ended} while a bout was still fighting: {got.ToJson()}");
            }

            // The thought needs a few hundred ticks of watching; the edges are left unjudged.
            if (w.AtCell >= 350 && !w.Thought)
            {
                throw new AcceptanceException($"no watched-a-sparring-match thought after {w.AtCell} watching ticks: {got.ToJson()}");
            }

            if (w.AtCell <= 250 && w.Thought)
            {
                throw new AcceptanceException($"watched-a-sparring-match thought after only {w.AtCell} watching ticks: {got.ToJson()}");
            }

            if (w.AtCell >= 350 && w.JoyMax <= w.JoyStart)
            {
                throw new AcceptanceException($"joy did not rise while watching ({LabSparring.Fixed(w.JoyStart, 3)} to {LabSparring.Fixed(w.JoyMax, 3)}): {got.ToJson()}");
            }

            if (w.AtCell >= 350)
            {
                longWatches++;
            }
        }

        if (longWatches == 0)
        {
            throw new AcceptanceException($"the free bystander never completed a long watch: {got.ToJson()}");
        }
    }
}
