using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The explicit worker/target footprint already persisted with an action. It carries no prediction of the native
/// route to that target. (Go <c>DispatchSite</c>.)
/// </summary>
public readonly record struct DispatchSite(PawnId Worker, string Target, Fact<Cell> Cell);

/// <summary>The dispatch footprint of an action, read from its payload (Go <c>action_rows</c> columns <c>pawn,target,x,z</c>).</summary>
internal static class ActionSites
{
    public static DispatchSite Of(DomainAction a)
    {
        static DispatchSite Site(PawnId worker, string target, Cell? cell) =>
            new(worker, target, cell is { } c ? Fact.Known(c) : default);

        return a.Payload switch
        {
            OwnedDraft p => Site(p.Pawn, "", null),
            Subdue p => Site(p.Pawn, p.Target.Value, null),
            Acquisition p => Site(default, p.Thing, p.Cell),
            Excavation p => Site(default, "", p.Cell),
            FoundationRemoval p => Site(default, "", p.Cell),
            FloorRemoval p => Site(default, "", p.Cell),
            Deconstruction p => Site(default, p.Target, p.Cell),
            MoveBuilding p => Site(default, p.Thing, p.Cell),
            CutPlant p => Site(default, p.Plant, p.Cell),
            CoverClearance p => Site(default, p.Thing, p.Cell),
            WastepackHaul p => Site(default, p.Thing, p.Cell),
            SupplyAllow p => Site(default, p.Thing, p.Cell),
            Tend p => Site(p.Doctor, p.Patient.Value, null),
            Rescue p => Site(p.Rescuer, p.Patient.Value, null),
            DropEquipment p => Site(p.Pawn, p.Thing, null),
            Capture p => Site(p.Capturer, p.Patient.Value, null),
            UseItem p => Site(p.Pawn, p.Target.Value, null),
            CombatBatch p => Site(default, p.Fight.Value, null),
            Strip p => Site(default, p.Target, null),
            Movement p => Site(p.Pawn, "", p.Destination),
            Haul p => Site(p.Pawn, p.Thing, p.Cell),
            Equip p => Site(p.Pawn, p.Thing, p.Cell),
            GearReplace p => Site(p.Pawn, p.Thing, null),
            OpenCasket p => Site(p.Pawn, p.Casket, p.Cell),
            Repair p => Site(p.Pawn, p.Structure, p.Cell),
            Clean p => Site(p.Pawn, p.Filth, p.Cell),
            RecoveryService p => Site(p.Pawn, p.Thing, null),
            Husbandry p => Site(default, p.Animal.Value, null),
            PrisonerInteraction p => Site(default, p.Pawn.Value, null),
            Ritual p => Site(p.Pawn, "", p.Verb == RitualVerb.Begin ? p.Spot : null),
            QuestAccept p => Site(p.AccepterPawn, p.Quest.Value, null),
            QuestShuttle p => Site(default, p.Quest.Value, null),
            HackDesignation p => Site(default, p.Target, null),
            GiveItem p => Site(p.Hauler, p.Recipient.Value, null),
            Ignite p => Site(p.Pawn, "", p.Cell),
            Gathering p => Site(p.Organizer, "", null),
            RemoveProductionBill p => Site(default, p.Bench, null),
            DoorControl p => Site(default, "", p.Cell),
            Ability p => Site(p.Pawn, AbilityTarget(p.Target), p.Target.Kind == AbilityTargetKind.Cell ? p.Target.Cell : null),
            BuildingTemperature p => Site(default, p.Thing, null),
            AutoRefuel p => Site(default, p.Thing, null),
            ClaimBuilding p => Site(default, p.Thing, null),
            GrowerCrop p => Site(default, p.Thing, null),
            BedUse p => Site(default, p.Thing, null),
            Assign p => Site(p.Pawn, p.Thing, null),
            ZoneDelete p => Site(default, p.Zone, null),
            ZoneCellEdit p => Site(default, p.Zone, null),
            StockpilePatch p => Site(default, p.Target, null),
            MoodRelief p => Site(p.Pawn, "", null),
            PawnSettings p => Site(p.Pawn, "", null),
            WorkAssignment p => Site(p.Pawn, "", null),
            Surgery p => Site(p.Pawn, "", null),
            DialogAnswer p => Site(default, "", new Cell(p.WindowId, p.OptionIndex)),
            Area p => Site(default, p.Key, null),
            BuildingOrder p => Site(default, "", p.Building.Cell),
            _ => Site(default, "", null),
        };
    }

    private static string AbilityTarget(AbilityTarget target) =>
        target.Kind is AbilityTargetKind.Pawn or AbilityTargetKind.Thing ? Wire.Name(target.Kind) + ":" + target.Id : "";
}
