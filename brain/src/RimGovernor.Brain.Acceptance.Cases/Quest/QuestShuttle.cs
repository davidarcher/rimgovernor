using System.Globalization;
using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/shuttle</c> (Go <c>shuttle.go</c>): native autoload toggle, explicit pawn boarding and loaded shuttle launch.
/// </summary>
internal static class QuestShuttle
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/shuttle",
        Scope = "Native autoload toggle, explicit pawn boarding and loaded shuttle launch. A Go snapshot cannot cover vanilla EnterTransporter work and transport-ship departure; actual passenger containers and spawned state are asserted.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/quest_shuttle_prepare" },
        NoKeep = true,
        Crew = new Crew { Size = 3 },
        Expansions = ["ludeon.rimworld.royalty"],
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        RequiredOps = ["test/quest_shuttle_prepare", "test/quest_shuttle_read"],
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var id = s.Identity;
        var prepared = s.Prepared ?? [];
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", id, cancellationToken).ConfigureAwait(false);

        async Task Apply(string label, JsonObject intent)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = id.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = label, ["questShuttle"] = intent }),
                },
                cancellationToken).ConfigureAwait(false);
            var rows = reply["results"].AsSlice();
            if (rows.Count != 1)
            {
                throw new AcceptanceException($"{label}: missing action result: {reply.ToJson()}");
            }

            var row = rows[0].AsMap() ?? [];
            if (row["applied"].AsMap()?["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"{label}: shuttle action not applied: {row.ToJson()}");
            }
        }

        Task<JsonObject> Read(string label, string quest) =>
            h.CallAsync(label, "test/quest_shuttle_read", new JsonObject { ["questId"] = quest }, cancellationToken);

        var cases = new (string Prefix, string Quest, string Pawn, bool Auto)[]
        {
            ("named", prepared["namedQuest"].AsString(), prepared["namedPawn"].AsString(), true),
            ("counted", prepared["countedQuest"].AsString(), prepared["countedPawn"].AsString(), false),
        };
        foreach (var tc in cases)
        {
            if (tc.Quest.Length == 0 || tc.Pawn.Length == 0)
            {
                throw new AcceptanceException($"missing fixture ids: {prepared.ToJson()}");
            }

            var intent = new JsonObject { ["questId"] = tc.Quest };
            if (tc.Auto)
            {
                intent["autoload"] = true;
            }
            else
            {
                intent["explicitPawns"] = new JsonObject { ["pawnIds"] = new JsonArray(tc.Pawn) };
            }

            await Apply(tc.Prefix + "-load", intent).ConfigureAwait(false);
            var loaded = false;
            for (var advanced = 0; advanced <= 6000; advanced += 300)
            {
                var row = await Read(string.Create(CultureInfo.InvariantCulture, $"{tc.Prefix}-board-{advanced}"), tc.Quest).ConfigureAwait(false);
                if (Flag(row["loaded"]))
                {
                    if (!row["pawnIds"].AsSlice().Any(pawn => pawn.AsString() == tc.Pawn))
                    {
                        throw new AcceptanceException($"{tc.Prefix}: loaded without exact pawn: {row.ToJson()}");
                    }

                    loaded = true;
                    break;
                }

                if (advanced == 6000)
                {
                    break;
                }

                _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            }

            if (!loaded)
            {
                throw new AcceptanceException($"{tc.Prefix}: boarding did not complete within 6000 ticks");
            }

            await Apply(tc.Prefix + "-launch", new JsonObject { ["questId"] = tc.Quest, ["launch"] = true }).ConfigureAwait(false);
            var departed = false;
            for (var advanced = 0; advanced <= 3000; advanced += 300)
            {
                var row = await Read(string.Create(CultureInfo.InvariantCulture, $"{tc.Prefix}-depart-{advanced}"), tc.Quest).ConfigureAwait(false);
                if (row["spawned"].AsBool() is false)
                {
                    departed = true;
                    break;
                }

                if (advanced == 3000)
                {
                    break;
                }

                _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            }

            if (!departed)
            {
                throw new AcceptanceException($"{tc.Prefix}: shuttle did not depart");
            }
        }
    }
}
