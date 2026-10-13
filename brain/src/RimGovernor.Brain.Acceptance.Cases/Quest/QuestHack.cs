using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/hack</c> (Go <c>hack.go</c>): native autohack enable/disable, replay and unhackable-target refusal, followed
/// by ordinary Research WorkGiver hacking an Ideology terminal.
/// </summary>
internal static class QuestHack
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/hack",
        Scope = "Native autohack enable/disable, replay and unhackable-target refusal, followed by ordinary Research WorkGiver hacking an Ideology terminal. A Go snapshot cannot prove vanilla pawn work, CompHackable completion or the exact native hacker.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/quest_hack_prepare" },
        NoKeep = true,
        Crew = new Crew { Size = 3 },
        Expansions = ["ludeon.rimworld.ideology"],
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        RequiredOps = ["test/quest_hack_prepare", "test/quest_hack_read"],
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var terminal = prepared["terminalId"].AsString();
        var hacker = prepared["hackerId"].AsString();
        var unavailable = prepared["unhackableItemId"].AsString();
        if (terminal.Length == 0 || hacker.Length == 0 || unavailable.Length == 0)
        {
            throw new AcceptanceException($"invalid hack fixture: {prepared.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Apply(string label, string key, string target, bool enabled)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["hackDesignation"] = new JsonObject { ["target"] = new JsonObject { ["id"] = target }, ["enabled"] = enabled },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            return results.Count != 1
                ? throw new AcceptanceException($"{label}: missing result: {reply.ToJson()}")
                : results[0].AsMap() ?? [];
        }

        Task<JsonObject> Read(string label) => h.CallAsync(label, "test/quest_hack_read", new JsonObject { ["terminalId"] = terminal }, cancellationToken);

        async Task AssertApplied(Task<JsonObject> pending)
        {
            var row = await pending.ConfigureAwait(false);
            if (row["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"hack action refused: {row.ToJson()}");
            }
        }

        var row = await Read("hack-before").ConfigureAwait(false);
        if (Flag(row["hacked"]) || row["progress"].AsNumber() != 0)
        {
            throw new AcceptanceException($"fixture supplied completion: {row.ToJson()}");
        }

        var result = await Apply("unhackable", "hack-unavailable", unavailable, true).ConfigureAwait(false);
        if (result["refused"].AsMap()?["code"].AsString() != "FAILURE_CODE_UNAVAILABLE")
        {
            throw new AcceptanceException($"unhackable target accepted: {result.ToJson()}");
        }

        await AssertApplied(Apply("enable", "hack-enable", terminal, true)).ConfigureAwait(false);
        await AssertApplied(Apply("enable-replay", "hack-enable", terminal, true)).ConfigureAwait(false);
        row = await Read("hack-enabled").ConfigureAwait(false);
        if (!Flag(row["autohack"]))
        {
            throw new AcceptanceException($"enable replay toggled off: {row.ToJson()}");
        }

        await AssertApplied(Apply("disable", "hack-disable", terminal, false)).ConfigureAwait(false);
        row = await Read("hack-disabled").ConfigureAwait(false);
        if (Flag(row["autohack"]))
        {
            throw new AcceptanceException($"disable did not apply: {row.ToJson()}");
        }

        await AssertApplied(Apply("work-enable", "hack-work", terminal, true)).ConfigureAwait(false);
        var tick = (ulong)row["tick"].AsNumber();
        var wait = new Wait
        {
            Ceiling = TimeSpan.FromMinutes(3),
            Stall = Wait.StallBudget(),
            Ticks = 6000,
            Tick = _ => Task.FromResult(tick),
        };
        await wait.WaitProgressAsync(
            async ct =>
            {
                var read = await Read("hack-work-read").ConfigureAwait(false);
                tick = (ulong)read["tick"].AsNumber();
                if (Flag(read["hacked"]))
                {
                    return read["hackerId"].AsString() != hacker
                        ? throw new AcceptanceException($"completion lacks exact ordinary hacker: {read.ToJson()}")
                        : (Wait.Signature(read.ToJson()), true);
                }

                _ = await s.AdvanceAsync(300, null, ct).ConfigureAwait(false);
                return (Wait.Signature(read.ToJson()), false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
