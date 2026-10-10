#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    // The attached declarative rules of one loaded game, with no game types:
    // validation of Go's rules, the lease and the status counters. NativeRuleRuntime evaluates the game side. The book
    // lives in memory only; a load starts empty and Go re-attaches each Round.
    internal sealed class NativeRuleBook
    {
        internal const string HuntJob = "Hunt";

        internal sealed class Entry
        {
            internal Operations.Rule Rule = new Operations.Rule();
            internal ulong Fired;
            internal long LastTick;
            internal string LastActor = "", LastTarget = "";
        }

        private readonly List<Entry> active = new List<Entry>();
        private long expiresAtTick;
        private bool leaseExpired;

        internal IReadOnlyList<Entry> Active => active;
        internal long ExpiresAtTick => expiresAtTick;
        internal bool LeaseExpired => leaseExpired;

        // The one reason v1 refuses the rule, or Unspecified when it is accepted.
        internal static Operations.RuleRefusalReason Validate(Operations.Rule? rule)
        {
            if (rule == null || !rule.HasId || !ProtoBoundary.IsIdentifier(rule.Id)) return Operations.RuleRefusalReason.InvalidId;
            if (!rule.HasTrigger || rule.Trigger != Operations.RuleTrigger.PreyKilled) return Operations.RuleRefusalReason.UnsupportedTrigger;
            if (rule.Predicates.Any(p => p != Operations.RulePredicate.ActorUndrafted && p != Operations.RulePredicate.ActorHuntingWorkActive
                && p != Operations.RulePredicate.TargetAvailable) || rule.Predicates.Distinct().Count() != rule.Predicates.Count)
                return Operations.RuleRefusalReason.UnsupportedPredicate;
            if (!rule.HasAction || rule.Action != Operations.RuleAction.GiveJob) return Operations.RuleRefusalReason.UnsupportedAction;
            if (!rule.HasJob || rule.Job != HuntJob) return Operations.RuleRefusalReason.UnsupportedJob;
            if (!rule.HasTarget || rule.Target != Operations.RuleTargetSelector.NearestDesignatedPrey) return Operations.RuleRefusalReason.UnsupportedTarget;
            if (!rule.HasPredatorMarginCells || float.IsNaN(rule.PredatorMarginCells) || float.IsInfinity(rule.PredatorMarginCells) || rule.PredatorMarginCells <= 0)
                return Operations.RuleRefusalReason.InvalidPredatorMargin;
            return Operations.RuleRefusalReason.Unspecified;
        }

        // Replaces every active rule with the accepted ones; a firing history
        // restarts with the attachment. Accepted rules keep request order.
        internal Operations.RulesAttached Attach(IEnumerable<Operations.Rule> rules, long expiresAt)
        {
            var result = new Operations.RulesAttached();
            var accepted = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                var reason = Validate(rule);
                if (reason == Operations.RuleRefusalReason.Unspecified && !seen.Add(rule.Id)) reason = Operations.RuleRefusalReason.DuplicateId;
                if (reason != Operations.RuleRefusalReason.Unspecified)
                {
                    var refusal = new Operations.RuleRefusal { Reason = reason };
                    if (rule != null && rule.HasId) refusal.RuleId = rule.Id;
                    result.Refused.Add(refusal);
                    continue;
                }
                accepted.Add(new Entry { Rule = rule.Clone() });
                result.AcceptedIds.Add(rule.Id);
            }
            active.Clear();
            active.AddRange(accepted);
            expiresAtTick = expiresAt;
            leaseExpired = false;
            return result;
        }

        // Returns how many rules were active.
        internal int Clear()
        {
            var cleared = active.Count;
            active.Clear();
            expiresAtTick = 0;
            leaseExpired = false;
            return cleared;
        }

        // Deactivates every rule once the lease tick has passed. True exactly once per attachment.
        internal bool TryExpire(long now, out int deactivated)
        {
            deactivated = 0;
            if (active.Count == 0 || now < expiresAtTick) return false;
            deactivated = active.Count;
            active.Clear();
            leaseExpired = true;
            return true;
        }

        internal void NoteFired(Entry entry, string actorId, string targetId, long now)
        {
            entry.Fired++;
            entry.LastTick = now;
            entry.LastActor = actorId;
            entry.LastTarget = targetId;
        }

        internal Operations.RulesStatus Status(long now)
        {
            var status = new Operations.RulesStatus { LeaseExpired = leaseExpired };
            foreach (var entry in active)
            {
                var row = new Operations.RuleStatus { RuleId = entry.Rule.Id, FiringCount = entry.Fired };
                if (entry.Fired > 0) { row.LastFiredTick = entry.LastTick; row.LastActorId = entry.LastActor; row.LastTargetId = entry.LastTarget; }
                status.Rules.Add(row);
            }
            if (active.Count > 0 || leaseExpired)
            {
                status.ExpiresAtTick = expiresAtTick;
                status.LeaseRemainingTicks = active.Count == 0 ? 0 : Math.Max(0, expiresAtTick - now);
            }
            return status;
        }
    }
}
