using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/give-item</c> (Go <c>give_item.go</c>): native whole-request count fencing and ordinary GiveToPawn delivery to
/// an Ideology beggar.
/// </summary>
internal static class QuestGiveItem
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "quest/give-item",
        Scope = "Native whole-request count fencing and ordinary GiveToPawn delivery to an Ideology beggar. Go snapshots cannot prove vanilla hauling transfers the requested items into the exact visitor's inventory.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/quest_give_prepare" },
        NoKeep = true,
        Crew = new Crew { Size = 3 },
        Expansions = ["ludeon.rimworld.ideology"],
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(3),
        RequiredOps = ["test/quest_give_prepare", "test/quest_give_read"],
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var hauler = prepared["haulerId"].AsString();
        var recipient = prepared["recipientId"].AsString();
        if (hauler.Length == 0 || recipient.Length == 0 || prepared["remaining"].AsNumber() != 20)
        {
            throw new AcceptanceException($"invalid giving fixture: {prepared.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Apply(string key, int remaining)
        {
            var reply = await h.WireAsync(
                key,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["giveItem"] = new JsonObject
                        {
                            ["hauler"] = new JsonObject { ["id"] = hauler },
                            ["recipient"] = new JsonObject { ["id"] = recipient },
                            ["definition"] = "Silver",
                            ["expectedRemaining"] = remaining,
                        },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            return results.Count != 1
                ? throw new AcceptanceException($"missing giving result: {reply.ToJson()}")
                : results[0].AsMap() ?? [];
        }

        Task<JsonObject> Read() => h.CallAsync("giving-read", "test/quest_give_read", new JsonObject { ["recipientId"] = recipient, ["haulerId"] = hauler }, cancellationToken);

        var result = await Apply("giving-stale-count", 19).ConfigureAwait(false);
        if (result["refused"].AsMap()?["code"].AsString() != "FAILURE_CODE_INVALID_REQUEST")
        {
            throw new AcceptanceException($"changed native request count accepted: {result.ToJson()}");
        }

        var before = await Read().ConfigureAwait(false);
        if (before["received"].AsNumber() != 0 || before["remaining"].AsNumber() != 20 || before["job"].AsString() == "GiveToPawn")
        {
            throw new AcceptanceException($"refusal changed native delivery state: {before.ToJson()}");
        }

        result = await Apply("giving-whole-request", 20).ConfigureAwait(false);
        if (result["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"whole request refused: {result.ToJson()}");
        }

        var started = await Read().ConfigureAwait(false);
        if (started["job"].AsString() != "GiveToPawn" || started["targetId"].AsString() != recipient)
        {
            throw new AcceptanceException($"exact vanilla delivery job missing: {started.ToJson()}");
        }

        var tick = (ulong)started["tick"].AsNumber();
        var wait = new Wait
        {
            Ceiling = TimeSpan.FromMinutes(2),
            Stall = Wait.StallBudget(),
            Ticks = 3000,
            Tick = _ => Task.FromResult(tick),
        };
        await wait.WaitProgressAsync(
            async ct =>
            {
                var row = await Read().ConfigureAwait(false);
                tick = (ulong)row["tick"].AsNumber();
                if (row["remaining"].AsNumber() == 0 && row["received"].AsNumber() == 20)
                {
                    return (Wait.Signature(row["received"].ToJson(), row["remaining"].ToJson()), true);
                }

                _ = await s.AdvanceAsync(60, null, ct).ConfigureAwait(false);
                return (Wait.Signature(row["received"].ToJson(), row["remaining"].ToJson(), row["job"].ToJson()), false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
