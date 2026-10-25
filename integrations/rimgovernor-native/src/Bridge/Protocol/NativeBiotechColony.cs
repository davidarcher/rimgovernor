using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // Biotech colony facts: the polluted cells the cleanup crew does not
    // cover, the wastepacks and the atomizers, gestators and chargers around
    // them, the gene banks, assemblers and genepacks, and the babies and
    // breastfeeders. Every verdict is the game's own (comps, buildings,
    // ChildcareUtility); nothing is a def-name list. The section is inactive
    // without Biotech, and a failed read throws and fails the colony read.
    internal static class NativeBiotechColony
    {
        private static string Id(string? value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static RimGovernor.Brain.Domain.Cell Cell(IntVec3 c) => new RimGovernor.Brain.Domain.Cell(c.x, c.z);
        private static bool Owned(Thing t, Map map) => t.Spawned && t.Map == map && t.Faction == Faction.OfPlayer;

        internal static RimGovernor.Brain.GameFacts.Observations.BiotechSection Read(Map map)
        {
            if (!ModsConfig.BiotechActive) return new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Inactive();
            var buildings = map.listerBuildings.allBuildingsColonist.Where(b => Owned(b, map)).OrderBy(b => b.thingIDNumber).ToList();
            var banks = buildings.Select(b => (Building: b, Container: b.TryGetComp<CompGenepackContainer>())).Where(x => x.Container != null).ToList();
            var haulables = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).Where(t => t.Spawned).OrderBy(t => t.thingIDNumber).ToList();
            var held = banks.SelectMany(x => x.Container.ContainedGenepacks.OrderBy(p => p.thingIDNumber)
                .Select(p => new RimGovernor.Brain.GameFacts.Observations.GenepackState { BankId = Id(x.Building.GetUniqueLoadID()) }));
            var loose = haulables.OfType<Genepack>().Select(_ => new RimGovernor.Brain.GameFacts.Observations.GenepackState { BankId = null });
            return new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Active(new RimGovernor.Brain.GameFacts.Observations.BiotechColonyFacts
            {
                PollutedUncoveredCells = Uncovered(map),
                Wastepacks = [.. haulables.Select(t => (Thing: t, Dissolution: (t as ThingWithComps)?.GetComp<CompDissolution>()))
                    .Where(x => x.Dissolution != null).Select(x => new RimGovernor.Brain.GameFacts.Observations.Wastepack
                    {
                        ThingId = Id(x.Thing.GetUniqueLoadID()), DefName = x.Thing.def.defName, Position = Cell(x.Thing.Position), Count = x.Thing.stackCount,
                        Frozen = (x.Dissolution ?? throw new System.InvalidOperationException("Required native value is unavailable: x.Dissolution.")).IsFrozen, InAtomizer = x.Dissolution.InAtomizer, Forbidden = x.Thing.IsForbidden(Faction.OfPlayer),
                    })],
                Atomizers = [.. buildings.OfType<Building_WastepackAtomizer>().Select(a => new RimGovernor.Brain.GameFacts.Observations.WastepackAtomizer { Position = Cell(a.Position) })],
                Gestators = [.. buildings.OfType<Building_MechGestator>().Select(g => new RimGovernor.Brain.GameFacts.Observations.MechGestatorState
                    {
                        BillId = g.ActiveMechBill is Bill_Mech bill ? Id(bill.GetUniqueLoadID()) : null,
                        WasteCount = g.WasteProducer?.Waste?.stackCount ?? 0,
                    })],
                Chargers = [.. buildings.OfType<Building_MechCharger>().Select(c => new RimGovernor.Brain.GameFacts.Observations.MechChargerState
                    {
                        Powered = c.IsPowered, FullOfWaste = c.IsFullOfWaste,
                        ChargingMechId = c.CurrentlyChargingMech is Pawn mech ? Id(mech.GetUniqueLoadID()) : null,
                    })],
                GeneBanks = [.. banks.Select(x => new RimGovernor.Brain.GameFacts.Observations.GeneBankState
                    {
                        Position = Cell(x.Building.Position), Capacity = x.Container.Props.maxCapacity,
                        PackIds = [.. x.Container.ContainedGenepacks.Select(p => Id(p.GetUniqueLoadID())).OrderBy(i => i, StringComparer.Ordinal)],
                    })],
                GeneAssemblers = [.. buildings.OfType<Building_GeneAssembler>().Select(a => new RimGovernor.Brain.GameFacts.Observations.GeneAssemblerState { Position = Cell(a.Position) })],
                Genepacks = [.. held, .. loose],
                Babies = [.. map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike
                    && (p.DevelopmentalStage.Baby() || p.DevelopmentalStage.Newborn())).Select(p => Id(p.GetUniqueLoadID())).OrderBy(i => i, StringComparer.Ordinal)],
                Breastfeeders = [.. ChildcareUtility.CanBreastfeedPlayerPawns.Where(p => p.Spawned && p.Map == map)
                    .Select(p => Id(p.GetUniqueLoadID())).OrderBy(i => i, StringComparer.Ordinal)],
            });
        }

        // The PollutionGrid's polluted pollutable cells outside the player's
        // Area_PollutionClear (all of them while the area does not exist).
        private static uint Uncovered(Map map)
        {
            var grid = map.pollutionGrid;
            var clear = map.areaManager.PollutionClear;
            uint uncovered = 0;
            foreach (var cell in grid.AllPollutableCells)
                if (grid.IsPolluted(cell) && (clear == null || !clear[cell])) uncovered++;
            return uncovered;
        }
    }
}
