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
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // The title ladder and permit catalog are def-mirror rows (#1875) and the
    // colony facts are ColonyFactsSnapshot.royalty (#1877); the royalty-specific
    // read tool is gone (#1879). What remains is each pawn row's royalty block.
    public static class NativePawnRoyalty
    {
        // A free colonist's royalty facts on its pawn row (#1876): holdings per
        // faction with each held permit's cooldown, the known psycasts and the
        // caster's psyfocus and neural heat (combat casts hold on them, #1611).
        // Absent without Royalty or a royalty tracker; a failed read leaves the
        // block absent next to a ReadIssue named "royalty".
        internal static void Apply(Pawn pawn, Obs.PawnState row)
        {
            if (!ModsConfig.RoyaltyActive || !pawn.IsFreeColonist || pawn.royalty == null) return;
            try { row.Royalty = Pawn(pawn); }
            catch (Exception ex)
            {
                row.Issues.Add(new Obs.ReadIssue { Field = "royalty", Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } });
            }
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
                if (title != null) holding.Title = title.defName;
                holding.Permits.AddRange(royalty.AllFactionPermits.Where(p => p.Faction == faction && p.Permit != null)
                    .Select(p => p.Permit.defName).OrderBy(n => n, StringComparer.Ordinal));
                // The native cooldown of each held permit (#1607): FactionPermit.LastUsedTick
                // (-1 until first used) and the ticks left of the permit's cooldown.
                var now = Find.TickManager.TicksGame;
                foreach (var held in royalty.AllFactionPermits.Where(p => p.Faction == faction && p.Permit != null && ProtoBoundary.IsIdentifier(p.Permit.defName))
                    .OrderBy(p => p.Permit.defName, StringComparer.Ordinal))
                {
                    var cooldown = new Obs.PermitCooldown { Permit = held.Permit.defName, CooldownRemainingTicks = held.OnCooldown ? Math.Max(0, held.LastUsedTick + held.Permit.CooldownTicks - now) : 0 };
                    if (held.LastUsedTick >= 0) cooldown.LastUsedTick = held.LastUsedTick;
                    holding.PermitCooldowns.Add(cooldown);
                }
                row.Holdings.Add(holding);
            }
            if (pawn.abilities != null)
                foreach (var ability in pawn.abilities.abilities.Where(a => a.def != null && a.def.IsPsycast && ProtoBoundary.IsIdentifier(a.def.defName))
                    .OrderBy(a => a.def.level).ThenBy(a => a.def.defName, StringComparer.Ordinal))
                    row.Psycasts.Add(Psycast(ability.def, ability.CooldownTicksRemaining));
            var tracker = pawn.psychicEntropy;
            if (tracker != null && tracker.Psylink != null)
            {
                if (tracker.NeedsPsyfocus) row.Psyfocus = tracker.CurrentPsyfocus;
                row.Entropy = tracker.EntropyValue;
                row.EntropyMax = tracker.MaxEntropy;
            }
            return row;
        }

        private static Obs.PawnPsycast Psycast(AbilityDef def, int cooldownRemaining)
        {
            var cast = new Obs.PawnPsycast { DefName = def.defName, Level = def.level, PsyfocusCost = def.PsyfocusCost, Entropy = def.EntropyGain, CooldownTicks = def.cooldownTicksRange.max, CooldownRemainingTicks = Math.Max(0, cooldownRemaining) };
            var target = def.verbProperties?.targetParams;
            if (target == null) return cast;
            cast.TargetKind = target.canTargetSelf && !target.canTargetPawns && !target.canTargetLocations && !target.canTargetBuildings && !target.canTargetItems ? Obs.PsycastTargetKind.Self
                : target.canTargetPawns ? Obs.PsycastTargetKind.Pawn
                : target.canTargetBuildings || target.canTargetItems ? Obs.PsycastTargetKind.Thing
                : target.canTargetLocations ? Obs.PsycastTargetKind.Cell
                : Obs.PsycastTargetKind.Unspecified;
            return cast;
        }
    }
}
