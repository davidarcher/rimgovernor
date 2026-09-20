#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeApparelPolicyOperations
    {
        private static string Hash(string s) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", ""); }
        internal static string Signature(ApparelPolicy p) => Hash(p.label + "|" + string.Join(",", p.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d))
            + "|" + string.Join(",", DefDatabase<SpecialThingFilterDef>.AllDefs.Where(d => !p.filter.Allows(d)).Select(d => d.defName).OrderBy(d => d))
            + "|" + p.filter.AllowedHitPointsPercents.min.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "|" + p.filter.AllowedHitPointsPercents.max.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + p.filter.AllowedQualityLevels);
        internal static string Token(Pawn p) => Hash(GearUpkeepTools.Identity(p) + "|" + string.Join(";",
            Current.Game.outfitDatabase.AllOutfits.OrderBy(o => o.id).Select(o => o.GetUniqueLoadID() + ":" + Signature(o))));
        internal static Obs.ApparelPolicyState Read(Pawn p)
        {
            var outfit = p.outfits?.CurrentApparelPolicy;
            var row = new Obs.ApparelPolicyState { Token = Token(p), Child = p.DevelopmentalStage.Child(), Slave = p.IsSlaveOfColony,
                IncapableOfViolence = p.WorkTagIsDisabled(WorkTags.Violent) };
            if (outfit != null) {
                row.Name = outfit.label; row.AllowedDefs.Add(outfit.filter.AllowedThingDefs.Where(d => d.IsApparel).Select(d => d.defName).OrderBy(d => d));
                row.MinHitPoints = outfit.filter.AllowedHitPointsPercents.min; row.MaxHitPoints = outfit.filter.AllowedHitPointsPercents.max;
                row.MinQuality = (int)outfit.filter.AllowedQualityLevels.min; row.MaxQuality = (int)outfit.filter.AllowedQualityLevels.max;
                row.ExcludesTainted = !outfit.filter.Allows(SpecialThingFilterDefOf.AllowDeadmansApparel) && outfit.filter.Allows(SpecialThingFilterDefOf.AllowNonDeadmansApparel);
            }
            foreach (var d in DefDatabase<ThingDef>.AllDefs.Where(d => d.IsApparel).OrderBy(d => d.defName))
                row.Definitions.Add(new Obs.ApparelPolicyDefinition { DefName = d.defName,
                    Armor = d.apparel.defaultOutfitTags.NotNullAndContains("Soldier") && !d.apparel.defaultOutfitTags.NotNullAndContains("Worker"),
                    Child = d.apparel.developmentalStageFilter.Has(DevelopmentalStage.Child), Adult = d.apparel.developmentalStageFilter.Has(DevelopmentalStage.Adult) });
            row.Drafted = p.Drafted;
            if (p.workSettings != null) foreach (var d in DefDatabase<WorkTypeDef>.AllDefs) row.Work.Add(new Obs.WorkSetting { DefName = d.defName, Priority = p.workSettings.GetPriority(d), Disabled = p.WorkTypeIsDisabled(d) });
            return row;
        }
        private static bool Prepare(Operations.SetApparelPolicy c, Common.ObservationContext context, out Pawn p)
        {
            p = null!;
            if (c?.Pawn == null || !c.HasName || !c.Name.StartsWith("RimGovernor ", StringComparison.Ordinal) || c.Name.Length > 80
                || !c.HasMinHitPoints || !c.HasMaxHitPoints || float.IsNaN(c.MinHitPoints) || float.IsNaN(c.MaxHitPoints)
                || c.MinHitPoints < 0 || c.MaxHitPoints > 1 || c.MinHitPoints > c.MaxHitPoints
                || !c.HasMinQuality || !c.HasMaxQuality || c.MinQuality < 0 || c.MaxQuality > 6 || c.MinQuality > c.MaxQuality
                || c.AllowedDefs.Count == 0 || c.AllowedDefs.Count > 512 || c.AllowedDefs.Distinct().Count() != c.AllowedDefs.Count
                || c.AllowedDefs.Any(d => DefDatabase<ThingDef>.GetNamedSilentFail(d)?.IsApparel != true)) return false;
            p = ProtoBoundary.LoadedMap(context)?.mapPawns.FreeColonistsSpawned.FirstOrDefault(v => v.GetUniqueLoadID() == c.Pawn.EntityId)!;
            if (p == null || GearUpkeepTools.Available(p) != null || c.Pawn.ExpectedSnapshotToken != Token(p)) return false;
            var sameName = Current.Game.outfitDatabase.AllOutfits.Where(v => v.label == c.Name).ToList();
            return sameName.Count <= 1;
        }
        internal static Operations.PreviewReply Preview(Operations.SetApparelPolicy c, Common.ObservationContext context) =>
            NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                Context = context.Clone(), Accepted = Prepare(c, context, out _), Reason = "Apparel policy definition and CAS checks." } });
        private static bool Matches(Pawn p, Operations.SetApparelPolicy c)
        {
            var v = p.outfits?.CurrentApparelPolicy;
            return v != null && p.outfits != null && p.outfits.forcedHandler.ForcedApparel.Count == 0 && !p.apparel.AnyApparelLocked && v.label == c.Name && v.filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d).SequenceEqual(c.AllowedDefs.OrderBy(d => d))
                && v.filter.AllowedHitPointsPercents.min == c.MinHitPoints && v.filter.AllowedHitPointsPercents.max == c.MaxHitPoints
                && (int)v.filter.AllowedQualityLevels.min == c.MinQuality && (int)v.filter.AllowedQualityLevels.max == c.MaxQuality
                && !v.filter.Allows(SpecialThingFilterDefOf.AllowDeadmansApparel) && v.filter.Allows(SpecialThingFilterDefOf.AllowNonDeadmansApparel);
        }
        private static Receipts.EffectEvidence Evidence(Pawn p, Operations.SetApparelPolicy c) => new Receipts.EffectEvidence {
            Settings = new Receipts.SettingsEffect { Snapshot = new Receipts.SnapshotEvidence { EntityId = p.GetUniqueLoadID(), BeforeToken = c.Pawn.ExpectedSnapshotToken, AfterToken = Token(p) } } };
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var c = request.Operation.SetApparelPolicy; var pre = request.Precondition;
            try {
                if (!Prepare(c, context, out var p)) return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Apparel policy is stale or invalid.") };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration); context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle; state.ApparelPolicies.Add(pre.Attempt.Clone(), c.Clone());
                using (authority.Owned()) {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(c, context, out p)) throw new InvalidOperationException("Apparel policy admission changed.");
                    {
                        var outfit = Current.Game.outfitDatabase.AllOutfits.FirstOrDefault(v => v.label == c.Name) ?? Current.Game.outfitDatabase.MakeNewOutfit();
                        outfit.label = c.Name; outfit.filter.SetDisallowAll();
                        foreach (var d in c.AllowedDefs) outfit.filter.SetAllow(DefDatabase<ThingDef>.GetNamed(d), true);
                        outfit.filter.AllowedHitPointsPercents = new FloatRange(c.MinHitPoints, c.MaxHitPoints);
                        outfit.filter.AllowedQualityLevels = new QualityRange((QualityCategory)c.MinQuality, (QualityCategory)c.MaxQuality);
                        outfit.filter.SetAllow(SpecialThingFilterDefOf.AllowNonDeadmansApparel, true);
                        outfit.filter.SetAllow(SpecialThingFilterDefOf.AllowDeadmansApparel, false);
                        p.outfits.CurrentApparelPolicy = outfit;
                        p.outfits.forcedHandler.Reset(); p.apparel.UnlockAll();
                        foreach (var pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive.Where(v => v.outfits?.CurrentApparelPolicy == outfit)) pawn.mindState?.Notify_OutfitChanged();
                    }
                    evidence = Evidence(p, c); if (!Matches(p, c)) throw new InvalidOperationException("Apparel filter readback differs.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            } catch (Exception e) {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Apparel admission failed: " + e.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Observe apparel policy: " + e.GetType().Name) };
            }
        }
        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Operations.SetApparelPolicy c)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false, Unknown = new Receipts.UnknownEffect { Reason = "Pawn apparel policy unavailable." } };
            var p = ProtoBoundary.LoadedMap(context)?.mapPawns.FreeColonistsSpawned.FirstOrDefault(v => v.GetUniqueLoadID() == c.Pawn.EntityId);
            if (p == null) return result;
            result.CompleteInspection = true; var evidence = Evidence(p,c);
            if (Matches(p,c)) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Evidence = evidence, Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Detail = "Apparel assignment or filter changed." };
            return result;
        }
    }
}
