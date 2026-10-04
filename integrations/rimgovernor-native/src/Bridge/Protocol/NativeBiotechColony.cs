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
        private static readonly System.Reflection.FieldInfo? AssemblerTotalWork = BridgeCommon.PrivateInstanceField(typeof(Building_GeneAssembler), "totalWorkRequired");
        private static readonly System.Reflection.FieldInfo? AssemblerPacks = BridgeCommon.PrivateInstanceField(typeof(Building_GeneAssembler), "genepacksToRecombine");
        private static readonly System.Reflection.FieldInfo? ExtractorTicks = BridgeCommon.PrivateInstanceField(typeof(Building_GeneExtractor), "ticksRemaining");
        private static readonly System.Reflection.FieldInfo? ExtractorPowerCut = BridgeCommon.PrivateInstanceField(typeof(Building_GeneExtractor), "powerCutTicks");
        private static readonly System.Reflection.FieldInfo? XenogermTarget = BridgeCommon.PrivateInstanceField(typeof(Xenogerm), "targetPawn");
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
                    var container = b.TryGetComp<CompGenepackContainer>();
                    if (container != null) facts.GeneBanks.Add(GeneBank(b, container));
                    if (b is Building_GeneAssembler assembler) facts.GeneAssemblers.Add(GeneAssembler(assembler));
                    if (b is Building_GeneExtractor extractor) facts.GeneExtractors.Add(GeneExtractor(extractor));
                    if (container != null)
                        foreach (var pack in container.ContainedGenepacks.OrderBy(p => p.thingIDNumber)) facts.Genepacks.Add(GenepackRow(pack, b, null));
                }

                foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver).OrderBy(t => t.thingIDNumber)) {
                    if (t is Genepack loose && loose.Spawned) facts.Genepacks.Add(GenepackRow(loose, null, loose.Position));
                    else if (t is Xenogerm germ && germ.Spawned) facts.Xenogerms.Add(XenogermRow(germ));
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

        private static IEnumerable<string> Genes(GeneSet? set) => (set?.GenesListForReading ?? new List<GeneDef>())
            .Select(g => Id(g.defName)).Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);

        private static IEnumerable<string> Ids(IEnumerable<Thing?>? things) => (things ?? Enumerable.Empty<Thing?>())
            .Where(t => t != null).Select(t => Id(t!.GetUniqueLoadID())).Where(i => i != null).Select(i => i!).OrderBy(i => i, StringComparer.Ordinal);

        private static Obs.GeneBankState GeneBank(Building b, CompGenepackContainer c)
        {
            var row = new Obs.GeneBankState { ThingId = Id(b.GetUniqueLoadID()), DefName = Id(b.def.defName), Position = Cell(b.Position),
                Powered = c.PowerOn, Capacity = c.Props.maxCapacity, AutoLoad = c.autoLoad };
            row.PackIds.Add(Ids(c.ContainedGenepacks));
            return row;
        }

        private static Obs.GeneAssemblerState GeneAssembler(Building_GeneAssembler a)
        {
            var row = new Obs.GeneAssemblerState { ThingId = Id(a.GetUniqueLoadID()), DefName = Id(a.def.defName), Position = Cell(a.Position), Powered = a.PowerOn,
                Working = a.Working, MaxComplexity = a.MaxComplexity() };
            row.LinkedBankIds.Add(Ids(a.ConnectedFacilities?.Where(f => f.TryGetComp<CompGenepackContainer>() != null)));
            if (a.Working) {
                if (Finite(a.ProgressPercent) is float progress) row.Progress = progress;
                if (AssemblerTotalWork?.GetValue(a) is float total && Finite(total) is float finiteTotal) row.TotalWork = finiteTotal;
                if (AssemblerPacks?.GetValue(a) is List<Genepack> packs) row.PackIds.Add(Ids(packs));
                row.ArchitesOwed = Math.Max(0, a.ArchitesRequiredNow);
                row.CanWorkNow = a.CanBeWorkedOnNow.Accepted;
            }
            return row;
        }

        private static Obs.GeneExtractorState GeneExtractor(Building_GeneExtractor e)
        {
            var row = new Obs.GeneExtractorState { ThingId = Id(e.GetUniqueLoadID()), DefName = Id(e.def.defName), Position = Cell(e.Position), Powered = e.PowerOn,
                Working = e.Working };
            if (e.SelectedPawn != null) row.SelectedPawnId = Id(e.SelectedPawn.GetUniqueLoadID());
            if (e.innerContainer.FirstOrDefault() is Pawn occupant) row.OccupantId = Id(occupant.GetUniqueLoadID());
            if (e.Working) {
                if (ExtractorTicks?.GetValue(e) is int ticks) row.TicksRemaining = ticks;
                if (ExtractorPowerCut?.GetValue(e) is int cut) row.PowerCutTicks = cut;
            }
            return row;
        }

        private static Obs.GenepackState GenepackRow(Genepack pack, Building? bank, IntVec3? at)
        {
            var row = new Obs.GenepackState { ThingId = Id(pack.GetUniqueLoadID()), DefName = Id(pack.def.defName), Deteriorating = pack.Deteriorating, AutoLoad = pack.AutoLoad,
                HitPoints = pack.HitPoints, Complexity = pack.GeneSet.ComplexityTotal, Metabolism = pack.GeneSet.MetabolismTotal, Archites = pack.GeneSet.ArchitesTotal };
            row.Genes.Add(Genes(pack.GeneSet));
            if (bank != null) row.BankId = Id(bank.GetUniqueLoadID());
            if (at is IntVec3 cell) row.Position = Cell(cell);
            return row;
        }

        private static Obs.XenogermState XenogermRow(Xenogerm g)
        {
            var row = new Obs.XenogermState { ThingId = Id(g.GetUniqueLoadID()), DefName = Id(g.def.defName), Position = Cell(g.Position),
                Complexity = g.GeneSet.ComplexityTotal, Metabolism = g.GeneSet.MetabolismTotal, Archites = g.GeneSet.ArchitesTotal, Forbidden = g.IsForbidden(Faction.OfPlayer) };
            row.Genes.Add(Genes(g.GeneSet));
            if (XenogermTarget?.GetValue(g) is Pawn target) row.TargetPawnId = Id(target.GetUniqueLoadID());
            // The pawns an implant may target (Xenogerm target rules): the game's
            // own metabolism after implanting, per pawn.
            if (g.Map != null)
                foreach (var p in g.Map.mapPawns.AllPawnsSpawned.Where(p => p.genes != null && p.RaceProps.Humanlike && !p.IsQuestLodger()
                    && (p.IsColonist || p.IsSlaveOfColony || p.IsPrisonerOfColony)).OrderBy(p => p.thingIDNumber))
                    if (Id(p.GetUniqueLoadID()) is string pawnId)
                        row.ImplantMetabolism.Add(new Obs.XenogermImplantMetabolism { PawnId = pawnId, MetabolismAfter = GeneUtility.MetabolismAfterImplanting(p, g.GeneSet) });
            return row;
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
