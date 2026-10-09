#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    // Declarative rules Go attaches (operations.proto Rule), executed in process
    // with no Go round trip. v1 is one shape: when a player pawn kills a wild
    // animal, the killer takes a Hunt job on the nearest designated prey, built
    // by the path GiveJobIntent's prioritized arm uses. The kill hook is the
    // delivery ledger's; the firing runs at the end of the next tick, never
    // inside the kill. A rule fires only while native holds Auto authority, at
    // most once per actor per 60 ticks, and its firing is journaled on the
    // clock event ring before the write. Rules live in memory per loaded game.
    internal static class NativeRuleRuntime
    {
        private const string Owner = "rimgovernor.native.rules";
        // Route checks are pathfinding; the nearest few candidates bound the cost of one firing.
        private const int MaxRouteChecks = 8;

        private sealed class Kill { internal Pawn Actor = null!, Prey = null!; }
        private sealed class State
        {
            internal readonly NativeRuleBook Book = new NativeRuleBook();
            internal readonly List<Kill> Pending = new List<Kill>();
        }

        private static readonly ConditionalWeakTable<Game, State> States = new ConditionalWeakTable<Game, State>();
        private static bool tickInstalled;

        // Installs the shared kill hook and the end-of-tick drain; false when either is unavailable.
        internal static bool EnsureHooks()
        {
            if (!NativeDeliveryLedger.EnsureInstalled()) return false;
            if (tickInstalled) return true;
            try
            {
                new Harmony(Owner).Patch(AccessTools.Method(typeof(TickManager), "DoSingleTick") ?? throw new MissingMethodException("TickManager.DoSingleTick"),
                    postfix: new HarmonyMethod(typeof(NativeRuleRuntime), nameof(AfterTick)));
                tickInstalled = true;
            }
            catch (Exception error) { ModLog.Error("rules", "Native rule hooks unavailable: " + error); }
            return tickInstalled;
        }

        internal static Operations.RulesAttached Attach(Common.ObservationContext context, IEnumerable<Operations.Rule> rules, long expiresAt)
        {
            var state = States.GetOrCreateValue(Current.Game);
            state.Pending.Clear();
            var attached = state.Book.Attach(rules, expiresAt);
            attached.Context = context.Clone();
            return attached;
        }

        internal static uint Clear(Game game)
        {
            if (!States.TryGetValue(game, out var state)) return 0;
            state.Pending.Clear();
            return (uint)state.Book.Clear();
        }

        internal static Operations.RulesStatus Status(Common.ObservationContext context, Game game)
        {
            var status = States.TryGetValue(game, out var state) ? state.Book.Status(context.Tick) : new Operations.RulesStatus();
            status.Context = context.Clone();
            return status;
        }

        // The PREY_KILLED trigger: called from the ledger's Pawn.Kill postfix for a wild animal a
        // player pawn killed. Cheap while no rule is attached.
        internal static void OnPreyKilled(Pawn? actor, Pawn prey)
        {
            try
            {
                if (actor == null || Current.Game == null || !States.TryGetValue(Current.Game, out var state) || state.Book.Active.Count == 0) return;
                state.Pending.Add(new Kill { Actor = actor, Prey = prey });
            }
            catch (Exception error) { ModLog.Error("rules", "Rule trigger failed: " + error); }
        }

        private static void AfterTick()
        {
            try
            {
                var game = Current.Game;
                if (game == null || Find.TickManager == null || !States.TryGetValue(game, out var state)) return;
                var now = Find.TickManager.TicksGame;
                var expiresAt = state.Book.ExpiresAtTick;
                if (state.Book.TryExpire(now, out var deactivated))
                {
                    state.Pending.Clear();
                    Supervisor.PublishRuleEvent("rule_lease_expired", "Native rules deactivated at the lease tick.",
                        new Dictionary<string, object?> { { "expiresAtTick", expiresAt }, { "deactivated", (long)deactivated } });
                    return;
                }
                if (state.Pending.Count == 0) return;
                var pending = state.Pending.ToList();
                state.Pending.Clear();
                if (!NativeControlAuthority.TryGetForGame(game, out var authority) || authority == null || !authority.Status().Active) return;
                foreach (var kill in pending) Evaluate(authority, state, kill, now);
            }
            catch (Exception error) { ModLog.Error("rules", "Native rule evaluation failed: " + error); }
        }

        private static void Evaluate(NativeControlAuthority authority, State state, Kill kill, int now)
        {
            var actor = kill.Actor;
            if (actor.Destroyed || !actor.Spawned || actor.Dead || actor.Downed || actor.Map == null) return;
            var actorId = actor.GetUniqueLoadID();
            foreach (var entry in state.Book.Active.ToList())
            {
                if (!state.Book.ActorReady(actorId, now)) return;
                if (!ActorQualifies(entry.Rule, actor)) continue;
                var def = DefDatabase<JobDef>.GetNamedSilentFail(entry.Rule.Job);
                var target = def == null ? null : Nearest(actor, def, entry.Rule.Radius, entry.Rule.PredatorMarginCells, kill.Prey);
                if (target == null) continue;
                Fire(authority, state, entry, actor, target, def!, now);
                return;
            }
        }

        // TARGET_AVAILABLE is the selector's own search: no target, no firing.
        private static bool ActorQualifies(Operations.Rule rule, Pawn actor)
        {
            foreach (var predicate in rule.Predicates)
            {
                if (predicate == Operations.RulePredicate.ActorUndrafted && actor.Drafted) return false;
                if (predicate == Operations.RulePredicate.ActorHuntingWorkActive
                    && (actor.workSettings == null || !actor.workSettings.WorkIsActive(WorkTypeDefOf.Hunting))) return false;
            }
            return true;
        }

        // The nearest designated wild prey in the radius that the hunter can reach by a route clear of
        // predators and that a work giver the hunter may do builds the job on.
        private static Pawn? Nearest(Pawn actor, JobDef def, uint radius, float predatorMarginCells, Pawn killed)
        {
            var limit = (long)radius * radius;
            var candidates = actor.Map.mapPawns.AllPawnsSpawned
                .Where(p => p != killed && !p.Dead && p.Faction == null && p.RaceProps.Animal && NativeHuntAcquisition.Designated(p)
                    && actor.Position.DistanceToSquared(p.Position) <= limit)
                .OrderBy(p => actor.Position.DistanceToSquared(p.Position)).ThenBy(p => p.thingIDNumber)
                .Take(MaxRouteChecks);
            return candidates.FirstOrDefault(p => HuntingSafety.RouteSafe(actor, p, predatorMarginCells) && NativePrioritizedJob.CanTake(actor, p, def));
        }

        private static void Fire(NativeControlAuthority authority, State state, NativeRuleBook.Entry entry, Pawn actor, Pawn target, JobDef def, int now)
        {
            var actorId = actor.GetUniqueLoadID();
            var targetId = target.GetUniqueLoadID();
            var rule = entry.Rule;
            // Journal first: the write happens only once its firing is on the ring.
            if (!Supervisor.PublishRuleEvent("rule_fired", "Rule " + rule.Id + ": " + rule.Job + " " + target.LabelShortCap,
                new Dictionary<string, object?> { { "ruleId", rule.Id }, { "job", rule.Job }, { "radius", (long)rule.Radius },
                    { "actorId", actorId }, { "targetId", targetId }, { "tick", (long)now } })) return;
            state.Book.NoteFired(entry, actorId, targetId, now);
            try
            {
                using (authority.Owned())
                using (OperationIntent.Scope("Rule " + rule.Id + ": " + rule.Job + " next prey"))
                    if (NativePrioritizedJob.TryTake(actor, target, def, out var reason) == null) ModLog.Warn("rules", "Rule " + rule.Id + " did not take its job: " + reason);
            }
            catch (Exception error) { ModLog.Error("rules", "Rule " + rule.Id + " write failed: " + error); }
        }
    }
}
