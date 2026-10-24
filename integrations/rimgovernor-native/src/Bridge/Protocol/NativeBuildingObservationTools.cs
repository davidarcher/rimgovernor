#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace HomeBridge.BridgeTools
{
    internal static class NativeBuildingObservationTools
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade. A refused request
        // or a census that cannot be read completely throws.
        internal static Obs.BuildingsSnapshot HandleListBuildings(Obs.ListBuildingsRequest parsed)
        {
            if (!Validate(parsed, out var invalidRequest)) throw NativeFaults.Refused(invalidRequest);
            if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                throw NativeFaults.Refused(error);
            try { return Read(map, parsed, context); }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Building census read failed: " + readError);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Building facts could not be read completely: " + readError.GetType().Name + ": " + readError.Message) });
            }
        }

        // Read is the list on the main thread under a validated identity: the
        // reply its tool encodes, and the section the bundle carries. A
        // building it cannot read completely throws, naming the building.
        internal static Obs.BuildingsSnapshot Read(Map map, Obs.ListBuildingsRequest parsed, Common.ObservationContext context)
        {
            if (!parsed.AnyFaction && Faction.OfPlayerSilentFail == null) throw new InvalidOperationException("Player faction unavailable.");
            var rows = new List<Obs.BuildingState>();
            foreach (var thing in Source(map).OrderBy(t => t.thingIDNumber))
            {
                try { if (Matches(thing, parsed)) rows.Add(Row(thing, context)); }
                catch (Exception error)
                {
                    throw new InvalidOperationException($"building {thing.thingIDNumber} ({thing.def?.defName}): {error.GetType().Name}: {error.Message}", error);
                }
            }
            return new Obs.BuildingsSnapshot { Context = context, Buildings = [.. rows] };
        }

        // Row is one listed building as the read emits it: the projection
        // with the settings row of the patchable kinds.
        private static Obs.BuildingState Row(Thing thing, Common.ObservationContext context)
        {
            var row = Project(thing);
            // Only the implemented BuildingPatchIntent arms carry a
            // settings row with their own dedicated CAS snapshot:
            // target_temperature_c (NativeBuildingTemperature), a
            // humanlike bed's medical flag (NativeBedUse), a
            // plant grower's crop (NativeGrowerCrop) and a claimable
            // building's faction (NativeClaimBuilding).
            // A player storage building (shelf) reports its storage token
            // (NativeStockpilePatch, a zone intent settings patch on a building) first.
            var tempControl = thing.TryGetComp<CompTempControl>();
            var forbidden = row.Settings.Forbidden;
            if (NativeStockpilePatch.StorageEligible(thing))
                row = row with { Settings = NativeStockpilePatch.Settings((Building_Storage)thing, context) };
            else if (tempControl != null)
                row = row with { Settings = new Obs.BuildingSettings { Snapshot = NativeBuildingTemperature.Snapshot(thing, context), TargetTemperatureC = tempControl.targetTemperature } };
            else if (NativeBedUse.Eligible(thing))
                row = row with { Settings = NativeBedUse.Settings((Building_Bed)thing, context) };
            else if (NativeGrowerCrop.Eligible(thing))
                row = row with { Settings = NativeGrowerCrop.Settings((Building_PlantGrower)thing, context) };
            else if (NativeClaimBuilding.Eligible(thing))
                row = row with { Settings = NativeClaimBuilding.Settings((Building)thing, context) };
            row = row with { Settings = row.Settings with { Forbidden = forbidden } };
            return row;
        }

        internal static bool Validate(Obs.ListBuildingsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope and exact bounded identifiers are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            return Identifiers(request.Ids) && Identifiers(request.DefNames);
        }

        // Match the existing listing's native coverage without its aggregation,
        // safe-read defaults, fuzzy name matching or detailed-row truncation.
        private static List<Thing> Source(Map map)
        {
            var seen = new HashSet<Thing>();
            foreach (var group in new[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame,
                ThingRequestGroup.BuildingArtificial, ThingRequestGroup.PotentialBillGiver })
                foreach (var thing in map.listerThings.ThingsInGroup(group))
                    if (thing != null && !(thing is Pawn) && !(thing is Corpse) && thing.Spawned && thing.Map == map) seen.Add(thing);
            return seen.ToList();
        }

        private static bool Matches(Thing thing, Obs.ListBuildingsRequest request)
        {
            if (!request.AnyFaction && thing.Faction != Faction.OfPlayerSilentFail) return false;
            if (request.Ids.Count != 0 && !request.Ids.Contains(Id(thing.GetUniqueLoadID()))) return false;
            var built = thing is Blueprint_Install install ? InstallTarget(install).def : thing.def.entityDefToBuild;
            if (request.DefNames.Count != 0 && !request.DefNames.Contains(thing.def.defName)
                && (built == null || !request.DefNames.Contains(built.defName))) return false;
            return request.Statuses.Count == 0 || request.Statuses.Contains(Status(thing));
        }

        internal static Obs.SnapshotRef Token(Thing thing, Common.ObservationContext context) =>
            NativeObservationSnapshot.Snapshot("building", context, Id(thing.GetUniqueLoadID()), w => {
                w.Write((int)Status(thing)); w.Write(thing.HitPoints); w.Write(thing.IsBurning());
            });

        // Ref points at a spawned building's row: the reference
        // every message other than the building table and the list read carries.
        internal static Common.Ref Ref(Thing thing) => new Common.Ref { Id = Id(thing.GetUniqueLoadID()) };

        // Head is a building row's identity: id, definition, label and cell.
        private static Obs.EntityRef Head(Thing thing) => new Obs.EntityRef { Id = Id(thing.GetUniqueLoadID()),
            DefName = Id(thing.def.defName), Label = PlacementPreviewOperation.Diagnostic(thing.LabelCap), MapId = thing.Map.uniqueID,
            Position = Cell(thing.Position) };

        // Project is the one building row builder, whole: the bundle's
        // building table and the list read emit it. Every row carries its
        // service state and forbidden flag; the list read adds the patchable
        // kinds' settings with their own CAS snapshots (Row).
        internal static Obs.BuildingState Project(Thing thing)
        {
            if (!thing.Spawned || thing.Map == null) throw new InvalidOperationException("Building is not spawned.");
            var stuff = (thing is Blueprint || thing is Frame) && thing is IConstructible constructible ? constructible.EntityToBuildStuff() : thing.Stuff;
            var rectangle = thing.OccupiedRect();
            foreach (var cell in rectangle.Cells)
                if (!cell.InBounds(thing.Map)) throw new InvalidOperationException("Building geometry is outside its map.");
            var player = Faction.OfPlayerSilentFail;
            return new Obs.BuildingState
            {
                Building = Head(thing),
                Rotation = Rotation(thing.Rotation),
                Stuff = stuff == null ? null : Id(stuff.defName),
                Faction = thing.Faction == null ? null : NativeRef.Of(Id(thing.Faction.GetUniqueLoadID())),
                Status = Status(thing),
                HitPoints = HitPoints(thing),
                Construction = Construction(thing, stuff),
                Service = Service(thing),
                Settings = new Obs.BuildingSettings { Forbidden = player != null && thing.IsForbidden(player) },
                Burning = thing.IsBurning(),
                Occupied = new Obs.Rectangle { Minimum = Cell(new IntVec3(rectangle.minX, 0, rectangle.minZ)), Maximum = Cell(new IntVec3(rectangle.maxX, 0, rectangle.maxZ)) },
                Anomaly = NativeAnomalyFacts.Building(thing),
                Door = thing is Building_Door door ? new Obs.BuildingDoor { Open = door.Open, HoldOpen = door.HoldOpen } : null,
            };
        }

        // HitPoints is a building's hit points; a def without them (useHitPoints false) is untracked.
        private static Obs.BuildingHitPoints HitPoints(Thing thing)
        {
            if (!thing.def.useHitPoints) return new Obs.BuildingHitPoints.Untracked();
            if (thing.HitPoints < 0 || thing.MaxHitPoints <= 0) throw new InvalidOperationException("Invalid native hit points.");
            return new Obs.BuildingHitPoints.Tracked { Current = thing.HitPoints, Maximum = thing.MaxHitPoints };
        }

        // Service is a building's power, switch, breakdown and fuel state:
        // a power trader's network, output and whether it produces, a
        // battery's network, a wind turbine's blocked wind path cells, the
        // flick switch (on without one) and a refuelable's fuel.
        private static Obs.BuildingServiceState Service(Thing thing)
        {
            var service = new Obs.BuildingServiceState { SwitchedOn = thing.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true,
                BrokenDown = thing.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false };
            var power = thing.TryGetComp<CompPowerTrader>();
            var battery = thing.TryGetComp<CompPowerBattery>();
            var net = power?.PowerNet ?? battery?.PowerNet;
            if (power != null) { service = service with { Connected = net != null }; service = service with { PowerOn = power.PowerOn }; service = service with { PowerOutputW = Finite(power.PowerOutput) ? power.PowerOutput : throw new InvalidOperationException("Invalid power output.") }; }
            else if (battery != null) { service = service with { Connected = net != null }; service = service with { PowerOn = net != null }; service = service with { PowerOutputW = 0 }; }
            if (net != null) service = service with { PowerNetId = NetId(net) };
            if (power != null) service = service with { Producer = power.Props.PowerConsumption < 0f };
            if (thing.TryGetComp<CompPowerPlantWind>() != null)
                service = service with { WindPathBlockedCells = checked((uint)PlacementPreviewOperation.WindBlockedCells(thing.Map, thing.Position, thing.Rotation, thing.def.size)) };
            var fuel = thing.TryGetComp<CompRefuelable>();
            if (fuel != null)
            {
                service = service with { Fuel = Nonnegative(fuel.Fuel) }; service = service with { TargetFuel = Nonnegative(fuel.TargetFuelLevel) }; service = service with { OutOfFuel = !fuel.HasFuel };
            }
            return service;
        }

        // NetId names a power network for the frame it is read in.
        internal static string NetId(PowerNet net) => net.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Construction is a blueprint's or frame's state, absent on a built row. An installation blueprint carries
        // nothing: TotalMaterialCost on it logs an error and pauses the simulation, and native installation consumes
        // no material.
        private static Obs.ConstructionState? Construction(Thing thing, ThingDef? stuff)
        {
            if (thing is Blueprint_Install) return new Obs.ConstructionState.Installation();
            if (!(thing is Blueprint || thing is Frame)) return null;
            var definition = thing.def.entityDefToBuild ?? throw new InvalidOperationException("Construction definition missing.");
            if (!(thing is IConstructible construction)) throw new InvalidOperationException("Constructible unavailable.");
            var work = thing is Frame frame ? NativeConstructionWork.Remaining(frame.WorkLeft)
                : Nonnegative(definition.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff));
            var costs = construction.TotalMaterialCost() ?? throw new InvalidOperationException("Cost list unavailable.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var resources = new List<Obs.MaterialDeficit>();
            foreach (var cost in costs)
            {
                var name = Id(cost.thingDef.defName);
                if (!seen.Add(name) || cost.count < 0) throw new InvalidOperationException("Invalid native construction costs.");
                var needed = construction.ThingCountNeeded(cost.thingDef);
                if (needed < 0) throw new InvalidOperationException("Invalid native construction quantities.");
                resources.Add(new Obs.MaterialDeficit { DefName = name, StillNeeded = needed });
            }
            return ConstructionSkillGuard.Read(thing, Id(definition.defName), work, [.. resources]);
        }

        private static bool Identifiers(IEnumerable<string> values) => values.Count() <= 256
            && values.All(ProtoBoundary.IsIdentifier) && values.Distinct(StringComparer.Ordinal).Count() == values.Count();
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static Obs.BuildingStatus Status(Thing thing) => thing is Blueprint ? Obs.BuildingStatus.Blueprint : thing is Frame ? Obs.BuildingStatus.Frame : Obs.BuildingStatus.Built;
        private static Thing InstallTarget(Blueprint_Install install)
        {
            var held = install.MiniToInstallOrBuildingToReinstall;
            return (held is MinifiedThing mini ? mini.InnerThing : held) ?? throw new InvalidOperationException("Installation target unavailable.");
        }
        private static Placement.Rotation Rotation(Rot4 rotation) => rotation == Rot4.North ? Placement.Rotation.North : rotation == Rot4.East ? Placement.Rotation.East
            : rotation == Rot4.South ? Placement.Rotation.South : rotation == Rot4.West ? Placement.Rotation.West : throw new InvalidOperationException("Invalid rotation.");
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native ID unavailable.");
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double Nonnegative(double value) => Finite(value) && value >= 0 ? value : throw new InvalidOperationException("Invalid native amount.");
    }
}
