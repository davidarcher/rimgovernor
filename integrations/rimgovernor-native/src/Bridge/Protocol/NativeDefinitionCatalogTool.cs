using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Native
{
    // The definition catalog: the static planning facts of every
    // buildable or sowable definition and every research project. They hold
    // for the whole load, so the controller reads them once per load token
    // and frames carry only per-map, per-tick values.
    internal static class NativeDefinitionCatalogTool
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        // A refused or unavailable prepare throws (NativeFaults).
        internal static RimGovernor.Brain.GameFacts.Observations.DefinitionCatalogAck HandleReadDefinitionCatalog(RimGovernor.Brain.GameOrders.Observations.DefinitionCatalogRequest parsed)
        {
            if (parsed.Scope.ExpectedIdentity == null)
                throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Expected identity required."));
            if (!GameThread.ValidateIdentity(parsed.Scope.ExpectedIdentity, out _, out var context, out var error))
                throw NativeFaults.Refused(error);
            var player = Faction.OfPlayerSilentFail;
            if (player?.def == null)
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NativeComponentMissing, Detail = "Player faction is unavailable." });
            try
            {
                // The brain builds its catalog over the load's def stat table right after this read, off the game thread.
                NativeStatTable.EnsureForLoad(context.Identity.LoadToken);
                NativeRaceFacts.EnsureForLoad(context.Identity.LoadToken);
                return new RimGovernor.Brain.GameFacts.Observations.DefinitionCatalogAck();
            }
            catch (Exception ex)
            {
                ObservationWork.Failed("definitionCatalog", ex);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic("The definition catalog could not be read completely: " + ex.GetType().Name + ": " + ex.Message) });
            }
        }

        // The reply carries no def rows or game constants (#3100), only the load it prepared: the in-process brain builds its catalog over the
        // game's Def objects (NativeCatalogFactory), the race facts are published beside the stat table
        // (NativeRaceFacts) and the derived facts are computed in process (BrainDerived).
        // The brain catalog's derived facts: the def names the game's code names (read straight from ThingDefOf, so
        // they ride no wire field) and the full rot rate the game computes in code.
        internal static RimGovernor.Brain.Bridge.CatalogDerived BrainDerived() => new(
            // Tradeable.IsCurrency is "def == ThingDefOf.Silver".
            ThingDefOf.Silver?.defName ?? throw new InvalidOperationException("ThingDefOf.Silver is not loaded."),
            // Building_FermentingBarrel adds ThingDefOf.Wort and takes out ThingDefOf.Beer.
            ThingDefOf.Wort?.defName ?? throw new InvalidOperationException("ThingDefOf.Wort is not loaded."),
            FullRotRateC());

        // The game keeps its rot curve as literals inside
        // GenTemperature.RotRateAtTemperature, so evaluate that function: bisect the
        // ordered bit patterns of the non-negative floats for the first temperature
        // whose rate reaches 1. It is exact for any non-decreasing curve, and fails
        // naming the function when the rate never reaches 1 or is not a number.
        private static float FullRotRateC()
        {
            const string member = "GenTemperature.RotRateAtTemperature";
            float Rate(float temperature)
            {
                var rate = GenTemperature.RotRateAtTemperature(temperature);
                if (float.IsNaN(rate) || float.IsInfinity(rate))
                    throw new InvalidOperationException($"{member}({temperature}) is {rate}.");
                return rate;
            }
            const float top = 1000f;
            if (Rate(0f) >= 1f)
                throw new InvalidOperationException($"{member} is already at its full rate at 0 C.");
            if (Rate(top) < 1f)
                throw new InvalidOperationException($"{member} never reaches a rate of 1 up to {top} C.");
            var below = BitConverter.ToInt32(BitConverter.GetBytes(0f), 0);
            var at = BitConverter.ToInt32(BitConverter.GetBytes(top), 0);
            while (at - below > 1)
            {
                var middle = below + (at - below) / 2;
                if (Rate(BitConverter.ToSingle(BitConverter.GetBytes(middle), 0)) >= 1f) at = middle; else below = middle;
            }
            return BitConverter.ToSingle(BitConverter.GetBytes(at), 0);
        }
    }
}
