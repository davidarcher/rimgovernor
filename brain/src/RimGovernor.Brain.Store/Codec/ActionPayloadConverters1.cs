using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

// Hand-written converters for the action payload records (A to H). Field names are the Go field names.

public sealed class AbilitySourceConverter() : StringCodec<AbilitySource>(AbilitySource.ParseAbilitySource, s => s.Key);

public sealed class AbilityTargetConverter : ObjectCodec<AbilityTarget>
{
    protected override AbilityTarget Read(JsonElement e, Codecs o) =>
        e.Get<AbilityTargetKind>("Kind", o) switch
        {
            AbilityTargetKind.None => AbilityTarget.NoAbilityTarget(),
            AbilityTargetKind.Cell => AbilityTarget.AbilityCellTarget(e.Get<Cell>("Cell", o)),
            AbilityTargetKind.Pawn => AbilityTarget.AbilityPawnTarget(new PawnId(e.Str("Id"))),
            AbilityTargetKind.Thing => AbilityTarget.AbilityThingTarget(e.Str("Id")),
            _ => throw new StoreCorruptException("unknown ability target kind"),
        };

    protected override void WriteFields(Utf8JsonWriter w, AbilityTarget v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        if (v.Kind == AbilityTargetKind.Cell)
        {
            w.Put("Cell", v.Cell, o);
        }
        else if (v.Kind != AbilityTargetKind.None)
        {
            w.WriteString("Id", v.Id);
        }
    }
}

public sealed class AbilityConverter : ObjectCodec<Ability>
{
    protected override Ability Read(JsonElement e, Codecs o) =>
        new(e.Get<PawnId>("Pawn", o), e.Get<AbilitySource>("Source", o), e.Get<AbilityTarget>("Target", o));

    protected override void WriteFields(Utf8JsonWriter w, Ability v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Source", v.Source, o);
        w.Put("Target", v.Target, o);
    }
}

public sealed class AcquisitionConverter : ObjectCodec<Acquisition>
{
    protected override Acquisition Read(JsonElement e, Codecs o) => new(e.Str("Thing"), e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, Acquisition v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class ApparelPolicyConverter : ObjectCodec<ApparelPolicy>
{
    protected override ApparelPolicy Read(JsonElement e, Codecs o) =>
        new(
            new ApparelPolicySpec(
                e.Get<PawnId>("Pawn", o),
                e.Str("Token"),
                e.Str("Name"),
                e.Get<EquatableArray<string>>("Definitions", o),
                e.Dbl("MinHP"),
                e.Dbl("MaxHP"),
                e.I32("MinQuality"),
                e.I32("MaxQuality")
            )
        );

    protected override void WriteFields(Utf8JsonWriter w, ApparelPolicy v, Codecs o)
    {
        w.Put("Pawn", v.Spec.Pawn, o);
        w.WriteString("Token", v.Spec.Token);
        w.WriteString("Name", v.Spec.Name);
        w.Put("Definitions", v.Spec.Definitions, o);
        w.WriteNumber("MinHP", v.Spec.MinHP);
        w.WriteNumber("MaxHP", v.Spec.MaxHP);
        w.WriteNumber("MinQuality", v.Spec.MinQuality);
        w.WriteNumber("MaxQuality", v.Spec.MaxQuality);
    }
}

public sealed class AreaRectConverter : ObjectCodec<AreaRect>
{
    protected override AreaRect Read(JsonElement e, Codecs o) => new(e.I32("MinX"), e.I32("MinZ"), e.I32("MaxX"), e.I32("MaxZ"));

    protected override void WriteFields(Utf8JsonWriter w, AreaRect v, Codecs o)
    {
        w.WriteNumber("MinX", v.MinX);
        w.WriteNumber("MinZ", v.MinZ);
        w.WriteNumber("MaxX", v.MaxX);
        w.WriteNumber("MaxZ", v.MaxZ);
    }
}

public sealed class AreaConverter : ObjectCodec<Area>
{
    protected override Area Read(JsonElement e, Codecs o)
    {
        var operation = e.Get<AreaOperation>("Operation", o);
        var cells = Area.ExpandAreaRects(e.Get<EquatableArray<AreaRect>>("Rects", o));
        return e.BoolOr("PollutionClear") ? Area.NewPollutionClearArea(operation, cells) : new Area(operation, e.StrOr("Key"), cells);
    }

    protected override void WriteFields(Utf8JsonWriter w, Area v, Codecs o)
    {
        w.Put("Operation", v.Operation, o);
        w.WriteString("Key", v.Key);
        w.WriteBoolean("PollutionClear", v.PollutionClear);
        w.Put("Rects", v.Rects, o);
    }
}

public sealed class AreaPlantCutConverter() : CellsPayloadConverterBase<AreaPlantCut>(c => new AreaPlantCut(c), v => v.Cells);

public sealed class BuildRoofConverter() : CellsPayloadConverterBase<BuildRoof>(c => new BuildRoof(c), v => v.Cells);

public sealed class RemoveRoofConverter() : CellsPayloadConverterBase<RemoveRoof>(c => new RemoveRoof(c), v => v.Cells);

/// <summary>The shared shape of a payload that is a canonical cell list.</summary>
public abstract class CellsPayloadConverterBase<T>(Func<IReadOnlyCollection<Cell>, T> make, Func<T, EquatableArray<Cell>> cells) : ObjectCodec<T>
{
    protected override T Read(JsonElement e, Codecs o) => make(e.Get<EquatableArray<Cell>>("Cells", o).Items);

    protected override void WriteFields(Utf8JsonWriter w, T v, Codecs o) => w.Put("Cells", cells(v), o);
}

public sealed class PreviousAssignmentConverter : ObjectCodec<PreviousAssignment>
{
    protected override PreviousAssignment Read(JsonElement e, Codecs o) =>
        e.Bool("Clear") ? PreviousAssignment.ClearPrevious() : PreviousAssignment.KnownPrevious(e.Str("Id"));

    protected override void WriteFields(Utf8JsonWriter w, PreviousAssignment v, Codecs o)
    {
        w.WriteBoolean("Clear", v.Clear);
        w.WriteString("Id", v.Id);
    }
}

public sealed class AssignConverter : ObjectCodec<Assign>
{
    protected override Assign Read(JsonElement e, Codecs o)
    {
        var assign = new Assign(e.Get<PawnId>("Pawn", o), e.Str("Thing"), e.Get<PreviousAssignment>("Previous", o));
        return e.BoolOr("Swap") ? assign.AsSwap() : assign;
    }

    protected override void WriteFields(Utf8JsonWriter w, Assign v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Thing", v.Thing);
        w.Put("Previous", v.Previous, o);
        w.WriteBoolean("Swap", v.Swap);
    }
}

public sealed class AutoHomeAreaOrderConverter : ObjectCodec<AutoHomeAreaOrder>
{
    protected override AutoHomeAreaOrder Read(JsonElement e, Codecs o) => new(e.Bool("Enabled"));

    protected override void WriteFields(Utf8JsonWriter w, AutoHomeAreaOrder v, Codecs o) => w.WriteBoolean("Enabled", v.Enabled);
}

public sealed class AutoRefuelConverter : ObjectCodec<AutoRefuel>
{
    protected override AutoRefuel Read(JsonElement e, Codecs o) => new(e.Str("Thing"), e.Bool("Allow"));

    protected override void WriteFields(Utf8JsonWriter w, AutoRefuel v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteBoolean("Allow", v.Allow);
    }
}

public sealed class BedUseConverter : ObjectCodec<BedUse>
{
    protected override BedUse Read(JsonElement e, Codecs o) =>
        e.BoolOr("Prisoners") ? BedUse.NewBedPrisoners(e.Str("Thing"))
        : e.BoolOr("Slaves") ? BedUse.NewBedSlaves(e.Str("Thing"))
        : BedUse.NewBedMedical(e.Str("Thing"), e.Bool("Medical"));

    protected override void WriteFields(Utf8JsonWriter w, BedUse v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteBoolean("Medical", v.Medical);
        w.WriteBoolean("Prisoners", v.Prisoners);
        w.WriteBoolean("Slaves", v.Slaves);
    }
}

public sealed class BuildingTemperatureConverter : ObjectCodec<BuildingTemperature>
{
    protected override BuildingTemperature Read(JsonElement e, Codecs o) => new(e.Str("Thing"), e.Dbl("Celsius"));

    protected override void WriteFields(Utf8JsonWriter w, BuildingTemperature v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteNumber("Celsius", v.Celsius);
    }
}

public sealed class CaptureConverter : ObjectCodec<Capture>
{
    protected override Capture Read(JsonElement e, Codecs o) =>
        e.Has("Bed")
            ? Capture.NewArrest(e.Get<PawnId>("Capturer", o), e.Get<PawnId>("Patient", o), e.Str("Bed"))
            : new Capture(e.Get<PawnId>("Capturer", o), e.Get<PawnId>("Patient", o));

    protected override void WriteFields(Utf8JsonWriter w, Capture v, Codecs o)
    {
        w.Put("Capturer", v.Capturer, o);
        w.Put("Patient", v.Patient, o);
        if (v.Bed is { } bed)
        {
            w.WriteString("Bed", bed);
        }
    }
}

public sealed class CargoItemConverter : ObjectCodec<CargoItem>
{
    protected override CargoItem Read(JsonElement e, Codecs o) => new(e.Str("Definition"), e.U64("Count"));

    protected override void WriteFields(Utf8JsonWriter w, CargoItem v, Codecs o)
    {
        w.WriteString("Definition", v.Definition);
        w.WriteNumber("Count", v.Count);
    }
}

public sealed class CaravanDepartureConverter : ObjectCodec<CaravanDeparture>
{
    protected override CaravanDeparture Read(JsonElement e, Codecs o) =>
        new(e.Get<EquatableArray<PawnId>>("Crew", o), e.Get<EquatableArray<CargoItem>>("Cargo", o), e.I32("DestinationTile"));

    protected override void WriteFields(Utf8JsonWriter w, CaravanDeparture v, Codecs o)
    {
        w.Put("Crew", v.Crew, o);
        w.Put("Cargo", v.Cargo, o);
        w.WriteNumber("DestinationTile", v.DestinationTile);
    }
}

public sealed class ClaimBuildingConverter : ObjectCodec<ClaimBuilding>
{
    protected override ClaimBuilding Read(JsonElement e, Codecs o) => new(e.Str("Thing"));

    protected override void WriteFields(Utf8JsonWriter w, ClaimBuilding v, Codecs o) => w.WriteString("Thing", v.Thing);
}

public sealed class CleanConverter : ObjectCodec<Clean>
{
    protected override Clean Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Str("Filth"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, Clean v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Filth", v.Filth);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class CombatCommandConverter : ObjectCodec<CombatCommand>
{
    protected override CombatCommand Read(JsonElement e, Codecs o) =>
        new()
        {
            Kind = e.Str("Kind"),
            Pawn = e.GetOr("Pawn", default(PawnId), o),
            Target = e.GetOr("Target", default(PawnId), o),
            Cell = e.GetOr("Cell", default(Cell), o),
            Aim = e.GetOr("Aim", default(Cell), o),
            Door = e.StrOr("Door"),
            FireMode = e.StrOr("FireMode"),
            Clear = e.BoolOr("Clear"),
            Shell = e.StrOr("Shell"),
            Drug = e.StrOr("Drug"),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatCommand v, Codecs o)
    {
        w.WriteString("Kind", v.Kind);
        w.Put("Pawn", v.Pawn, o);
        w.Put("Target", v.Target, o);
        w.Put("Cell", v.Cell, o);
        w.Put("Aim", v.Aim, o);
        w.WriteString("Door", v.Door);
        w.WriteString("FireMode", v.FireMode);
        w.WriteBoolean("Clear", v.Clear);
        w.WriteString("Shell", v.Shell);
        w.WriteString("Drug", v.Drug);
    }
}

public sealed class CombatBatchConverter : ObjectCodec<CombatBatch>
{
    protected override CombatBatch Read(JsonElement e, Codecs o) =>
        new(e.Get<PlanId>("Fight", o), e.Str("Key"), e.Get<EquatableArray<CombatCommand>>("Orders", o).Items);

    protected override void WriteFields(Utf8JsonWriter w, CombatBatch v, Codecs o)
    {
        w.Put("Fight", v.Fight, o);
        w.WriteString("Key", v.Key);
        w.Put("Orders", v.Orders, o);
    }
}

public sealed class CombatResultConverter : ObjectCodec<CombatResult>
{
    protected override CombatResult Read(JsonElement e, Codecs o) =>
        new()
        {
            Index = e.I32("Index"),
            PawnId = e.Str("PawnId"),
            Applied = e.BoolOr("Applied"),
            Refusal = e.StrOr("Refusal"),
            JobDef = e.StrOr("JobDef"),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatResult v, Codecs o)
    {
        w.WriteNumber("Index", v.Index);
        w.WriteString("PawnId", v.PawnId);
        w.WriteBoolean("Applied", v.Applied);
        w.WriteString("Refusal", v.Refusal);
        w.WriteString("JobDef", v.JobDef);
    }
}

public sealed class CommsTradeRequestConverter : ObjectCodec<CommsTradeRequest>
{
    protected override CommsTradeRequest Read(JsonElement e, Codecs o) =>
        new(e.Get<TradeRequestKind>("Kind", o), e.Str("Faction"), e.Str("TraderKind"), e.Str("Console"), e.Str("Negotiator"), e.I64("ExpectedLastRequestTick"));

    protected override void WriteFields(Utf8JsonWriter w, CommsTradeRequest v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.WriteString("Faction", v.Faction);
        w.WriteString("TraderKind", v.TraderKind);
        w.WriteString("Console", v.Console);
        w.WriteString("Negotiator", v.Negotiator);
        w.WriteNumber("ExpectedLastRequestTick", v.ExpectedLastRequestTick);
    }
}

public sealed class CoverClearanceConverter : ObjectCodec<CoverClearance>
{
    protected override CoverClearance Read(JsonElement e, Codecs o) =>
        new(e.Str("Thing"), e.Str("Definition"), e.Get<CoverClearanceDesignation>("Designation", o), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, CoverClearance v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Designation", v.Designation, o);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class CutPlantConverter : ObjectCodec<CutPlant>
{
    protected override CutPlant Read(JsonElement e, Codecs o) => new(e.Str("Plant"), e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, CutPlant v, Codecs o)
    {
        w.WriteString("Plant", v.Plant);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class DeconstructionConverter : ObjectCodec<Deconstruction>
{
    protected override Deconstruction Read(JsonElement e, Codecs o)
    {
        var cut = new Deconstruction(e.Str("Target"), e.Str("Definition"), e.Get<Cell>("Cell", o));
        return e.BoolOr("ReplacesWithWall") ? cut.WithWallReplacement() : cut;
    }

    protected override void WriteFields(Utf8JsonWriter w, Deconstruction v, Codecs o)
    {
        w.WriteString("Target", v.Target);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
        w.WriteBoolean("ReplacesWithWall", v.ReplacesWithWall);
    }
}

public sealed class DialogAnswerConverter : ObjectCodec<DialogAnswer>
{
    protected override DialogAnswer Read(JsonElement e, Codecs o) =>
        e.StrOr("LetterToken").Length != 0
            ? DialogAnswer.NewJoinerLetterAnswer(e.I32("WindowId"), e.Str("OptionLabel"), e.Str("LetterToken"))
            : new DialogAnswer(e.I32("WindowId"), e.I32("OptionIndex"), e.Str("OptionLabel"));

    protected override void WriteFields(Utf8JsonWriter w, DialogAnswer v, Codecs o)
    {
        w.WriteNumber("WindowId", v.WindowId);
        w.WriteNumber("OptionIndex", v.OptionIndex);
        w.WriteString("OptionLabel", v.OptionLabel);
        w.WriteString("LetterToken", v.LetterToken);
    }
}

public sealed class DoorControlConverter : ObjectCodec<DoorControl>
{
    protected override DoorControl Read(JsonElement e, Codecs o) => new(e.Get<Cell>("Cell", o), e.Bool("HoldOpen"));

    protected override void WriteFields(Utf8JsonWriter w, DoorControl v, Codecs o)
    {
        w.Put("Cell", v.Cell, o);
        w.WriteBoolean("HoldOpen", v.HoldOpen);
    }
}

public sealed class DropEquipmentConverter : ObjectCodec<DropEquipment>
{
    protected override DropEquipment Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Str("Thing"));

    protected override void WriteFields(Utf8JsonWriter w, DropEquipment v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Thing", v.Thing);
    }
}

public sealed class DrugPolicyEntryConverter : ObjectCodec<DrugPolicyEntry>
{
    protected override DrugPolicyEntry Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Drug"),
            e.Bool("Joy"),
            e.Bool("Addiction"),
            e.Bool("Scheduled"),
            e.Dbl("DaysFrequency"),
            e.Dbl("OnlyIfMoodBelow"),
            e.Dbl("OnlyIfJoyBelow"),
            e.I32("TakeToInventory")
        );

    protected override void WriteFields(Utf8JsonWriter w, DrugPolicyEntry v, Codecs o)
    {
        w.WriteString("Drug", v.Drug);
        w.WriteBoolean("Joy", v.Joy);
        w.WriteBoolean("Addiction", v.Addiction);
        w.WriteBoolean("Scheduled", v.Scheduled);
        w.WriteNumber("DaysFrequency", v.DaysFrequency);
        w.WriteNumber("OnlyIfMoodBelow", v.OnlyIfMoodBelow);
        w.WriteNumber("OnlyIfJoyBelow", v.OnlyIfJoyBelow);
        w.WriteNumber("TakeToInventory", v.TakeToInventory);
    }
}

public sealed class DrugPolicyConverter : ObjectCodec<DrugPolicy>
{
    protected override DrugPolicy Read(JsonElement e, Codecs o) => new(e.Str("Name"), e.Get<EquatableArray<DrugPolicyEntry>>("Entries", o));

    protected override void WriteFields(Utf8JsonWriter w, DrugPolicy v, Codecs o)
    {
        w.WriteString("Name", v.Name);
        w.Put("Entries", v.Entries, o);
    }
}

public sealed class EquipConverter : ObjectCodec<Equip>
{
    protected override Equip Read(JsonElement e, Codecs o) =>
        new(e.Get<PawnId>("Pawn", o), e.Str("Thing"), e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, Equip v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class ExcavationConverter : ObjectCodec<Excavation>
{
    protected override Excavation Read(JsonElement e, Codecs o) => new(e.Get<Cell>("Cell", o), e.Str("Definition"));

    protected override void WriteFields(Utf8JsonWriter w, Excavation v, Codecs o)
    {
        w.Put("Cell", v.Cell, o);
        w.WriteString("Definition", v.Definition);
    }
}

public sealed class FloorRemovalConverter : ObjectCodec<FloorRemoval>
{
    protected override FloorRemoval Read(JsonElement e, Codecs o) => new(e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, FloorRemoval v, Codecs o)
    {
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class FoodPolicyConverter : ObjectCodec<FoodPolicy>
{
    protected override FoodPolicy Read(JsonElement e, Codecs o) => new(e.Str("Name"), e.Get<EquatableArray<string>>("Definitions", o));

    protected override void WriteFields(Utf8JsonWriter w, FoodPolicy v, Codecs o)
    {
        w.WriteString("Name", v.Name);
        w.Put("Definitions", v.Definitions, o);
    }
}

public sealed class FoundationRemovalConverter : ObjectCodec<FoundationRemoval>
{
    protected override FoundationRemoval Read(JsonElement e, Codecs o) => new(e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, FoundationRemoval v, Codecs o)
    {
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class GatheringConverter : ObjectCodec<Gathering>
{
    protected override Gathering Read(JsonElement e, Codecs o) => new(e.Str("Def"), e.Get<PawnId>("Organizer", o));

    protected override void WriteFields(Utf8JsonWriter w, Gathering v, Codecs o)
    {
        w.WriteString("Def", v.Def);
        w.Put("Organizer", v.Organizer, o);
    }
}

public sealed class GearReplaceConverter : ObjectCodec<GearReplace>
{
    protected override GearReplace Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Str("Thing"), e.Str("Definition"));

    protected override void WriteFields(Utf8JsonWriter w, GearReplace v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
    }
}

public sealed class GiveItemConverter : ObjectCodec<GiveItem>
{
    protected override GiveItem Read(JsonElement e, Codecs o) =>
        new(e.Get<PawnId>("Hauler", o), e.Get<PawnId>("Recipient", o), e.Str("Definition"), e.I64("ExpectedRemaining"));

    protected override void WriteFields(Utf8JsonWriter w, GiveItem v, Codecs o)
    {
        w.Put("Hauler", v.Hauler, o);
        w.Put("Recipient", v.Recipient, o);
        w.WriteString("Definition", v.Definition);
        w.WriteNumber("ExpectedRemaining", v.ExpectedRemaining);
    }
}

public sealed class GrowerCropConverter : ObjectCodec<GrowerCrop>
{
    protected override GrowerCrop Read(JsonElement e, Codecs o) => new(e.Str("Thing"), e.Str("Crop"));

    protected override void WriteFields(Utf8JsonWriter w, GrowerCrop v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Crop", v.Crop);
    }
}

public sealed class HackDesignationConverter : ObjectCodec<HackDesignation>
{
    protected override HackDesignation Read(JsonElement e, Codecs o) => new(e.Str("Target"), e.Bool("Enabled"));

    protected override void WriteFields(Utf8JsonWriter w, HackDesignation v, Codecs o)
    {
        w.WriteString("Target", v.Target);
        w.WriteBoolean("Enabled", v.Enabled);
    }
}

public sealed class HaulConverter : ObjectCodec<Haul>
{
    protected override Haul Read(JsonElement e, Codecs o) =>
        new(e.Get<PawnId>("Pawn", o), e.Str("Thing"), e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, Haul v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class HusbandryConverter : ObjectCodec<Husbandry>
{
    protected override Husbandry Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Animal", o), e.Get<HusbandryMethod>("Method", o), e.StrOr("Argument"));

    protected override void WriteFields(Utf8JsonWriter w, Husbandry v, Codecs o)
    {
        w.Put("Animal", v.Animal, o);
        w.Put("Method", v.Method, o);
        w.WriteString("Argument", v.Argument);
    }
}
