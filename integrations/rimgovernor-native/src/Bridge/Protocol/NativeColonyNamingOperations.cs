#nullable enable
using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // NamingIntent (#941): the colony-wide, one-shot autopilot confirmation
    // of the initial naming dialog, through ColonyNamingTools' dialog lookup
    // and the native name validators/callbacks. It is independent of
    // PlayerPresentation.Apply's own confirm_colony_names branch, which stays
    // explicit-player-only. With the dialog gone and the names holding, the
    // intent applies again as it stands.
    internal sealed class NamingActionHandler : IActionHandler
    {
        private static string? Field(Dialog_GiveName dialog, string name) =>
            (AccessTools.Field(typeof(Dialog_GiveName), name)?.GetValue(dialog) as string)?.Trim();

        private static bool Holds(Operations.NamingIntent c) =>
            ColonyNamingTools.Pending() == null && Faction.OfPlayer?.Name == c.FactionName
            && Find.WorldObjects.Settlements.Exists(v => v.Faction == Faction.OfPlayer && v.Name == c.SettlementName);

        // Resolves the pending dialog the intent names; a null dialog with no
        // failure means the names already hold.
        private static Common.Failure? Resolve(Operations.NamingIntent? c, out Dialog_NamePlayerFactionAndSettlement? dialog, out Settlement? settlement)
        {
            dialog = null; settlement = null;
            if (c == null || !c.HasWindowId || !c.HasFactionName || !c.HasSettlementName || c.FactionName.Length == 0 || c.SettlementName.Length == 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Naming requires a window and both suggestions.");
            if (Holds(c)) return null;
            var pending = ColonyNamingTools.Pending();
            if (pending == null || pending.ID != c.WindowId || Field(pending, "curName") != c.FactionName || Field(pending, "curSecondName") != c.SettlementName)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Naming window or suggestions changed; inspect again.");
            var target = AccessTools.Field(typeof(Dialog_NamePlayerFactionAndSettlement), "settlement")?.GetValue(pending) as Settlement;
            if (target == null || !ProtoBoundary.IsLoaded(target.Map))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The naming dialog's settlement is not loaded.");
            var type = typeof(Dialog_NamePlayerFactionAndSettlement);
            if (!(bool)AccessTools.Method(type, "IsValidName").Invoke(pending, new object[] { c.FactionName })
                || !(bool)AccessTools.Method(type, "IsValidSecondName").Invoke(pending, new object[] { c.SettlementName }))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native naming validation refused the suggestions.");
            dialog = pending; settlement = target;
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Naming, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var c = action.Naming;
            var failure = Resolve(c, out var dialog, out var settlement);
            if (failure != null) throw new InvalidOperationException("Naming prerequisites changed before apply: " + failure.Detail);
            if (dialog != null && settlement != null)
            {
                var type = typeof(Dialog_NamePlayerFactionAndSettlement);
                AccessTools.Method(type, "Named").Invoke(dialog, new object[] { c.FactionName });
                AccessTools.Method(type, "NamedSecond").Invoke(dialog, new object[] { c.SettlementName });
                Messages.Message("PlayerFactionAndBaseGainsName".Translate(c.FactionName, c.SettlementName), MessageTypeDefOf.TaskCompletion, historical: false);
                Find.WindowStack.TryRemove(dialog);
                if (Faction.OfPlayer.Name != c.FactionName || settlement.Name != c.SettlementName || Find.WindowStack.Windows.Contains(dialog))
                    throw new InvalidOperationException("Naming confirmation did not verify after native callbacks.");
            }
            return new Receipts.EffectEvidence { Naming = new Receipts.NamingEffect {
                WindowId = c.WindowId, FactionName = Faction.OfPlayer.Name, SettlementName = c.SettlementName, Confirmed = true } };
        }
    }
}
