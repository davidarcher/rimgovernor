#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Biotech colony facts (#1679): what makes and removes pollution, the mech
    // gestators and chargers that produce wastepacks, and baby care. Every
    // verdict is the game's own (comps, buildings, ChildcareUtility); nothing
    // is a def-name list. The section is absent without Biotech.
    internal static class NativeBiotechColony
    {
        private static readonly System.Reflection.FieldInfo? Disabled = BridgeCommon.PrivateInstanceField(typeof(CompPollutionPump), "disabledByArtificialBuildings");
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static float? Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? (float?)null : value;
        private static bool Owned(Thing t, Map map) => t.Spawned && t.Map == map && t.Faction == Faction.OfPlayer;

        internal static Obs.BiotechSection? Read(Map map)
        {
            if (!ModsConfig.BiotechActive) return null;
            try {
                var facts = new Obs.BiotechColonyFacts();
                var grid = map.pollutionGrid;
                var totals = new Obs.PollutionTotals { TotalPollution = grid.TotalPollution, PollutableCells = (uint)grid.AllPollutableCells.Count };
                var clear = map.areaManager.PollutionClear;
                if (clear != null) { totals.ClearAreaId = Id(clear.GetUniqueLoadID()); totals.ClearAreaCells = clear.TrueCount; }
                uint polluted = 0, uncovered = 0;
                foreach (var cell in grid.AllPollutableCells) {
                    if (!grid.IsPolluted(cell)) continue;
                    polluted++;
                    if (clear == null || !clear[cell]) uncovered++;
                }
                totals.PollutedCells = polluted; totals.PollutedUncoveredCells = uncovered;
                facts.Pollution = totals;

                var buildings = map.listerBuildings.allBuildingsColonist.Where(b => Owned(b, map)).OrderBy(b => b.thingIDNumber).ToList();
                foreach (var b in buildings) {
                    var toxifier = b.TryGetComp<CompToxifier>();
                    var over = b.TryGetComp<CompPolluteOverTime>();
                    if (toxifier != null || over != null) {
                        var row = new Obs.Polluter { ThingId = Id(b.GetUniqueLoadID()), DefName = Id(b.def.defName), Position = Cell(b.Position) };
                        if (toxifier != null) row.Polluting = toxifier.CanPolluteNow;
                        if (over?.props is CompProperties_PolluteOverTime props) row.CellsPerDay = props.cellsToPollutePerDay;
                        facts.Polluters.Add(row);
                    }
                    if (b is Building_WastepackAtomizer atomizer) {
                        var comp = atomizer.Atomizer;
                        var row = new Obs.WastepackAtomizer { ThingId = Id(b.GetUniqueLoadID()), DefName = Id(b.def.defName), Position = Cell(b.Position),
                            Powered = b.GetComp<CompPowerTrader>()?.PowerOn ?? false, SpaceLeft = comp.SpaceLeft, AutoLoad = comp.AutoLoad, TicksLeft = comp.TicksLeftUntilAllAtomized };
                        if (Finite(comp.FillPercent) is float fill) row.FillPercent = fill;
                        facts.Atomizers.Add(row);
                    }
                    var pump = b.TryGetComp<CompPollutionPump>();
                    if (pump != null) {
                        var row = new Obs.PollutionPump { ThingId = Id(b.GetUniqueLoadID()), DefName = Id(b.def.defName), Position = Cell(b.Position),
                            Powered = b.GetComp<CompPowerTrader>()?.PowerOn ?? false };
                        if (Disabled?.GetValue(pump) is bool disabled) row.DisabledByArtificialBuildings = disabled;
                        facts.Pumps.Add(row);
                    }
                    if (b is Building_MechGestator gestator) facts.Gestators.Add(Gestator(gestator));
                    if (b is Building_MechCharger charger) facts.Chargers.Add(Charger(charger));
                }

                foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).OrderBy(t => t.thingIDNumber)) {
                    var dissolution = (t as ThingWithComps)?.GetComp<CompDissolution>();
                    if (dissolution == null || !t.Spawned) continue;
                    facts.Wastepacks.Add(new Obs.Wastepack { ThingId = Id(t.GetUniqueLoadID()), DefName = Id(t.def.defName), Position = Cell(t.Position), Count = t.stackCount,
                        Frozen = dissolution.IsFrozen, Outdoors = dissolution.IsOutdoors, InAtomizer = dissolution.InAtomizer, CanDissolveNow = dissolution.CanDissolveNow,
                        DeteriorationRate = dissolution.DeterioarationRate, Forbidden = t.IsForbidden(Faction.OfPlayer) });
                }

                foreach (var pawn in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike
                    && (p.DevelopmentalStage.Baby() || p.DevelopmentalStage.Newborn())).OrderBy(p => p.thingIDNumber))
                    facts.Babies.Add(Baby(pawn));
                facts.Breastfeeders.Add(ChildcareUtility.CanBreastfeedPlayerPawns.Where(p => p.Spawned && p.Map == map)
                    .Select(p => Id(p.GetUniqueLoadID())).Where(i => i != null).Select(i => i!).OrderBy(i => i, StringComparer.Ordinal));
                return new Obs.BiotechSection { Observed = facts };
            } catch (Exception) {
                return new Obs.BiotechSection { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = "Biotech colony facts unavailable." } };
            }
        }

        private static Obs.MechGestatorState Gestator(Building_MechGestator g)
        {
            var row = new Obs.MechGestatorState { ThingId = Id(g.GetUniqueLoadID()), DefName = Id(g.def.defName), Position = Cell(g.Position), Powered = g.PoweredOn,
                WasteCount = g.WasteProducer?.Waste?.stackCount ?? 0 };
            var bill = g.ActiveMechBill;
            if (bill != null) {
                row.BillId = Id(bill.GetUniqueLoadID());
                row.State = bill.State.ToString();
                row.CyclesCompleted = bill.GestationCyclesCompleted;
                if (Finite(bill.BandwidthCost) is float cost) row.BandwidthCost = cost;
                if (Finite(g.CurrentBillFormingPercent) is float percent) row.FormingPercent = percent;
                var mech = bill.BoundPawn;
                if (mech != null) { row.BoundPawnId = Id(mech.GetUniqueLoadID()); row.MechKind = Id(mech.kindDef?.defName); }
            }
            return row;
        }

        private static Obs.MechChargerState Charger(Building_MechCharger c)
        {
            var row = new Obs.MechChargerState { ThingId = Id(c.GetUniqueLoadID()), DefName = Id(c.def.defName), Position = Cell(c.Position), Powered = c.IsPowered,
                WasteCount = c.GetComp<CompWasteProducer>()?.Waste?.stackCount ?? 0, FullOfWaste = c.IsFullOfWaste };
            if (c.CurrentlyChargingMech != null) row.ChargingMechId = Id(c.CurrentlyChargingMech.GetUniqueLoadID());
            return row;
        }

        private static Obs.BabyCare Baby(Pawn baby)
        {
            var row = new Obs.BabyCare { PawnId = Id(baby.GetUniqueLoadID()), InBed = baby.CurrentBed() != null,
                WantsSuckle = ChildcareUtility.WantsSuckle(baby, out _), CanSuckleNow = ChildcareUtility.CanSuckleNow(baby, out _),
                BeingPlayedWith = ChildcareUtility.BabyBeingPlayedWith(baby) };
            foreach (var feeder in (baby.mindState?.Autofeeders() ?? Enumerable.Empty<Pawn>()).OrderBy(p => p.thingIDNumber))
                row.Autofeeders.Add(new Obs.BabyAutofeeder { PawnId = Id(feeder.GetUniqueLoadID()), Mode = baby.mindState!.AutofeedSetting(feeder).ToString() });
            return row;
        }
    }
}
