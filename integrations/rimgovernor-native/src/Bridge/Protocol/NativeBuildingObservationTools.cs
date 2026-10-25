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
            // The settings the brain reads: a temperature control (NativeBuildingTemperature, with its CAS
            // snapshot), a humanlike bed (NativeBedUse) or a plant grower (NativeGrowerCrop).
            var tempControl = thing.TryGetComp<CompTempControl>();
            Obs.BuildingControl control =
                tempControl != null ? new Obs.BuildingControl.Temperature(Eligible(NativeBuildingTemperature.Snapshot(thing, context), thing), tempControl.targetTemperature)
                : NativeBedUse.Eligible(thing) ? NativeBedUse.Control((Building_Bed)thing)
                : NativeGrowerCrop.Eligible(thing) ? NativeGrowerCrop.Control((Building_PlantGrower)thing)
                : new Obs.BuildingControl.None();
            return row with { Settings = row.Settings with { Control = control } };
        }

        // The temperature snapshot of a building with a temperature control; one its eligibility test then
        // refuses is a broken invariant that fails the read.
        private static Obs.SnapshotRef Eligible(Obs.SnapshotRef? snapshot, Thing thing) =>
            snapshot ?? throw new InvalidOperationException($"building {thing.GetUniqueLoadID()} has a temperature control but no temperature snapshot");

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
            DefName = Id(thing.def.defName), Label = PlacementPreviewOperation.Diagnostic(thing.LabelCap), Place = new Obs.EntityPlace.OnMap(thing.Map.uniqueID, Cell(thing.Position)) };

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
                Roofed = rectangle.Cells.All(cell => cell.Roofed(thing.Map)),
                RainVulnerable = thing.def.GetCompProperties<CompProperties_Power>()?.shortCircuitInRain ?? false,
                FuelDefinitions = [.. (thing.def.GetCompProperties<CompProperties_Refuelable>() is { } refuelable ? refuelable.fuelFilter.AllowedThingDefs.Select(def => def.defName).OrderBy(name => name, StringComparer.Ordinal) : Enumerable.Empty<string>())],
                Turret = thing is Building nativeBuilding && NativeDefenseStats.TurretDps(nativeBuilding) is { } dps ? new Obs.TurretGun(Nonnegative(dps)) : null,
                Conduit = thing.def.thingClass == typeof(Building) && thing.def.GetCompProperties<CompProperties_Power>()?.compClass == typeof(CompPowerTransmitter)
                    ? thing.def == ThingDefOf.PowerConduit ? Obs.ConduitKind.Exposed : Obs.ConduitKind.Protected
                    : Obs.ConduitKind.None,
                Rotation = Rotation(thing.Rotation),
                Stuff = stuff == null ? null : Id(stuff.defName),
                Faction = thing.Faction == null ? null : NativeRef.Of(Id(thing.Faction.GetUniqueLoadID())),
                Status = Status(thing),
                HitPoints = HitPoints(thing),
                Construction = Construction(thing),
                Service = Service(thing),
                Settings = new Obs.BuildingSettings { Forbidden = player != null && thing.IsForbidden(player), Control = new Obs.BuildingControl.None() },
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

        // Service is a building's power, switch, breakdown, fuel and wind
        // state, read whole: power is a trader (network, output, whether it
        // produces), a battery (network) or none; fuel is refuelable or not;
        // the wind path is a turbine's blocked cells or none; the flick
        // switch is on without one.
        private static Obs.BuildingServiceState Service(Thing thing) => new()
        {
            Power = Power(thing),
            SwitchedOn = thing.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true,
            Fuel = thing.TryGetComp<CompRefuelable>() is { } fuel
                ? new Obs.BuildingFuel.Refuelable { Burn = new Obs.FuelBurn {
                    PerDay = Nonnegative(fuel.Props.fuelConsumptionRate), UnitsPerItem = Nonnegative(fuel.Props.FuelMultiplierCurrentDifficulty),
                    UseDriven = fuel.Props.consumeFuelOnlyWhenUsed || fuel.Props.externalTicking,
                    InRain = fuel.Props.fuelConsumptionPerTickInRain > 0,
                    WhenPowered = fuel.Props.consumeFuelOnlyWhenPowered, Flickable = thing.TryGetComp<CompFlickable>() != null }, Fuel = Nonnegative(fuel.Fuel), TargetFuel = Nonnegative(fuel.TargetFuelLevel), OutOfFuel = !fuel.HasFuel }
                : new Obs.BuildingFuel.NotRefuelable(),
            BrokenDown = thing.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false,
            Wind = thing.TryGetComp<CompPowerPlantWind>() != null
                ? new Obs.BuildingWind.WindTurbine { BlockedCells = checked((uint)PlacementPreviewOperation.WindBlockedCells(thing.Map, thing.Position, thing.Rotation, thing.def.size)) }
                : new Obs.BuildingWind.NotWindTurbine(),
        };

        private static Obs.BuildingPower Power(Thing thing)
        {
            if (thing.TryGetComp<CompPowerTrader>() is { } power)
            {
                return new Obs.BuildingPower.Trader
                {
                    PowerOn = power.PowerOn,
                    OutputW = Finite(power.PowerOutput) ? power.PowerOutput : throw new InvalidOperationException("Invalid power output."),
                    RatedW = Finite(power.Props.PowerConsumption) ? -power.Props.PowerConsumption : throw new InvalidOperationException("Invalid rated power."),
                    Network = power.PowerNet is { } net ? NetId(net) : null,
                };
            }
            if (thing.TryGetComp<CompPowerBattery>() is { } battery)
            {
                return new Obs.BuildingPower.Battery { StorageWD = Nonnegative(battery.Props.storedEnergyMax), Network = battery.PowerNet is { } net ? NetId(net) : null };
            }
            return new Obs.BuildingPower.NoPower();
        }

        // NetId names a power network for the frame it is read in.
        internal static string NetId(PowerNet net) => net.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Construction is a blueprint's or frame's state, absent on a built row. An installation blueprint carries
        // nothing: TotalMaterialCost on it logs an error and pauses the simulation, and native installation consumes
        // no material.
        private static Obs.ConstructionState? Construction(Thing thing)
        {
            if (thing is Blueprint_Install) return new Obs.ConstructionState.Installation();
            if (!(thing is Blueprint || thing is Frame)) return null;
            var definition = thing.def.entityDefToBuild ?? throw new InvalidOperationException("Construction definition missing.");
            if (!(thing is IConstructible construction)) throw new InvalidOperationException("Constructible unavailable.");
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
            return ConstructionSkillGuard.Read(thing, Id(definition.defName), [.. resources]);
        }

        private static bool Identifiers(IEnumerable<string> values) => values.Count() <= 256
            && values.All(ProtoBoundary.IsIdentifier) && values.Distinct(StringComparer.Ordinal).Count() == values.Count();
        private static RimGovernor.Brain.Domain.Cell Cell(IntVec3 cell) => new RimGovernor.Brain.Domain.Cell { X = cell.x, Z = cell.z };
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
