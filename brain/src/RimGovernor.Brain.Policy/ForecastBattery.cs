namespace RimGovernor.Brain.Policy;

// The battery room credit (#3814, epic #3650). Confirmed with ilspycmd on Assembly-CSharp.dll:
// - Rain: SteadyEnvironmentEffects.RollForRainFire shorts a random colonist ElecFire building only when
//   !roofGrid.Roofed(building.Position) (ShortCircuitUtility.TryShortCircuitInRain: a battery over 100 Wd sets off a
//   flame explosion). Only the roof matters, and every battery the colony builds is roofed without this room: the store
//   method places one on a roofed cell (the room's slots, else an indoor search) and shelter_power encloses an exposed
//   one where it stands. Nothing moves a standing battery into the room, so the room changes no battery's rain exposure.
// - Zzztt (IncidentWorker_ShortCircuit): the explosion is at a random conduit and sized by the net's stored energy, wherever
//   the batteries stand.
// - Enclosure: a battery set alight explodes again (Building_Battery.PostApplyDamage, flame damage over 500 Wd), and a
//   stone room keeps that fire from other rooms' things. That is fire harm, which the harm term does not model yet
//   (HarmLedger's fire_unassessed).
// So the room earns zero: priced, it leaves the development count (the issue's no-mechanic branch).
public static class ForecastBattery
{
    /// <summary>The credit of a planned battery room (#3814): nothing, since no modelled consequence changes with it (above).</summary>
    internal static IReadOnlyList<RoomGain> BatteryCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon) => [];
}
