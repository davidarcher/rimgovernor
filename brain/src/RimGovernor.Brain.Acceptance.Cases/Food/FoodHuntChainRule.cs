using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>food/hunt-chain-rule</c> (Go <c>huntchainrule.go</c>).</summary>
internal static class FoodHuntChainRule
{
    private const string PrepareOp = "test/hunt_chain_prepare";
    private const string ObserveOp = "test/hunt_chain_observe";
    private const string ChainId = PolicyFunctions.HuntChainRuleId;
    private const long ChainLease = 2500;

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/hunt-chain-rule",
        Scope = "Native postcondition and end-to-end signal (a Go snapshot test cannot cover a native rule firing on the kill hook): "
            + "one hunter and six designated deer; an out-of-whitelist rule is refused; the controller (service family rules, #2154) attaches the hunt-chain "
            + "rule itself and renews it before expiry through journaled rules_attach actions, and with it attached the hunter's job two ticks after the first kill is Hunt on another "
            + "live designated deer, not the vanilla haul of its own kill; with rules cleared the next kill "
            + "leaves vanilla behaviour; after the lease expires nothing fires.",
        Start = new FixtureStart { Op = PrepareOp, On = new LabStart() },
        Serve = new ServeSpec { Families = ["acquisition", "work", "rules"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "hunt-chain" },
        QuietWorld = true,
        RequiredOps = [ObserveOp],
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Reason = "a lab with one ranger, a corpse stockpile and six deer; one controller phase until two timely rules_attach receipts, then three native kills",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        foreach (var tool in new[] { "rimgovernor/rules_attach", "rimgovernor/rules_clear", "rimgovernor/rules_read_status" })
        {
            if (!Dyn.Contains(s.Names, tool))
            {
                throw new AcceptanceException($"missing {tool} in discovery");
            }
        }

        Task<JsonObject> Observe(string label) => h.CallAsync(label, ObserveOp, null, cancellationToken);
        async Task<JsonObject> Status(string label)
        {
            var reply = await h.WireAsync(label, "rules_read_status", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
            return Harness.Outcome(reply, "status").Value;
        }

        async Task RecordsAtLeast(string label, int n)
        {
            var current = await Observe(label + "-before").ConfigureAwait(false);
            if (current["records"].AsSlice().Count >= n)
            {
                return;
            }

            _ = await NativeReads.RunUntilAsync(s, label, 6000, async ct =>
            {
                var v = await h.CallAsync(label + "-progress", ObserveOp, null, ct).ConfigureAwait(false);
                s.Report["chain_tally"] = v;
                return (Wait.Signature(v["tick"].ToJson(), v["kills"].ToJson()), v["records"].AsSlice().Count >= n);
            }, quiet: true, cancellationToken).ConfigureAwait(false);
        }

        async Task<JsonObject> Record(int i)
        {
            var v = await Observe("record").ConfigureAwait(false);
            var rows = v["records"].AsSlice();
            if (rows.Count <= i)
            {
                throw new AcceptanceException($"want kill record {i}, have {rows.Count}: {v.ToJson()}");
            }

            return rows[i].AsMap() ?? [];
        }

        // The fixture stages pawns outside an owned scope, so authority is granted after it.
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "grant", identity, cancellationToken).ConfigureAwait(false);

        static JsonObject Rule(string id, string job) => new()
        {
            ["id"] = id,
            ["trigger"] = "RULE_TRIGGER_PREY_KILLED",
            ["predicates"] = NativeReads.Strings("RULE_PREDICATE_ACTOR_UNDRAFTED", "RULE_PREDICATE_ACTOR_HUNTING_WORK_ACTIVE", "RULE_PREDICATE_TARGET_AVAILABLE"),
            ["action"] = "RULE_ACTION_GIVE_JOB",
            ["job"] = job,
            ["target"] = "RULE_TARGET_SELECTOR_NEAREST_DESIGNATED_PREY",
            ["radius"] = 80,
        };

        async Task<JsonObject> Attach(string label, long lease, params JsonObject[] rules)
        {
            var start = await Observe(label + "-tick").ConfigureAwait(false);
            var reply = await h.WireAsync(
                label,
                "rules_attach",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["rules"] = new JsonArray([.. rules.Select(r => (JsonNode?)r)]),
                    ["expiresAtTick"] = ((long)(start["tick"].AsNumber() + lease)).ToString(CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false);
            return Harness.Outcome(reply, "attached").Value;
        }

        // The whitelist refuses a draft job; the probe attaches nothing that lasts.
        var attached = await Attach("attach", ChainLease, Rule(ChainId, "Hunt"), Rule("draft", "Draft")).ConfigureAwait(false);
        var refused = attached["refused"].AsSlice();
        var ids = attached["acceptedIds"].AsSlice();
        if (ids.Count != 1 || ids[0].AsString() != ChainId || refused.Count != 1)
        {
            throw new AcceptanceException($"want only {ChainId} accepted and the draft rule refused: {attached.ToJson()}");
        }

        var refusal = refused[0].AsMap();
        if (refusal?["ruleId"].AsString() != "draft" || refusal["reason"].AsString() != "RULE_REFUSAL_REASON_UNSUPPORTED_JOB")
        {
            throw new AcceptanceException($"the draft rule must be refused as an unsupported job: {refused[0].ToJson()}");
        }

        _ = await h.WireAsync("probe-clear", "rules_clear", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);

        // Rule active: the controller attaches the hunt-chain rule itself, its rules_attach receipt in the journal, and
        // the hunter chains to the next prey. The brain's journal belongs to the game, so the renewal is read from the
        // native rules status (each distinct lease expiry is one rules_attach) and the firing from the kill record.
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        var phase = BrainRun.Wrap(s, service, "hunt-chain");
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var expiries = new List<long>();
            var wait = new Wait { Ceiling = TimeSpan.FromMinutes(10), Stall = Wait.StallBudget(), Terminal = service.Exited, Interval = TimeSpan.FromMilliseconds(250) };
            try
            {
                await wait.WaitProgressAsync(async ct =>
                {
                    var standing = await Status("chain-renewal-status").ConfigureAwait(false);
                    var active = standing["rules"].Rows().Any(r => r["ruleId"].AsString() == ChainId);
                    if (active && standing["expiresAtTick"].AsUInt64() is { } expiresAt && (expiries.Count == 0 || expiries[^1] != (long)expiresAt))
                    {
                        expiries.Add((long)expiresAt);
                    }
                    else if (!active && expiries.Count == 1 && standing["leaseExpired"].AsBool() == true)
                    {
                        throw new AcceptanceException($"hunt-chain renewal missed lease: {string.Join(' ', expiries)}");
                    }

                    s.Report["chain_renewal_ticks"] = new JsonArray([.. expiries.Select(e => (JsonNode?)JsonValue.Create(e))]);
                    if (expiries.Count < 2)
                    {
                        return (Wait.Signature(string.Join(',', expiries)), false);
                    }

                    var seen = await h.CallAsync("chain-renewal-observe", ObserveOp, null, ct).ConfigureAwait(false);
                    var fired = seen["records"].AsSlice().Count;
                    return (Wait.Signature(string.Join(',', expiries), fired), fired > 0);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (WaitError e)
            {
                throw new AcceptanceException($"the controller attaches and renews the hunt-chain rule: {e.Message}", e);
            }
        }
        catch
        {
            _ = await phase.StopAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (await phase.StopAsync(cancellationToken).ConfigureAwait(false) is { } rulesService)
        {
            s.Report["rules_service"] = rulesService;
        }

        await NativeReads.PauseAsync(h, "pause-rules_service", cancellationToken).ConfigureAwait(false);
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "regrant", identity, cancellationToken).ConfigureAwait(false);
        var standingNow = await Status("controller-status").ConfigureAwait(false);
        var activeRules = standingNow["rules"].AsSlice();
        if (activeRules.Count != 1 || activeRules[0].AsMap()?["ruleId"].AsString() != ChainId || standingNow["leaseRemainingTicks"].AsNumber() <= 0)
        {
            throw new AcceptanceException($"want the controller's {ChainId} rule active under its lease: {standingNow.ToJson()}");
        }

        s.Report["chain_attached"] = standingNow.DeepClone();
        await RecordsAtLeast("chain-first", 1).ConfigureAwait(false);
        var first = await Record(0).ConfigureAwait(false);
        if (first["jobDef"].AsString() != "Hunt" || !first["targetLive"].Flag() || !first["targetDesignated"].Flag()
            || first["targetIsKilled"].Flag() || first["carryingCorpse"].Flag())
        {
            throw new AcceptanceException($"after the first kill the hunter must be on Hunt against another live designated deer, not hauling its kill: {first.ToJson()}");
        }

        var fired = await Status("fired-status").ConfigureAwait(false);
        if (fired["rules"].AsSlice().Count != 1)
        {
            throw new AcceptanceException($"want one active rule: {fired.ToJson()}");
        }

        // Go matched the native journal's RuleFired event (hunter, next target, kill tick) against this record; the
        // brain reads no clock inbox here, so the kill record's own Hunt-on-live-designated-prey state is the evidence.
        s.Report["chain_fired"] = new JsonObject { ["record"] = first.DeepClone(), ["status"] = fired.DeepClone() };

        // Renewal may have allowed more kills; clear is judged on the next one.
        var beforeClear = await Observe("before-clear").ConfigureAwait(false);
        var clearedIndex = beforeClear["records"].AsSlice().Count;
        if (beforeClear["designatedAlive"].AsNumber() < 3)
        {
            throw new AcceptanceException($"need three standing prey for clear and expiry checks: {beforeClear.ToJson()}");
        }

        var cleared = await h.WireAsync("clear", "rules_clear", new JsonObject { ["identity"] = identity.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var clearedBody = Harness.Outcome(cleared, "cleared").Value;
        if (clearedBody["cleared"].AsNumber() != 1)
        {
            throw new AcceptanceException($"clear must report the one active rule: {cleared.ToJson()}");
        }

        await RecordsAtLeast("chain-cleared", clearedIndex + 1).ConfigureAwait(false);
        var second = await Record(clearedIndex).ConfigureAwait(false);
        if (second["targetLive"].Flag())
        {
            throw new AcceptanceException($"with the rules cleared the hunter must keep vanilla behaviour after its kill: {second.ToJson()}");
        }

        var after = await Status("cleared-status").ConfigureAwait(false);
        if (after["rules"].AsSlice().Count != 0)
        {
            throw new AcceptanceException($"cleared rules must not stay active: {after.ToJson()}");
        }

        // Lease expired: nothing fires on a later kill while another target stands.
        var beforeExpiry = await Observe("before-expiry").ConfigureAwait(false);
        var expiredIndex = beforeExpiry["records"].AsSlice().Count;
        if (beforeExpiry["designatedAlive"].AsNumber() < 2)
        {
            throw new AcceptanceException($"need two standing prey for expiry check: {beforeExpiry.ToJson()}");
        }

        _ = await Attach("attach-short", 10, Rule(ChainId, "Hunt")).ConfigureAwait(false);
        _ = await NativeReads.RunUntilAsync(s, "chain-expiry", 600, async _ =>
        {
            var v = await Status("expiry-status").ConfigureAwait(false);
            return (Wait.Signature(v["leaseRemainingTicks"].ToJson()), v["leaseExpired"].AsBool() == true);
        }, quiet: true, cancellationToken).ConfigureAwait(false);
        await RecordsAtLeast("chain-expired", expiredIndex + 1).ConfigureAwait(false);
        var third = await Record(expiredIndex).ConfigureAwait(false);
        if (third["targetLive"].Flag())
        {
            throw new AcceptanceException($"after the lease expired nothing may fire: {third.ToJson()}");
        }

        var final = await Status("expired-status").ConfigureAwait(false);
        if (final["leaseExpired"].AsBool() != true || final["rules"].AsSlice().Count != 0)
        {
            throw new AcceptanceException($"the expired lease must leave no active rule: {final.ToJson()}");
        }

        s.Report["chain_expired"] = new JsonObject { ["record"] = third.DeepClone(), ["status"] = final.DeepClone() };
    }
}
