using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>burial/stranger_sarcophagus</c> (Go <c>burial/stranger_sarcophagus.go</c>).</summary>
internal static class StrangerSarcophagus
{
    private const string TombStage = "test/tomb_stage";
    private const string TombSecond = "test/tomb_second";
    private const string TombRead = "test/tomb_read";
    private const string TombFresh = "test/tomb_fresh";

    // One vanilla haul into the sarcophagus and the deconstruction work, as the grave case bounds its haul.
    private const int TombWindow = 2 * Wait.TicksPerDay;

    // How far (cells) the ejected corpse may land from the sarcophagus: ThingPlaceMode.Near on a 1x2 building.
    private const int TombNear = 4;

    internal static AcceptanceCase Case() => new()
    {
        Name = "burial/stranger_sarcophagus",
        Scope = "Issue #2338 stranger sarcophagus loop (epic #2209): a colonist hauls a stranger corpse into a fresh sarcophagus and every "
            + "colonist gains exactly one KnowBuriedInSarcophagus; the corpse carries everBuriedInSarcophagus; a second stranger corpse buried "
            + "in the same, now used, sarcophagus gives nothing; a DECONSTRUCT designation (the planner's TombDispose action) ejects the corpse "
            + "beside the cell, spawned. Vanilla Building_Sarcophagus.Notify_HauledTo, everNonEmpty and "
            + "Building_Casket.Destroy physics that no snapshot test over recorded facts covers; the planner decision (TombDispose, cap, funding, "
            + "no re-staging of a flagged corpse) is snapshot-tested in policy and buildingruntime. "
            + "It is also the KnowBuriedInSarcophagus probe (#3222) that verifies the mechanic card: the first burial is the positive path; "
            + "each guard of Notify_HauledTo has a negative asserting no colonist gains the memory: thisIsFirstBodyEver by second_burial "
            + "(the used sarcophagus), and, each into a fresh sarcophagus, InnerPawn.def.race.Humanlike by animal_burial (an animal corpse), "
            + "!everBuriedInSarcophagus by flagged_burial (the first corpse, already flagged) and hauler.IsColonist by visitor_burial "
            + "(a non-colonist on a forced haul).",
        Start = new FixtureStart { Op = TombStage, On = new LabStart() },
        RequiredOps = [TombStage, TombSecond, TombRead, TombFresh],
        QuietWorld = true,
        Keep = ["Mood"],
        Reason = "Mood stays live: the assertion reads the memory it gains.",
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed class TombState
    {
        public bool Present { get; set; }

        public bool Holds { get; set; }

        public string HaulerJob { get; set; } = "";

        public List<int> Memories { get; } = [];

        public List<bool> Moods { get; } = [];

        public Dictionary<string, JsonObject> Corpses { get; } = [];

        public override string ToString() => $"present={Present} holds={Holds} haulerJob={HaulerJob} memories=[{string.Join(',', Memories)}] moods=[{string.Join(',', Moods)}] corpses={new JsonObject(Corpses.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value.DeepClone()))).ToJsonString()}";
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var prepared = s.Prepared ?? [];
        var sarcophagus = prepared["sarcophagus"].AsString();
        var first = prepared["corpse"].AsString();
        if (sarcophagus.Length == 0 || first.Length == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture ids: {prepared.ToJson()}");
        }

        var cellX = (int)prepared["sarcophagusX"].AsNumber();
        var cellZ = (int)prepared["sarcophagusZ"].AsNumber();

        // The sarcophagus (and forced hauler) the reads look at: the first one until the probe negatives stage fresh ones.
        var watched = sarcophagus;
        var hauler = "";

        async Task<TombState> Read(string label, params string[] ids)
        {
            var joined = string.Concat(ids.Select(id => "," + id));
            var reply = await h.CallAsync(label, TombRead, new JsonObject { ["sarcophagus"] = watched, ["ids"] = joined, ["hauler"] = hauler }, cancellationToken).ConfigureAwait(false);
            var st = new TombState
            {
                Present = reply["present"].AsBool() == true,
                Holds = reply["holds"].AsBool() == true,
                HaulerJob = reply["haulerJob"].AsString(),
            };
            foreach (var raw in reply["colonists"].AsSlice())
            {
                var row = raw.AsMap() ?? [];
                st.Moods.Add(row["mood"].AsBool() == true);
                st.Memories.Add((int)row["memories"].AsNumber());
            }

            foreach (var raw in reply["corpses"].AsSlice())
            {
                if (raw.AsMap() is { } row)
                {
                    st.Corpses[row["id"].AsString()] = row;
                }
            }

            return st;
        }

        // Polls label's read every 300 ticks until done holds, bounded.
        async Task<TombState> Until(string label, string what, string[] ids, Func<TombState, bool> done)
        {
            for (var advanced = 0; ; advanced += 300)
            {
                var st = await Read($"{label}-{CaseSupport.Tag(advanced)}", ids).ConfigureAwait(false);
                if (done(st))
                {
                    s.Report[label + "_ticks"] = advanced;
                    return st;
                }

                if (advanced >= TombWindow)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{what}: not within {advanced} ticks: {st}"));
                }

                _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            }
        }

        static bool Flag(TombState st, string id, string key) => st.Corpses.TryGetValue(id, out var row) && row[key].AsBool() == true;
        static bool Inside(TombState st, string id) => Flag(st, id, "inSarcophagus");

        // Asserts every colonist carries want KnowBuriedInSarcophagus memories.
        static void Everyone(TombState st, int want, string when)
        {
            if (st.Memories.Count == 0)
            {
                throw new AcceptanceException($"{when}: no colonist read");
            }

            for (var i = 0; i < st.Memories.Count; i++)
            {
                if (!st.Moods[i] || st.Memories[i] != want)
                {
                    throw new AcceptanceException($"{when}: colonist {i} has {st.Memories[i]} {PolicyFunctions.KnowBuriedInSarcophagusThought} memories (mood need {st.Moods[i]}), want {want}: {st}");
                }
            }
        }

        // Burial one: the first body ever in a fresh sarcophagus.
        var buried = await Until("first_burial", "the stranger corpse was not hauled into the sarcophagus", [first], st => Inside(st, first)).ConfigureAwait(false);
        if (!Flag(buried, first, "everBuried"))
        {
            throw new AcceptanceException($"the buried corpse lacks everBuriedInSarcophagus: {buried}");
        }

        Everyone(buried, 1, "after the first burial");
        s.Report["colonists"] = buried.Memories.Count;

        // Burial two: the same sarcophagus again, with a fresh stranger corpse.
        var reply = await h.CallAsync("second-stage", TombSecond, new JsonObject { ["sarcophagus"] = sarcophagus }, cancellationToken).ConfigureAwait(false);
        var second = reply["second"].AsString();
        if (second.Length == 0)
        {
            throw new AcceptanceException($"tomb_second: no second corpse: {reply.ToJson()}");
        }

        var again = await Until("second_burial", "the second stranger corpse was not hauled into the sarcophagus", [first, second], st => Inside(st, second)).ConfigureAwait(false);
        Everyone(again, 1, "after the second burial");
        if (!Flag(again, first, "everBuried"))
        {
            throw new AcceptanceException($"the ejected first corpse lost everBuriedInSarcophagus: {again}");
        }

        s.Report["second_burial_flag"] = Flag(again, second, "everBuried");

        // Disposal: the planner's TombDispose action, a DECONSTRUCT designation.
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "tomb-authority", s.Identity, cancellationToken).ConfigureAwait(false);
        var designation = new JsonObject
        {
            ["designation"] = "THING_DESIGNATION_DECONSTRUCT",
            ["target"] = new JsonObject { ["id"] = sarcophagus },
            ["guard"] = "DESIGNATION_GUARD_ENCLOSURE",
        };
        var applied = await h.WireAsync(
            "tomb-dispose",
            "operations_apply",
            new JsonObject
            {
                ["identity"] = s.Identity.DeepClone(),
                ["actions"] = new JsonArray(new JsonObject { ["key"] = "tomb-dispose", ["designate"] = designation }),
            },
            cancellationToken).ConfigureAwait(false);
        var result = CaseSupport.OneResult("tomb-dispose", applied);
        if (result["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"tomb-dispose: not applied: {result.ToJson()}");
        }

        var gone = await Until("deconstruct", "the sarcophagus was not deconstructed", [first, second], st => !st.Present).ConfigureAwait(false);
        if (!Flag(gone, second, "spawned") || Inside(gone, second))
        {
            throw new AcceptanceException($"the corpse was not ejected into the open: {gone.Corpses.GetValueOrDefault(second).ToJson()}");
        }

        var ejected = gone.Corpses[second];
        var x = (int)ejected["x"].AsNumber();
        var z = (int)ejected["z"].AsNumber();
        var dx = x - cellX;
        var dz = z - cellZ;
        s.Report["ejected_at"] = new JsonObject { ["x"] = x, ["z"] = z, ["sarcophagus"] = CaseSupport.Cell(cellX, cellZ) };
        s.Report["ejected_rot"] = ejected["rot"]?.DeepClone();
        if (dx < -TombNear || dx > TombNear || dz < -TombNear || dz > TombNear)
        {
            throw new AcceptanceException($"the corpse landed ({x},{z}), more than {TombNear} cells from the sarcophagus at ({cellX},{cellZ})");
        }

        s.Report["ejected_flag"] = Flag(gone, second, "everBuried");

        // Deconstruction gives no memory either.
        Everyone(gone, 1, "after the deconstruction");

        // The probe negatives (#3222): a fresh sarcophagus each, so thisIsFirstBodyEver holds and exactly one other guard fails.
        foreach (var (kind, guard) in Negatives)
        {
            var staged = await h.CallAsync($"{kind}-stage", TombFresh, new JsonObject { ["kind"] = kind, ["corpse"] = first }, cancellationToken).ConfigureAwait(false);
            var corpse = staged["corpse"].AsString();
            watched = staged["sarcophagus"].AsString();
            hauler = staged["hauler"].AsString();
            if (corpse.Length == 0 || watched.Length == 0 || (kind == "visitor" && (staged["ordered"].AsBool() != true || staged["haulerColonist"].AsBool() != false)))
            {
                throw new AcceptanceException($"tomb_fresh {kind}: not staged: {staged.ToJson()}");
            }

            var arrived = await Until($"{kind}_burial", $"the {kind} corpse was not hauled into its fresh sarcophagus", [corpse], st =>
            {
                if (kind == "visitor" && !Inside(st, corpse) && st.HaulerJob != "HaulToContainer")
                {
                    throw new AcceptanceException($"the visitor dropped its forced haul: {st}");
                }

                return Inside(st, corpse);
            }).ConfigureAwait(false);
            Everyone(arrived, 1, $"after the {kind} burial ({guard})");
            s.Report[kind + "_staged"] = staged.DeepClone();
            s.Report[kind + "_flag"] = Flag(arrived, corpse, "everBuried");
            if (kind != "flagged" && Flag(arrived, corpse, "everBuried"))
            {
                throw new AcceptanceException($"the {kind} corpse gained everBuriedInSarcophagus past a failed guard: {arrived}");
            }
        }
    }

    // Each probe negative with the Building_Sarcophagus.Notify_HauledTo guard it fails; the used-sarcophagus second burial fails thisIsFirstBodyEver.
    private static readonly (string Kind, string Guard)[] Negatives =
    [
        ("animal", "base.Corpse.InnerPawn.def.race.Humanlike"),
        ("flagged", "!base.Corpse.everBuriedInSarcophagus"),
        ("visitor", "hauler.IsColonist"),
    ];
}
