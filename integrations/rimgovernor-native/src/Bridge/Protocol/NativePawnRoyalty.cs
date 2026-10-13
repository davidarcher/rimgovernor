#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // The title ladder and permit catalog are def-mirror rows and the
    // colony facts are ColonyFactsSnapshot.royalty; the royalty-specific
    // read tool is gone. What remains is each pawn row's royalty block.
    public static class NativePawnRoyalty
    {
        // A free colonist's royalty facts on its pawn row: holdings per
        // faction with each held permit's cooldown, the known psycasts and the
        // caster's psyfocus and neural heat (combat casts hold on them).
        // Absent without Royalty or a royalty tracker; a failed read leaves the
        // block absent next to a ReadIssue named "royalty".
        internal static Obs.PawnState Apply(Pawn pawn, Obs.PawnState row)
        {
            if (!ModsConfig.RoyaltyActive || !pawn.IsFreeColonist || pawn.royalty == null) return row;
            try { row = row with { Royalty = Pawn(pawn) }; }
            catch (Exception ex)
            {
                row = row with { Issues = [.. row.Issues, new Obs.ReadIssue { Field = "royalty", Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } }] };
            }
            return row;
        }

        private static Obs.PawnRoyalty Pawn(Pawn pawn)
        {
            var royalty = pawn.royalty;
            var factions = royalty.AllFactionPermits.Select(p => p.Faction)
                .Concat(royalty.AllTitlesForReading.Select(t => t.faction))
                .Where(f => f?.def != null && ProtoBoundary.IsIdentifier(f.def.defName)).Distinct()
                .OrderBy(f => f.def.defName, StringComparer.Ordinal).ToList();
            var row = new Obs.PawnRoyalty();
            foreach (var faction in factions)
            {
                var holding = new Obs.PawnRoyalHolding { FactionDef = faction.def.defName, Favor = royalty.GetFavor(faction), PermitPoints = royalty.GetPermitPoints(faction) };
                var title = royalty.GetCurrentTitle(faction);
                if (title != null) holding = holding with { Title = title.defName };
                holding = holding with { Permits = [.. holding.Permits, .. royalty.AllFactionPermits.Where(p => p.Faction == faction && p.Permit != null)
                    .Select(p => p.Permit.defName).OrderBy(n => n, StringComparer.Ordinal)] };
                // The native cooldown of each held permit: FactionPermit.LastUsedTick
                // (-1 until first used) and the ticks left of the permit's cooldown.
                var now = Find.TickManager.TicksGame;
                foreach (var held in royalty.AllFactionPermits.Where(p => p.Faction == faction && p.Permit != null && ProtoBoundary.IsIdentifier(p.Permit.defName))
                    .OrderBy(p => p.Permit.defName, StringComparer.Ordinal))
                {
                    var cooldown = new Obs.PermitCooldown { Permit = held.Permit.defName, CooldownRemainingTicks = held.OnCooldown ? Math.Max(0, held.LastUsedTick + held.Permit.CooldownTicks - now) : 0 };
                    if (held.LastUsedTick >= 0) cooldown = cooldown with { LastUsedTick = held.LastUsedTick };
                    holding = holding with { PermitCooldowns = [.. holding.PermitCooldowns, cooldown] };
                }
                row = row with { Holdings = [.. row.Holdings, holding] };
            }
            if (pawn.abilities != null)
                foreach (var ability in pawn.abilities.abilities.Where(a => a.def != null && a.def.IsPsycast && ProtoBoundary.IsIdentifier(a.def.defName))
                    .OrderBy(a => a.def.level).ThenBy(a => a.def.defName, StringComparer.Ordinal))
                    row = row with { Psycasts = [.. row.Psycasts, Psycast(ability.def, ability.CooldownTicksRemaining)] };
            var tracker = pawn.psychicEntropy;
            if (tracker != null && tracker.Psylink != null)
            {
                if (tracker.NeedsPsyfocus) row = row with { Psyfocus = tracker.CurrentPsyfocus };
                row = row with { Entropy = tracker.EntropyValue };
                row = row with { EntropyMax = tracker.MaxEntropy };
            }
            return row;
        }

        // The ability's level, costs, cooldown and target kind are its AbilityDef row;
        // only the cooldown left is live.
        private static Obs.PawnPsycast Psycast(AbilityDef def, int cooldownRemaining) =>
            new Obs.PawnPsycast { DefName = def.defName, CooldownRemainingTicks = Math.Max(0, cooldownRemaining) };
    }
}
