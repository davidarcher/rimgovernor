using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

// Hand-written converters for the action payload records (I to Z) and the nested value records they carry.

public sealed class IdeoligionDesignConverter : ObjectCodec<IdeoligionDesign>
{
    protected override IdeoligionDesign Read(JsonElement e, Codecs o) =>
        new(e.Get<EquatableArray<string>>("Memes", o), e.Get<EquatableArray<string>>("Precepts", o), e.Bool("Fluid"));

    protected override void WriteFields(Utf8JsonWriter w, IdeoligionDesign v, Codecs o)
    {
        w.Put("Memes", v.Memes, o);
        w.Put("Precepts", v.Precepts, o);
        w.WriteBoolean("Fluid", v.Fluid);
    }
}

public sealed class IdeoligionReformConverter : ObjectCodec<IdeoligionReform>
{
    protected override IdeoligionReform Read(JsonElement e, Codecs o) =>
        new(e.Str("Ideo"), e.Get<IdeoligionDesign>("Expected", o), e.Get<IdeoligionDesign>("Design", o), e.I32("Count"));

    protected override void WriteFields(Utf8JsonWriter w, IdeoligionReform v, Codecs o)
    {
        w.WriteString("Ideo", v.IdeoId);
        w.Put("Expected", v.Expected, o);
        w.Put("Design", v.Design, o);
        w.WriteNumber("Count", v.Count);
    }
}

public sealed class IgniteConverter : ObjectCodec<Ignite>
{
    protected override Ignite Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, Ignite v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class MoodReliefConverter : ObjectCodec<MoodRelief>
{
    protected override MoodRelief Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Get<MoodReliefNeed>("Need", o));

    protected override void WriteFields(Utf8JsonWriter w, MoodRelief v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Need", v.Need, o);
    }
}

public sealed class MoveBuildingConverter : ObjectCodec<MoveBuilding>
{
    protected override MoveBuilding Read(JsonElement e, Codecs o) =>
        new(e.Str("Thing"), e.Str("Definition"), e.Get<Cell>("Cell", o), e.Get<Rotation>("Rotation", o));

    protected override void WriteFields(Utf8JsonWriter w, MoveBuilding v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
        w.Put("Rotation", v.Rotation, o);
    }
}

public sealed class MovementConverter : ObjectCodec<Movement>
{
    protected override Movement Read(JsonElement e, Codecs o) =>
        new(e.Get<PawnId>("Pawn", o), e.Get<Cell>("Destination", o), e.Get<ActionId>("DraftAction", o));

    protected override void WriteFields(Utf8JsonWriter w, Movement v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Destination", v.Destination, o);
        w.Put("DraftAction", v.DraftAction, o);
    }
}

public sealed class OpenCasketConverter : ObjectCodec<OpenCasket>
{
    protected override OpenCasket Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Str("Casket"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, OpenCasket v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Casket", v.Casket);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class OwnedDraftConverter : ObjectCodec<OwnedDraft>
{
    protected override OwnedDraft Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o));

    protected override void WriteFields(Utf8JsonWriter w, OwnedDraft v, Codecs o) => w.Put("Pawn", v.Pawn, o);
}

public sealed class PawnSettingsConverter : ObjectCodec<PawnSettings>
{
    protected override PawnSettings Read(JsonElement e, Codecs o)
    {
        var pawn = e.Get<PawnId>("Pawn", o);
        return e.Get<SettingKind>("Kind", o) switch
        {
            SettingKind.Hostility => PawnSettings.NewHostilitySetting(pawn, e.Get<HostilityResponse>("Hostility", o)),
            SettingKind.SelfTend => PawnSettings.NewSelfTendSetting(pawn, e.Bool("SelfTend")),
            SettingKind.MedicineCarry => PawnSettings.NewMedicineCarrySetting(pawn, e.I32("MedicineCarry")),
            SettingKind.Nickname => PawnSettings.NewNicknameSetting(pawn, e.Str("LeaveName")),
            SettingKind.MedicalCare => PawnSettings.NewMedicalCareSetting(pawn, e.Get<MedicalCare>("MedicalCare", o)),
            SettingKind.ReadingPolicy => PawnSettings.NewReadingPolicySetting(pawn, e.Str("ReadingPolicy")),
            SettingKind.DrugPolicy => PawnSettings.NewDrugPolicySetting(pawn, e.Str("DrugPolicy")),
            SettingKind.FoodPolicy => PawnSettings.NewFoodPolicySetting(pawn, e.Str("FoodPolicy")),
            SettingKind.MechWorkMode => PawnSettings.NewMechWorkModeSetting(pawn, e.Str("MechWorkMode")),
            SettingKind.MechControlGroup => PawnSettings.NewMechControlGroupSetting(pawn, e.I32("MechControlGroup")),
            SettingKind.ChoosePermit => PawnSettings.NewChoosePermitSetting(pawn, e.Str("PermitFaction"), e.Str("PermitDef")),
            SettingKind.ExtractBioferrite => PawnSettings.NewExtractBioferriteSetting(pawn, e.Bool("ExtractBioferrite")),
            SettingKind.EatTrigger => PawnSettings.NewEatTriggerSetting(pawn, e.Dbl("EatTrigger")),
            _ => throw new StoreCorruptException("unknown pawn setting kind"),
        };
    }

    protected override void WriteFields(Utf8JsonWriter w, PawnSettings v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Kind", v.Kind, o);
        switch (v.Kind)
        {
            case SettingKind.Hostility:
                w.Put("Hostility", v.Hostility!.Value, o);
                break;
            case SettingKind.SelfTend:
                w.WriteBoolean("SelfTend", v.SelfTend);
                break;
            case SettingKind.MedicineCarry:
                w.WriteNumber("MedicineCarry", v.MedicineCarry!.Value);
                break;
            case SettingKind.Nickname:
                w.WriteString("LeaveName", v.LeaveName);
                break;
            case SettingKind.MedicalCare:
                w.Put("MedicalCare", v.MedicalCare!.Value, o);
                break;
            case SettingKind.ReadingPolicy:
                w.WriteString("ReadingPolicy", v.ReadingPolicy);
                break;
            case SettingKind.DrugPolicy:
                w.WriteString("DrugPolicy", v.DrugPolicy);
                break;
            case SettingKind.FoodPolicy:
                w.WriteString("FoodPolicy", v.FoodPolicy);
                break;
            case SettingKind.MechWorkMode:
                w.WriteString("MechWorkMode", v.MechWorkMode);
                break;
            case SettingKind.MechControlGroup:
                w.WriteNumber("MechControlGroup", v.MechControlGroup!.Value);
                break;
            case SettingKind.ChoosePermit:
                w.WriteString("PermitFaction", v.ChoosePermit!.Value.Faction);
                w.WriteString("PermitDef", v.ChoosePermit!.Value.Permit);
                break;
            case SettingKind.ExtractBioferrite:
                w.WriteBoolean("ExtractBioferrite", v.ExtractBioferrite!.Value);
                break;
            case SettingKind.EatTrigger:
                w.WriteNumber("EatTrigger", v.EatTrigger!.Value);
                break;
            default:
                throw new StoreCorruptException("unknown pawn setting kind");
        }
    }
}

public sealed class PolicyPruneConverter : ObjectCodec<PolicyPrune>
{
    protected override PolicyPrune Read(JsonElement e, Codecs o) => new(e.Get<PolicyDatabase>("Database", o), e.Get<EquatableArray<string>>("Ids", o));

    protected override void WriteFields(Utf8JsonWriter w, PolicyPrune v, Codecs o)
    {
        w.Put("Database", v.Database, o);
        w.Put("Ids", v.Ids, o);
    }
}

public sealed class PrisonerInteractionConverter : ObjectCodec<PrisonerInteraction>
{
    protected override PrisonerInteraction Read(JsonElement e, Codecs o) =>
        new(e.Get<PawnId>("Pawn", o), e.Get<PrisonerInteractionMode>("Interaction", o));

    protected override void WriteFields(Utf8JsonWriter w, PrisonerInteraction v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Interaction", v.Interaction, o);
    }
}

public sealed class ProductionBillConverter : ObjectCodec<ProductionBill>
{
    protected override ProductionBill Read(JsonElement e, Codecs o)
    {
        var mode = e.Get<BillMode>("Mode", o);
        var bench = e.Str("Bench");
        var recipe = e.Str("Recipe");
        var worker = e.StrOr("Worker");
        var bill = mode == BillMode.HumanButcherForever
            ? ProductionBill.NewHumanButcherBill(bench, recipe, worker)
            : new ProductionBill(bench, recipe, mode, e.I32("Target"), [.. e.Get<EquatableArray<string>>("Ingredients", o)]);
        if (mode == BillMode.GearBatch && worker.Length != 0)
        {
            bill = bill.PinWorker(worker);
        }
        var replaces = e.StrOr("Replaces");
        return replaces.Length != 0 ? bill.ReplaceOwnedBill(replaces) : bill;
    }

    protected override void WriteFields(Utf8JsonWriter w, ProductionBill v, Codecs o)
    {
        w.WriteString("Bench", v.Bench);
        w.WriteString("Recipe", v.Recipe);
        w.Put("Mode", v.Mode, o);
        w.WriteNumber("Target", v.Target);
        w.Put("Ingredients", v.Ingredients, o);
        w.WriteString("Worker", v.Worker);
        w.WriteString("Replaces", v.Replaces);
    }
}

public sealed class QuestAcceptConverter : ObjectCodec<QuestAccept>
{
    protected override QuestAccept Read(JsonElement e, Codecs o) =>
        new(e.Get<QuestId>("Quest", o), e.Get<PawnId>("AccepterPawn", o), e.I32("RewardChoice"));

    protected override void WriteFields(Utf8JsonWriter w, QuestAccept v, Codecs o)
    {
        w.Put("Quest", v.Quest, o);
        w.Put("AccepterPawn", v.AccepterPawn, o);
        w.WriteNumber("RewardChoice", v.RewardChoice);
    }
}

public sealed class QuestShuttleConverter : ObjectCodec<QuestShuttle>
{
    protected override QuestShuttle Read(JsonElement e, Codecs o) =>
        new(e.Get<QuestId>("Quest", o), e.Get<ShuttleLoading>("Loading", o), e.Bool("Autoload"), e.Get<EquatableArray<PawnId>>("Pawns", o), e.Bool("Launch"));

    protected override void WriteFields(Utf8JsonWriter w, QuestShuttle v, Codecs o)
    {
        w.Put("Quest", v.Quest, o);
        w.Put("Loading", v.Loading, o);
        w.WriteBoolean("Autoload", v.Autoload);
        w.Put("Pawns", v.Pawns, o);
        w.WriteBoolean("Launch", v.Launch);
    }
}

public sealed class ReadingPolicyConverter : ObjectCodec<ReadingPolicy>
{
    protected override ReadingPolicy Read(JsonElement e, Codecs o) => new(e.Str("Name"), e.Get<EquatableArray<string>>("Definitions", o));

    protected override void WriteFields(Utf8JsonWriter w, ReadingPolicy v, Codecs o)
    {
        w.WriteString("Name", v.Name);
        w.Put("Definitions", v.Definitions, o);
    }
}

public sealed class RecoveryServiceConverter : ObjectCodec<RecoveryService>
{
    protected override RecoveryService Read(JsonElement e, Codecs o) =>
        new(e.Get<PawnId>("Pawn", o), e.Str("Thing"), e.Get<RecoveryMethod>("Method", o));

    protected override void WriteFields(Utf8JsonWriter w, RecoveryService v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Thing", v.Thing);
        w.Put("Method", v.Method, o);
    }
}

public sealed class RemoveProductionBillConverter : ObjectCodec<RemoveProductionBill>
{
    protected override RemoveProductionBill Read(JsonElement e, Codecs o)
    {
        var remove = new RemoveProductionBill(e.Str("Bench"), e.Str("Bill"));
        return e.BoolOr("CancelsUnfinished") ? remove.CancelUnfinished() : remove;
    }

    protected override void WriteFields(Utf8JsonWriter w, RemoveProductionBill v, Codecs o)
    {
        w.WriteString("Bench", v.Bench);
        w.WriteString("Bill", v.Bill);
        w.WriteBoolean("CancelsUnfinished", v.CancelsUnfinished);
    }
}

public sealed class RepairConverter : ObjectCodec<Repair>
{
    protected override Repair Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Str("Structure"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, Repair v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Structure", v.Structure);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class RescueConverter : ObjectCodec<Rescue>
{
    protected override Rescue Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Rescuer", o), e.Get<PawnId>("Patient", o));

    protected override void WriteFields(Utf8JsonWriter w, Rescue v, Codecs o)
    {
        w.Put("Rescuer", v.Rescuer, o);
        w.Put("Patient", v.Patient, o);
    }
}

public sealed class ResearchSelectConverter : ObjectCodec<ResearchSelect>
{
    protected override ResearchSelect Read(JsonElement e, Codecs o) => new(e.Str("Project"));

    protected override void WriteFields(Utf8JsonWriter w, ResearchSelect v, Codecs o) => w.WriteString("Project", v.Project);
}

public sealed class RitualKindConverter() : StringCodec<RitualKind>(s => new RitualKind(s), v => v.Value);

public sealed class RitualSlotConverter : ObjectCodec<RitualSlot>
{
    protected override RitualSlot Read(JsonElement e, Codecs o) => new(e.Str("Slot"), e.Get<EquatableArray<PawnId>>("Pawns", o));

    protected override void WriteFields(Utf8JsonWriter w, RitualSlot v, Codecs o)
    {
        w.WriteString("Slot", v.Slot);
        w.Put("Pawns", v.Pawns, o);
    }
}

public sealed class RitualConverter : ObjectCodec<Ritual>
{
    protected override Ritual Read(JsonElement e, Codecs o)
    {
        var pawn = e.Get<PawnId>("Pawn", o);
        var kind = e.Get<RitualKind>("Kind", o);
        return e.Get<RitualVerb>("Verb", o) switch
        {
            RitualVerb.Start => new Ritual(pawn, kind, RitualVerb.Start),
            RitualVerb.Begin => Ritual.NewRitualBegin(pawn, kind.Value, e.Get<Cell>("Spot", o), e.Get<EquatableArray<RitualSlot>>("Slots", o), e.Get<EquatableArray<PawnId>>("Spectators", o)),
            _ => throw new StoreCorruptException("unknown ritual verb"),
        };
    }

    protected override void WriteFields(Utf8JsonWriter w, Ritual v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Kind", v.Kind, o);
        w.Put("Verb", v.Verb, o);
        if (v.Verb == RitualVerb.Begin)
        {
            w.Put("Spot", v.Spot, o);
            w.Put("Slots", v.Slots, o);
            w.Put("Spectators", v.Spectators, o);
        }
    }
}

public sealed class RuleConverter : ObjectCodec<Rule>
{
    protected override Rule Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Id"),
            e.Get<RuleTrigger>("Trigger", o),
            e.Get<EquatableArray<RulePredicate>>("Predicates", o),
            e.Get<RuleActionKind>("Action", o),
            e.Str("Job"),
            e.Get<RuleTarget>("Target", o),
            e.Get<uint>("Radius", o),
            e.Get<float>("PredatorMarginCells", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, Rule v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.Put("Trigger", v.Trigger, o);
        w.Put("Predicates", v.Predicates, o);
        w.Put("Action", v.Action, o);
        w.WriteString("Job", v.Job);
        w.Put("Target", v.Target, o);
        w.WriteNumber("Radius", v.Radius);
        w.WriteNumber("PredatorMarginCells", v.PredatorMarginCells);
    }
}

public sealed class RulesAttachConverter : ObjectCodec<RulesAttach>
{
    protected override RulesAttach Read(JsonElement e, Codecs o) => new(e.Get<EquatableArray<Rule>>("Rules", o), e.I64("LeaseTicks"));

    protected override void WriteFields(Utf8JsonWriter w, RulesAttach v, Codecs o)
    {
        w.Put("Rules", v.Rules, o);
        w.WriteNumber("LeaseTicks", v.LeaseTicks);
    }
}

public sealed class FilterSelectorConverter : ObjectCodec<FilterSelector>
{
    protected override FilterSelector Read(JsonElement e, Codecs o) => new(e.Get<SelectorKind>("Kind", o), e.Str("Name"));

    protected override void WriteFields(Utf8JsonWriter w, FilterSelector v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.WriteString("Name", v.Name);
    }
}

public sealed class HitPointRangeConverter : ObjectCodec<HitPointRange>
{
    protected override HitPointRange Read(JsonElement e, Codecs o) => new(e.Dbl("Min"), e.Dbl("Max"));

    protected override void WriteFields(Utf8JsonWriter w, HitPointRange v, Codecs o)
    {
        w.WriteNumber("Min", v.Min);
        w.WriteNumber("Max", v.Max);
    }
}

public sealed class QualityRangeConverter : ObjectCodec<QualityRange>
{
    protected override QualityRange Read(JsonElement e, Codecs o) => new(e.Get<Quality>("Min", o), e.Get<Quality>("Max", o));

    protected override void WriteFields(Utf8JsonWriter w, QualityRange v, Codecs o)
    {
        w.Put("Min", v.Min, o);
        w.Put("Max", v.Max, o);
    }
}

public sealed class StockpileFilterConverter : ObjectCodec<StockpileFilter>
{
    protected override StockpileFilter Read(JsonElement e, Codecs o)
    {
        var filter = new StockpileFilter(e.Get<FilterBase>("Base", o), e.Get<EquatableArray<FilterSelector>>("Allow", o), e.Get<EquatableArray<FilterSelector>>("Disallow", o));
        if (e.Has("HitPoints"))
        {
            var hp = e.Get<HitPointRange>("HitPoints", o);
            filter = filter.WithHitPoints(hp.Min, hp.Max);
        }
        if (e.Has("Quality"))
        {
            var q = e.Get<QualityRange>("Quality", o);
            filter = filter.WithQuality(q.Min, q.Max);
        }
        return filter;
    }

    protected override void WriteFields(Utf8JsonWriter w, StockpileFilter v, Codecs o)
    {
        w.Put("Base", v.Base, o);
        w.Put("Allow", v.Allow, o);
        w.Put("Disallow", v.Disallow, o);
        if (v.HitPoints is { } hp)
        {
            w.Put("HitPoints", hp, o);
        }
        if (v.QualityRange is { } q)
        {
            w.Put("Quality", q, o);
        }
    }
}

public sealed class GroundRectConverter : ObjectCodec<GroundRect>
{
    protected override GroundRect Read(JsonElement e, Codecs o) => new(e.Get<Cell>("Origin", o), e.I32("Width"), e.I32("Height"));

    protected override void WriteFields(Utf8JsonWriter w, GroundRect v, Codecs o)
    {
        w.Put("Origin", v.Origin, o);
        w.WriteNumber("Width", v.Width);
        w.WriteNumber("Height", v.Height);
    }
}

public sealed class ZoneCellEditConverter : ObjectCodec<ZoneCellEdit>
{
    protected override ZoneCellEdit Read(JsonElement e, Codecs o) =>
        new(e.Str("Zone"), e.Get<CellEditMode>("Mode", o), e.Get<EquatableArray<Cell>>("Cells", o));

    protected override void WriteFields(Utf8JsonWriter w, ZoneCellEdit v, Codecs o)
    {
        w.WriteString("Zone", v.Zone);
        w.Put("Mode", v.Mode, o);
        w.Put("Cells", v.Cells, o);
    }
}

public sealed class StockpilePatchConverter : ObjectCodec<StockpilePatch>
{
    protected override StockpilePatch Read(JsonElement e, Codecs o) =>
        new(e.Get<StorageTargetKind>("TargetKind", o), e.Str("Target"), e.Get<StockpileFilter>("Filter", o), e.Get<StockpilePriority>("Priority", o), e.StrOr("Role"));

    protected override void WriteFields(Utf8JsonWriter w, StockpilePatch v, Codecs o)
    {
        w.Put("TargetKind", v.TargetKind, o);
        w.WriteString("Target", v.Target);
        w.Put("Filter", v.Filter, o);
        w.Put("Priority", v.Priority, o);
        w.WriteString("Role", v.Role);
    }
}

public sealed class StripConverter : ObjectCodec<Strip>
{
    protected override Strip Read(JsonElement e, Codecs o) => new(e.Str("Target"));

    protected override void WriteFields(Utf8JsonWriter w, Strip v, Codecs o) => w.WriteString("Target", v.Target);
}

public sealed class SubdueConverter : ObjectCodec<Subdue>
{
    protected override Subdue Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Get<PawnId>("Target", o), e.Get<ActionId>("DraftAction", o));

    protected override void WriteFields(Utf8JsonWriter w, Subdue v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Target", v.Target, o);
        w.Put("DraftAction", v.DraftAction, o);
    }
}

public sealed class SupplyAllowConverter : ObjectCodec<SupplyAllow>
{
    protected override SupplyAllow Read(JsonElement e, Codecs o) =>
        e.BoolOr("Forbidden")
            ? SupplyAllow.NewSupplyForbid(e.Str("Thing"), e.Str("Definition"), e.Get<Cell>("Cell", o))
            : new SupplyAllow(e.Str("Thing"), e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, SupplyAllow v, Codecs o)
    {
        w.WriteBoolean("Forbidden", v.Forbidden);
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class SurgeryConverter : ObjectCodec<Surgery>
{
    protected override Surgery Read(JsonElement e, Codecs o)
    {
        var surgery = new Surgery(e.Get<PawnId>("Pawn", o), e.Str("Recipe"), e.I32("Part"), e.Bool("AcknowledgeViolation"));
        return e.Has("Surgeon") ? surgery.WithSurgeon(e.Get<PawnId>("Surgeon", o)) : surgery;
    }

    protected override void WriteFields(Utf8JsonWriter w, Surgery v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Recipe", v.Recipe);
        w.WriteNumber("Part", v.Part);
        w.WriteBoolean("AcknowledgeViolation", v.AcknowledgeViolation);
        if (v.Surgeon is { } surgeon)
        {
            w.Put("Surgeon", surgeon, o);
        }
    }
}

public sealed class TendConverter : ObjectCodec<Tend>
{
    protected override Tend Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Doctor", o), e.Get<PawnId>("Patient", o));

    protected override void WriteFields(Utf8JsonWriter w, Tend v, Codecs o)
    {
        w.Put("Doctor", v.Doctor, o);
        w.Put("Patient", v.Patient, o);
    }
}

public sealed class TradeParticipantConverter : ObjectCodec<TradeParticipant>
{
    protected override TradeParticipant Read(JsonElement e, Codecs o) => new(e.Get<TradeParticipantKind>("Kind", o), e.Str("Id"), e.StrOr("Caravan"));

    protected override void WriteFields(Utf8JsonWriter w, TradeParticipant v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.WriteString("Id", v.Id);
        w.WriteString("Caravan", v.Caravan);
    }
}

public sealed class TradeLineConverter : ObjectCodec<TradeLine>
{
    protected override TradeLine Read(JsonElement e, Codecs o) => new(e.Str("LineId"), e.I32("AbsoluteCount"));

    protected override void WriteFields(Utf8JsonWriter w, TradeLine v, Codecs o)
    {
        w.WriteString("LineId", v.LineId);
        w.WriteNumber("AbsoluteCount", v.AbsoluteCount);
    }
}

public sealed class TradeConverter : ObjectCodec<Trade>
{
    protected override Trade Read(JsonElement e, Codecs o)
    {
        var participant = e.Get<TradeParticipant>("Participant", o);
        var negotiator = e.Get<PawnId>("Negotiator", o);
        var trade = e.Get<TradeOperationKind>("Kind", o) switch
        {
            TradeOperationKind.Open => Trade.NewTradeOpen(participant.Id, negotiator, e.BoolOr("GiftMode")),
            TradeOperationKind.SetLines => Trade.NewTradeSetLines(participant.Id, negotiator, e.Get<EquatableArray<TradeLine>>("Lines", o), e.BoolOr("AllowPawns")),
            TradeOperationKind.Accept => Trade.NewTradeAccept(participant.Id, negotiator, e.Str("ExpectedDealSignature"), e.BoolOr("AllowEmpty"), e.BoolOr("ReceiveQuest")),
            TradeOperationKind.End => Trade.NewTradeEnd(participant.Id, negotiator, e.Get<TradeEndKind>("EndKind", o), e.BoolOr("ReceiveQuest")),
            _ => throw new StoreCorruptException("unknown trade operation"),
        };
        return trade.WithParticipant(participant);
    }

    protected override void WriteFields(Utf8JsonWriter w, Trade v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.Put("Participant", v.Participant, o);
        w.Put("Negotiator", v.Negotiator, o);
        w.WriteBoolean("GiftMode", v.GiftMode);
        w.Put("Lines", v.Lines, o);
        w.WriteBoolean("AllowPawns", v.AllowPawns);
        w.WriteString("ExpectedDealSignature", v.ExpectedDealSignature);
        w.WriteBoolean("AllowEmpty", v.AllowEmpty);
        w.Put("EndKind", v.EndKind, o);
        w.WriteBoolean("ReceiveQuest", v.ReceiveQuest);
    }
}

public sealed class UseItemConverter : ObjectCodec<UseItem>
{
    protected override UseItem Read(JsonElement e, Codecs o) => new(e.Get<PawnId>("Pawn", o), e.Str("Item"), e.Get<PawnId>("Target", o));

    protected override void WriteFields(Utf8JsonWriter w, UseItem v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Item", v.Item);
        w.Put("Target", v.Target, o);
    }
}

public sealed class WallRemovalConverter : ObjectCodec<WallRemoval>
{
    protected override WallRemoval Read(JsonElement e, Codecs o) => new(e.Str("Original"), e.Get<ActionId>("BackupOf", o), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, WallRemoval v, Codecs o)
    {
        w.WriteString("Original", v.Original);
        w.Put("BackupOf", v.BackupOf, o);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class WastepackHaulConverter : ObjectCodec<WastepackHaul>
{
    protected override WastepackHaul Read(JsonElement e, Codecs o) => new(e.Str("Thing"), e.Str("Definition"), e.Get<Cell>("Cell", o));

    protected override void WriteFields(Utf8JsonWriter w, WastepackHaul v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
    }
}

public sealed class WorkSettingConverter : ObjectCodec<WorkSetting>
{
    protected override WorkSetting Read(JsonElement e, Codecs o) => new(e.Str("Definition"), e.I32("Priority"));

    protected override void WriteFields(Utf8JsonWriter w, WorkSetting v, Codecs o)
    {
        w.WriteString("Definition", v.Definition);
        w.WriteNumber("Priority", v.Priority);
    }
}

public sealed class WorkAssignmentConverter : ObjectCodec<WorkAssignment>
{
    protected override WorkAssignment Read(JsonElement e, Codecs o)
    {
        var pawn = e.Get<PawnId>("Pawn", o);
        if (e.BoolOr("HasArea"))
        {
            return WorkAssignment.NewAreaAssignment(pawn, e.Bool("AreaClear"), e.StrOr("Area"));
        }
        var settings = e.Get<EquatableArray<WorkSetting>>("Settings", o);
        var schedule = e.Get<EquatableArray<string>>("Schedule", o);
        return schedule.Count > 0 ? WorkAssignment.NewScheduleAssignment(pawn, settings, [.. schedule]) : WorkAssignment.NewWorkAssignment(pawn, settings);
    }

    protected override void WriteFields(Utf8JsonWriter w, WorkAssignment v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Settings", v.Settings, o);
        w.WriteBoolean("HasArea", v.HasArea);
        w.WriteBoolean("AreaClear", v.AreaClear);
        w.WriteString("Area", v.Area);
        w.Put("Schedule", v.Schedule, o);
    }
}

public sealed class ZoneCreateConverter : ObjectCodec<ZoneCreate>
{
    protected override ZoneCreate Read(JsonElement e, Codecs o)
    {
        var zone = e.Get<ZoneKind>("Kind", o) switch
        {
            ZoneKind.Growing => new ZoneCreate(ZoneKind.Growing, e.Str("Crop"), e.Get<EquatableArray<Cell>>("Cells", o)),
            ZoneKind.Fishing => e.StrOr("ExtendId").Length != 0
                ? ZoneCreate.NewFishingZoneExtension(e.Str("ExtendId"), e.Get<EquatableArray<Cell>>("Cells", o))
                : ZoneCreate.NewFishingZone(e.Get<EquatableArray<Cell>>("Cells", o)),
            ZoneKind.Stockpile => e.Has("Rectangle")
                ? ZoneCreate.NewFilteredStockpileZone(e.Get<StockpileFilter>("Filter", o), e.Get<StockpilePriority>("Priority", o), e.Get<GroundRect>("Rectangle", o))
                : ZoneCreate.NewFilteredStockpileCellsZone(e.Get<StockpileFilter>("Filter", o), e.Get<StockpilePriority>("Priority", o), e.Get<EquatableArray<Cell>>("Cells", o)),
            _ => throw new StoreCorruptException("unknown zone kind"),
        };
        var role = e.StrOr("Role");
        return role.Length != 0 ? zone.WithRole(role) : zone;
    }

    protected override void WriteFields(Utf8JsonWriter w, ZoneCreate v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.WriteString("Crop", v.Crop);
        if (v.Priority is { } priority)
        {
            w.Put("Priority", priority, o);
        }
        if (v.Rectangle is { } rectangle)
        {
            w.Put("Rectangle", rectangle, o);
        }
        else
        {
            w.Put("Cells", v.Cells, o);
        }
        if (v.Filter is { } filter)
        {
            w.Put("Filter", filter, o);
        }
        w.WriteString("Role", v.Role);
        w.WriteString("ExtendId", v.ExtendId);
    }
}

public sealed class ZoneDeleteConverter : ObjectCodec<ZoneDelete>
{
    protected override ZoneDelete Read(JsonElement e, Codecs o) => new(e.Str("Zone"));

    protected override void WriteFields(Utf8JsonWriter w, ZoneDelete v, Codecs o) => w.WriteString("Zone", v.Zone);
}

public sealed class BuildingConverter : ObjectCodec<Building>
{
    protected override Building Read(JsonElement e, Codecs o) =>
        new(e.Str("Definition"), e.Get<Cell>("Cell", o), e.Get<Rotation>("Rotation", o), e.StrOr("Stuff"));

    protected override void WriteFields(Utf8JsonWriter w, Building v, Codecs o)
    {
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
        w.Put("Rotation", v.Rotation, o);
        w.WriteString("Stuff", v.Stuff);
    }
}
