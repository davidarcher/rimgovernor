using System;
using System.Linq;
using HomeBridge.BridgeTools;
using Operations = RimGovernor.Protocol.Operations;

// The declarative rule book (#2152): attach is replace-all and idempotent by id, a rule outside the v1
// whitelist is refused with a reason, and a lease expires once and deactivates every rule. The game side (kill hook, target search, the Hunt write) runs in acceptance.
internal static class NativeRuleRuntimeProbe
{
    private static Operations.Rule Hunt(string id, Action<Operations.Rule> edit = null)
    {
        var rule = new Operations.Rule
        {
            Id = id, Trigger = Operations.RuleTrigger.PreyKilled, Action = Operations.RuleAction.GiveJob, Job = "Hunt",
            Target = Operations.RuleTargetSelector.NearestDesignatedPrey, Radius = 40, PredatorMarginCells = 25,
        };
        rule.Predicates.Add(Operations.RulePredicate.ActorUndrafted);
        rule.Predicates.Add(Operations.RulePredicate.ActorHuntingWorkActive);
        rule.Predicates.Add(Operations.RulePredicate.TargetAvailable);
        edit?.Invoke(rule);
        return rule;
    }

    internal static void Invoke()
    {
        var book = new NativeRuleBook();

        // Attach: accepted ids in request order, replace-all, idempotent by id.
        var attached = book.Attach(new[] { Hunt("a"), Hunt("b") }, 2500);
        Require(attached.AcceptedIds.SequenceEqual(new[] { "a", "b" }) && attached.Refused.Count == 0, "both rules are accepted");
        Require(book.Active.Count == 2 && book.ExpiresAtTick == 2500, "two rules and the lease are active");
        attached = book.Attach(new[] { Hunt("a") }, 3000);
        Require(book.Active.Count == 1 && book.Active[0].Rule.Id == "a" && book.ExpiresAtTick == 3000, "a second attach replaces the first");
        attached = book.Attach(new[] { Hunt("a") }, 3000);
        Require(attached.AcceptedIds.Count == 1 && book.Active.Count == 1, "the same attach again changes nothing");

        // Refusals: each names its rule and one reason; the accepted rules still attach.
        var cases = new (string Name, Operations.Rule Rule, Operations.RuleRefusalReason Reason)[]
        {
            ("blank id", Hunt(" "), Operations.RuleRefusalReason.InvalidId),
            ("trigger", Hunt("t", r => r.Trigger = Operations.RuleTrigger.Unspecified), Operations.RuleRefusalReason.UnsupportedTrigger),
            ("predicate", Hunt("p", r => r.Predicates.Add(Operations.RulePredicate.Unspecified)), Operations.RuleRefusalReason.UnsupportedPredicate),
            ("repeated predicate", Hunt("q", r => r.Predicates.Add(Operations.RulePredicate.ActorUndrafted)), Operations.RuleRefusalReason.UnsupportedPredicate),
            ("action", Hunt("x", r => r.Action = Operations.RuleAction.Unspecified), Operations.RuleRefusalReason.UnsupportedAction),
            ("draft job", Hunt("d", r => r.Job = "Draft"), Operations.RuleRefusalReason.UnsupportedJob),
            ("selector", Hunt("s", r => r.Target = Operations.RuleTargetSelector.Unspecified), Operations.RuleRefusalReason.UnsupportedTarget),
            ("margin zero", Hunt("m0", r => r.PredatorMarginCells = 0), Operations.RuleRefusalReason.InvalidPredatorMargin),
            ("margin absent", Hunt("m1", r => r.ClearPredatorMarginCells()), Operations.RuleRefusalReason.InvalidPredatorMargin),
        };
        foreach (var c in cases)
        {
            attached = book.Attach(new[] { Hunt("ok"), c.Rule }, 2500);
            Require(attached.AcceptedIds.SequenceEqual(new[] { "ok" }), c.Name + ": the valid rule still attaches");
            Require(attached.Refused.Count == 1 && attached.Refused[0].Reason == c.Reason, c.Name + ": refused as " + c.Reason);
        }
        attached = book.Attach(new[] { Hunt("dup"), Hunt("dup") }, 2500);
        Require(attached.Refused.Count == 1 && attached.Refused[0].Reason == Operations.RuleRefusalReason.DuplicateId, "a repeated id is refused");
        attached = book.Attach(Enumerable.Range(0, 40).Select(i => Hunt("r" + i)), 2500);
        Require(book.Active.Count == 40 && attached.Refused.Count == 0, "the rule count has no limit");

        book.Attach(new[] { Hunt("a"), Hunt("b") }, 2500);
        book.NoteFired(book.Active[0], "Thing_Human1", "Thing_Deer2", 100);

        // Status: the firing count and the last firing, the lease remaining.
        var status = book.Status(1000);
        Require(status.Rules.Count == 2 && status.Rules[0].FiringCount == 1 && status.Rules[0].LastFiredTick == 100
            && status.Rules[0].LastActorId == "Thing_Human1" && status.Rules[0].LastTargetId == "Thing_Deer2" && status.Rules[1].FiringCount == 0, "status carries the firing history");
        Require(status.ExpiresAtTick == 2500 && status.LeaseRemainingTicks == 1500 && !status.LeaseExpired, "status carries the lease");

        // Lease expiry: nothing before the tick, one expiry at it, every rule deactivated, no second expiry.
        Require(!book.TryExpire(2499, out _) && book.Active.Count == 2, "no expiry before the lease tick");
        Require(book.TryExpire(2500, out var deactivated) && deactivated == 2 && book.Active.Count == 0, "the lease tick deactivates every rule");
        Require(!book.TryExpire(2501, out _), "expiry is reported once");
        status = book.Status(2600);
        Require(status.Rules.Count == 0 && status.LeaseExpired && status.LeaseRemainingTicks == 0, "status reads the expiry");
        book.Attach(new[] { Hunt("a") }, 3000);
        Require(!book.Status(2600).LeaseExpired && book.Active.Count == 1, "a new attach renews after expiry");

        // Clear.
        Require(book.Clear() == 1 && book.Active.Count == 0 && !book.Status(2600).LeaseExpired, "clear removes the rules");
        Require(book.Clear() == 0 && !book.TryExpire(9000, out _), "clearing nothing expires nothing");
        Console.WriteLine("native-rule-runtime: ok");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("native-rule-runtime: " + message);
    }
}
