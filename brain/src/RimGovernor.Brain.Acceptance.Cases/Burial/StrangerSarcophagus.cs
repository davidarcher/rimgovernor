using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>burial/stranger_sarcophagus</c>: the KnowBuriedInSarcophagus probe, a native haul that gives no memory.</summary>
internal static class StrangerSarcophagus
{
    private const string TombStage = "test/tomb_stage";
    private const string TombRead = "test/tomb_read";

    // One vanilla haul into the sarcophagus, as the grave case bounds its haul.
    private const int TombWindow = 2 * Wait.TicksPerDay;

    // Ticks the read waits after the haul, so a memory gained a moment late would show.
    private const int Settle = 600;

    internal static AcceptanceCase Case() => new()
    {
        Name = "burial/stranger_sarcophagus",
        Scope = "Issue #3242, the KnowBuriedInSarcophagus probe (epic #3217): a colonist natively hauls a stranger corpse into a fresh "
            + "sarcophagus, and no colonist gains KnowBuriedInSarcophagus and the corpse's everBuriedInSarcophagus stays false. "
            + "Building_Sarcophagus.Notify_HauledTo returns unless thisIsFirstBodyEver, which only TryAcceptThing sets and no haul path "
            + "calls, so the memory is unreachable by hauling. Vanilla physics no snapshot test over recorded facts covers; it is the "
            + "negative benchmark the mechanic-card gate cites.",
        Start = new FixtureStart { Op = TombStage, On = new LabStart() },
        RequiredOps = [TombStage, TombRead],
        QuietWorld = true,
        Keep = ["Mood"],
        VanillaColonists = true,
        Reason = "Mood stays live: the assertion reads the memory a burial would give. VanillaColonists: colonists haul the corpse on "
            + "their own, the vanilla Notify_HauledTo path the probe observes.",
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed class TombState
    {
        public bool Holds { get; set; }

        public List<int> Memories { get; } = [];

        public List<bool> Moods { get; } = [];

        public Dictionary<string, JsonObject> Corpses { get; } = [];

        public override string ToString() => $"holds={Holds} memories=[{string.Join(',', Memories)}] moods=[{string.Join(',', Moods)}] corpses={new JsonObject(Corpses.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value.DeepClone()))).ToJsonString()}";
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var prepared = s.Prepared ?? [];
        var sarcophagus = prepared["sarcophagus"].AsString();
        var corpse = prepared["corpse"].AsString();
        if (sarcophagus.Length == 0 || corpse.Length == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture ids: {prepared.ToJson()}");
        }

        async Task<TombState> Read(string label)
        {
            var reply = await h.CallAsync(label, TombRead, new JsonObject { ["sarcophagus"] = sarcophagus, ["ids"] = "," + corpse }, cancellationToken).ConfigureAwait(false);
            var st = new TombState { Holds = reply["holds"].AsBool() == true };
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

        bool Flag(TombState st, string key) => st.Corpses.TryGetValue(corpse, out var row) && row[key].AsBool() == true;

        // The native haul: polled every 300 ticks, bounded.
        TombState buried;
        for (var advanced = 0; ; advanced += 300)
        {
            buried = await Read($"burial-{CaseSupport.Tag(advanced)}").ConfigureAwait(false);
            if (Flag(buried, "inSarcophagus"))
            {
                s.Report["burial_ticks"] = advanced;
                break;
            }

            if (advanced >= TombWindow)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the stranger corpse was not hauled into the sarcophagus within {advanced} ticks: {buried}"));
            }

            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
        }

        _ = await s.AdvanceAsync(Settle, null, cancellationToken).ConfigureAwait(false);
        var settled = await Read("settled").ConfigureAwait(false);
        if (settled.Memories.Count == 0)
        {
            throw new AcceptanceException($"no colonist read: {settled}");
        }

        s.Report["colonists"] = settled.Memories.Count;
        s.Report["memories"] = new JsonArray([.. settled.Memories.Select(m => (JsonNode?)m)]);
        for (var i = 0; i < settled.Memories.Count; i++)
        {
            if (!settled.Moods[i] || settled.Memories[i] != 0)
            {
                throw new AcceptanceException($"colonist {i} has {settled.Memories[i]} {PolicyFunctions.KnowBuriedInSarcophagusThought} memories (mood need {settled.Moods[i]}), want 0: {settled}");
            }
        }

        if (!Flag(settled, "inSarcophagus") || Flag(settled, "everBuried"))
        {
            throw new AcceptanceException($"the hauled corpse is not in the sarcophagus with everBuriedInSarcophagus false: {settled}");
        }
    }
}
