using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimGovernor.Native
{
    // The title ladder and permit catalog are def-mirror rows and the
    // colony facts are ColonyFactsSnapshot.royalty; the royalty-specific
    // read tool is gone. What remains is each pawn row's royalty block.
    public static class NativePawnRoyalty
    {
        // A pawn's royalty facts on its pawn row: holdings per faction with
        // each held permit's cooldown, the known psycasts and the caster's
        // psyfocus and neural heat (combat casts hold on them). Inactive
        // without Royalty; no tracker for a pawn the game gives none.
        internal static RimGovernor.Brain.GameFacts.Observations.PawnRoyaltyState State(Pawn pawn)
        {
            if (!ModsConfig.RoyaltyActive) return new RimGovernor.Brain.GameFacts.Observations.PawnRoyaltyState.Inactive();
            if (pawn.royalty == null) return new RimGovernor.Brain.GameFacts.Observations.PawnRoyaltyState.NoTracker();
            return new RimGovernor.Brain.GameFacts.Observations.PawnRoyaltyState.Active(Pawn(pawn));
        }
        private static RimGovernor.Brain.GameFacts.Observations.PawnRoyalty Pawn(Pawn pawn)
        {
            var royalty = pawn.royalty;
            var factions = royalty.AllFactionPermits.Select(p => p.Faction)
                .Concat(royalty.AllTitlesForReading.Select(t => t.faction))
                .Where(f => f?.def != null && GameThread.IsIdentifier(f.def.defName)).Distinct()
                .OrderBy(f => f.def.defName, StringComparer.Ordinal).ToList();
            var now = Find.TickManager.TicksGame;
            var tracker = pawn.psychicEntropy;
            return new RimGovernor.Brain.GameFacts.Observations.PawnRoyalty
            {
                Holdings = [.. factions.Select(faction => Holding(royalty, faction, now))],
                Psycasts = [.. (pawn.abilities?.abilities ?? new List<Ability>()).Where(a => a.def != null && a.def.IsPsycast && GameThread.IsIdentifier(a.def.defName))
                    .OrderBy(a => a.def.level).ThenBy(a => a.def.defName, StringComparer.Ordinal)
                    .Select(a => Psycast(a.def, a.CooldownTicksRemaining))],
                Psylink = tracker?.Psylink == null ? null : new RimGovernor.Brain.GameFacts.Observations.PawnPsylink
                {
                    Psyfocus = tracker.CurrentPsyfocus,
                    Entropy = tracker.EntropyValue,
                    EntropyMax = tracker.MaxEntropy,
                },
            };
        }

        // One faction's standing: its current title, favor, permit points, the held permits and each held permit's
        // cooldown, the ticks left of the permit's cooldown.
        private static RimGovernor.Brain.GameFacts.Observations.PawnRoyalHolding Holding(Pawn_RoyaltyTracker royalty, Faction faction, int now)
        {
            var held = royalty.AllFactionPermits.Where(p => p.Faction == faction && p.Permit != null).ToList();
            return new RimGovernor.Brain.GameFacts.Observations.PawnRoyalHolding
            {
                FactionDef = faction.def.defName,
                Title = royalty.GetCurrentTitle(faction)?.defName,
                Favor = royalty.GetFavor(faction),
                PermitPoints = royalty.GetPermitPoints(faction),
                Permits = [.. held.Select(p => p.Permit.defName).OrderBy(n => n, StringComparer.Ordinal)],
                PermitCooldowns = [.. held.Where(p => GameThread.IsIdentifier(p.Permit.defName)).OrderBy(p => p.Permit.defName, StringComparer.Ordinal)
                    .Select(p => new RimGovernor.Brain.GameFacts.Observations.PermitCooldown { Permit = p.Permit.defName, CooldownRemainingTicks = p.OnCooldown ? Math.Max(0, p.LastUsedTick + p.Permit.CooldownTicks - now) : 0 })],
            };
        }

        // The ability's level, costs, cooldown and target kind are its AbilityDef row;
        // only the cooldown left is live.
        private static RimGovernor.Brain.GameFacts.Observations.PawnPsycast Psycast(AbilityDef def, int cooldownRemaining) =>
            new RimGovernor.Brain.GameFacts.Observations.PawnPsycast { DefName = def.defName, CooldownRemainingTicks = Math.Max(0, cooldownRemaining) };
    }
}
