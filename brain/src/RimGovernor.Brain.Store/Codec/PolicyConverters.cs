using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store.Codec;

internal sealed class AdmissionConverter : ObjectCodec<global::RimGovernor.Brain.Policy.Admission>
{
    protected override global::RimGovernor.Brain.Policy.Admission Read(JsonElement e, Codecs o) =>
        new()
        {
            Proposal = e.GetOr<SafeguardProposal?>("Proposal", null, o),
            Action = e.GetOr<DomainAction?>("Action", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, global::RimGovernor.Brain.Policy.Admission v, Codecs o)
    {
        if (v.Proposal is { } vProposal)
        {
            w.Put("Proposal", vProposal, o);
        }
        if (v.Action is { } vAction)
        {
            w.Put("Action", vAction, o);
        }
    }
}

internal sealed class StoreAdmissionConverter : ObjectCodec<global::RimGovernor.Brain.Store.Admission>
{
    protected override global::RimGovernor.Brain.Store.Admission Read(JsonElement e, Codecs o) =>
        new(
            e.Get<GenerationSnapshot>("Snapshot", o),
            e.Get<Tick>("Tick", o),
            e.Get<EquatableArray<Cell>>("Footprint", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, global::RimGovernor.Brain.Store.Admission v, Codecs o)
    {
        w.Put("Snapshot", v.Snapshot, o);
        w.Put("Tick", v.Tick, o);
        w.Put("Footprint", v.Footprint, o);
    }
}

internal sealed class AnimalOrderConverter : ObjectCodec<AnimalOrder>
{
    protected override AnimalOrder Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            Kind = e.Get<CombatOrderKind>("Kind", o),
            Cell = e.Get<Cell>("Cell", o),
            Target = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Target", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, AnimalOrder v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Kind", v.Kind, o);
        w.Put("Cell", v.Cell, o);
        w.Put("Target", v.Target, o);
    }
}

internal sealed class AnimalUpkeepHistoryConverter : ObjectCodec<AnimalUpkeepHistory>
{
    protected override AnimalUpkeepHistory Read(JsonElement e, Codecs o) =>
        new()
        {
            Containment = e.Bool("Containment"),
        };

    protected override void WriteFields(Utf8JsonWriter w, AnimalUpkeepHistory v, Codecs o)
    {
        w.WriteBoolean("Containment", v.Containment);
    }
}

internal sealed class BlockedReasonConverter() : StringCodec<BlockedReason>(s => new(s), v => v.Value);
internal sealed class CastMarkConverter : ObjectCodec<CastMark>
{
    protected override CastMark Read(JsonElement e, Codecs o) =>
        new(
            e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            e.Str("Def"),
            e.Get<Tick>("Tick", o),
            e.Dbl("Psyfocus"),
            e.Dbl("Entropy"),
            e.I32("Cooldown")
        );

    protected override void WriteFields(Utf8JsonWriter w, CastMark v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Def", v.Def);
        w.Put("Tick", v.Tick, o);
        w.WriteNumber("Psyfocus", v.Psyfocus);
        w.WriteNumber("Entropy", v.Entropy);
        w.WriteNumber("Cooldown", v.Cooldown);
    }
}

internal sealed class CauseConverter() : StringCodec<Cause>(s => new(s), v => v.Value);
internal sealed class ColonyStageRecordConverter : ObjectCodec<ColonyStageRecord>
{
    protected override ColonyStageRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            Stage = e.Get<ColonyStage>("Stage", o),
            Since = e.Get<Tick>("Since", o),
            Blocker = e.Get<StageBlocker>("Blocker", o),
            Reason = e.Str("Reason"),
            Held = e.Bool("Held"),
            ProductionBlocked = e.Bool("ProductionBlocked"),
            ProductionSince = e.Get<Tick>("ProductionSince", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ColonyStageRecord v, Codecs o)
    {
        w.Put("Stage", v.Stage, o);
        w.Put("Since", v.Since, o);
        w.Put("Blocker", v.Blocker, o);
        w.WriteString("Reason", v.Reason);
        w.WriteBoolean("Held", v.Held);
        w.WriteBoolean("ProductionBlocked", v.ProductionBlocked);
        w.Put("ProductionSince", v.ProductionSince, o);
    }
}

internal sealed class CombatAnimalRestorationConverter : ObjectCodec<CombatAnimalRestoration>
{
    protected override CombatAnimalRestoration Read(JsonElement e, Codecs o) =>
        new(
            e.Get<global::RimGovernor.Brain.Domain.PawnId>("Pawn", o),
            e.Str("Area")
        );

    protected override void WriteFields(Utf8JsonWriter w, CombatAnimalRestoration v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Area", v.Area);
    }
}

internal sealed class CombatBurnConverter : ObjectCodec<CombatBurn>
{
    protected override CombatBurn Read(JsonElement e, Codecs o) =>
        new()
        {
            Hive = e.Get<Cell>("Hive", o),
            Exit = e.Get<Cell>("Exit", o),
            Room = e.Get<Rectangle>("Room", o),
            Carrier = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Carrier", o),
            Lit = e.Get<Tick>("Lit", o),
            Thrown = e.Get<Tick>("Thrown", o),
            Done = e.Bool("Done"),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatBurn v, Codecs o)
    {
        w.Put("Hive", v.Hive, o);
        w.Put("Exit", v.Exit, o);
        w.Put("Room", v.Room, o);
        w.Put("Carrier", v.Carrier, o);
        w.Put("Lit", v.Lit, o);
        w.Put("Thrown", v.Thrown, o);
        w.WriteBoolean("Done", v.Done);
    }
}

internal sealed class CombatDoorRestorationConverter : ObjectCodec<CombatDoorRestoration>
{
    protected override CombatDoorRestoration Read(JsonElement e, Codecs o) =>
        new(
            e.Get<Cell>("Cell", o),
            e.Bool("HoldOpen"),
            e.Bool("Forbidden")
        );

    protected override void WriteFields(Utf8JsonWriter w, CombatDoorRestoration v, Codecs o)
    {
        w.Put("Cell", v.Cell, o);
        w.WriteBoolean("HoldOpen", v.HoldOpen);
        w.WriteBoolean("Forbidden", v.Forbidden);
    }
}

internal sealed class CombatFlankConverter : ObjectCodec<CombatFlank>
{
    protected override CombatFlank Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawns = e.Get<List<global::RimGovernor.Brain.Policy.PawnId>>("Pawns", o),
            Cells = e.Get<List<Cell>>("Cells", o),
            Open = e.Bool("Open"),
            Held = e.Get<List<global::RimGovernor.Brain.Policy.PawnId>>("Held", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatFlank v, Codecs o)
    {
        w.Put("Pawns", v.Pawns, o);
        w.Put("Cells", v.Cells, o);
        w.WriteBoolean("Open", v.Open);
        w.Put("Held", v.Held, o);
    }
}

internal sealed class CombatGroupConverter : ObjectCodec<CombatGroup>
{
    protected override CombatGroup Read(JsonElement e, Codecs o) =>
        new()
        {
            Hostiles = e.Get<List<global::RimGovernor.Brain.Policy.PawnId>>("Hostiles", o),
            Pawns = e.Get<List<global::RimGovernor.Brain.Policy.PawnId>>("Pawns", o),
            Cells = e.Get<List<Cell>>("Cells", o),
            Fallback = e.Get<List<Cell>>("Fallback", o),
            FellBack = e.Bool("FellBack"),
            Centroid = e.Get<Cell>("Centroid", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatGroup v, Codecs o)
    {
        w.Put("Hostiles", v.Hostiles, o);
        w.Put("Pawns", v.Pawns, o);
        w.Put("Cells", v.Cells, o);
        w.Put("Fallback", v.Fallback, o);
        w.WriteBoolean("FellBack", v.FellBack);
        w.Put("Centroid", v.Centroid, o);
    }
}

internal sealed class CombatMemoryConverter : ObjectCodec<CombatMemory>
{
    protected override CombatMemory Read(JsonElement e, Codecs o) =>
        new()
        {
            Tactic = e.Get<CombatTactic>("Tactic", o),
            Roles = e.Get<EquatableArray<CombatRole>>("Roles", o),
            Refusal = e.Str("Refusal"),
            Formed = e.Get<Tick>("Formed", o),
            Relieved = e.Get<List<global::RimGovernor.Brain.Policy.PawnId>>("Relieved", o),
            Issued = e.Get<List<IssuedOrder>>("Issued", o),
            Tick = e.Get<Tick>("Tick", o),
            Rescue = e.GetOr<CombatRescue?>("Rescue", null, o),
            Pods = e.GetOr<PodArrival?>("Pods", null, o),
            PodDoors = e.Get<List<PodDoor>>("PodDoors", o),
            PodWait = e.Bool("PodWait"),
            PodStruck = e.Bool("PodStruck"),
            PotshotDoor = e.GetOr<PodDoor?>("PotshotDoor", null, o),
            ChokeDoor = e.GetOr<PodDoor?>("ChokeDoor", null, o),
            HeldDoors = e.Get<List<Cell>>("HeldDoors", o),
            Kiter = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Kiter", o),
            Leading = e.Bool("Leading"),
            Driller = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Driller", o),
            Wait = e.Bool("Wait"),
            WaitSince = e.Get<Tick>("WaitSince", o),
            WaitDoors = e.Get<EquatableArray<PodDoor>>("WaitDoors", o),
            WaitRooms = e.Get<EquatableArray<WaitDoor>>("WaitRooms", o),
            Scattered = e.Bool("Scattered"),
            Repairs = e.Get<List<Cell>>("Repairs", o),
            LineHeld = e.I32("LineHeld"),
            SapperBreach = e.GetOr<Cell?>("SapperBreach", null, o),
            Intercept = e.Bool("Intercept"),
            MechLure = e.Bool("MechLure"),
            Rushing = e.Bool("Rushing"),
            SiegeCamp = e.Get<Tick>("SiegeCamp", o),
            SiegeMode = e.Get<SiegeMode>("SiegeMode", o),
            SiegeMortar = e.Bool("SiegeMortar"),
            CannotHit = e.Get<List<HitRefusal>>("CannotHit", o),
            Unreachable = e.Get<List<Cell>>("Unreachable", o),
            Flank = e.GetOr<CombatFlank?>("Flank", null, o),
            Animals = e.Get<List<AnimalOrder>>("Animals", o),
            Untrained = e.Get<List<global::RimGovernor.Brain.Policy.PawnId>>("Untrained", o),
            Groups = e.Get<List<CombatGroup>>("Groups", o),
            NoShells = e.Get<List<string>>("NoShells", o),
            Dosed = e.Get<List<global::RimGovernor.Brain.Policy.PawnId>>("Dosed", o),
            Permitted = e.Get<EquatableArray<string>>("Permitted", o),
            Casts = e.Get<List<CastMark>>("Casts", o),
            EmpAdapted = e.Get<List<EmpAdaptation>>("EmpAdapted", o),
            Burn = e.GetOr<CombatBurn?>("Burn", null, o),
            Tuning = e.GetOr<CombatTuning?>("Tuning", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatMemory v, Codecs o)
    {
        w.Put("Tactic", v.Tactic, o);
        w.Put("Roles", v.Roles, o);
        w.WriteString("Refusal", v.Refusal);
        w.Put("Formed", v.Formed, o);
        w.Put("Relieved", v.Relieved, o);
        w.Put("Issued", v.Issued, o);
        w.Put("Tick", v.Tick, o);
        if (v.Rescue is { } vRescue)
        {
            w.Put("Rescue", vRescue, o);
        }
        if (v.Pods is { } vPods)
        {
            w.Put("Pods", vPods, o);
        }
        w.Put("PodDoors", v.PodDoors, o);
        w.WriteBoolean("PodWait", v.PodWait);
        w.WriteBoolean("PodStruck", v.PodStruck);
        if (v.PotshotDoor is { } vPotshotDoor)
        {
            w.Put("PotshotDoor", vPotshotDoor, o);
        }
        if (v.ChokeDoor is { } vChokeDoor)
        {
            w.Put("ChokeDoor", vChokeDoor, o);
        }
        w.Put("HeldDoors", v.HeldDoors, o);
        w.Put("Kiter", v.Kiter, o);
        w.WriteBoolean("Leading", v.Leading);
        w.Put("Driller", v.Driller, o);
        w.WriteBoolean("Wait", v.Wait);
        w.Put("WaitSince", v.WaitSince, o);
        w.Put("WaitDoors", v.WaitDoors, o);
        w.Put("WaitRooms", v.WaitRooms, o);
        w.WriteBoolean("Scattered", v.Scattered);
        w.Put("Repairs", v.Repairs, o);
        w.WriteNumber("LineHeld", v.LineHeld);
        if (v.SapperBreach is { } vSapperBreach)
        {
            w.Put("SapperBreach", vSapperBreach, o);
        }
        w.WriteBoolean("Intercept", v.Intercept);
        w.WriteBoolean("MechLure", v.MechLure);
        w.WriteBoolean("Rushing", v.Rushing);
        w.Put("SiegeCamp", v.SiegeCamp, o);
        w.Put("SiegeMode", v.SiegeMode, o);
        w.WriteBoolean("SiegeMortar", v.SiegeMortar);
        w.Put("CannotHit", v.CannotHit, o);
        w.Put("Unreachable", v.Unreachable, o);
        if (v.Flank is { } vFlank)
        {
            w.Put("Flank", vFlank, o);
        }
        w.Put("Animals", v.Animals, o);
        w.Put("Untrained", v.Untrained, o);
        w.Put("Groups", v.Groups, o);
        w.Put("NoShells", v.NoShells, o);
        w.Put("Dosed", v.Dosed, o);
        w.Put("Permitted", v.Permitted, o);
        w.Put("Casts", v.Casts, o);
        w.Put("EmpAdapted", v.EmpAdapted, o);
        if (v.Burn is { } vBurn)
        {
            w.Put("Burn", vBurn, o);
        }
        if (v.Tuning is { } vTuning)
        {
            w.Put("Tuning", vTuning, o);
        }
    }
}

internal sealed class CombatOrderConverter : ObjectCodec<CombatOrder>
{
    protected override CombatOrder Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            Kind = e.Get<CombatOrderKind>("Kind", o),
            Cell = e.Get<Cell>("Cell", o),
            Target = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Target", o),
            FireMode = e.Str("FireMode"),
            Reason = e.Get<CombatOrderReason>("Reason", o),
            Door = e.Get<DoorMode>("Door", o),
            Aim = e.Get<Cell>("Aim", o),
            Clear = e.Bool("Clear"),
            Shell = e.Str("Shell"),
            Faction = e.Str("Faction"),
            Permit = e.Str("Permit"),
            Arm = e.Get<PsycastTarget>("Arm", o),
            Drug = e.Str("Drug"),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatOrder v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Kind", v.Kind, o);
        w.Put("Cell", v.Cell, o);
        w.Put("Target", v.Target, o);
        w.WriteString("FireMode", v.FireMode);
        w.Put("Reason", v.Reason, o);
        w.Put("Door", v.Door, o);
        w.Put("Aim", v.Aim, o);
        w.WriteBoolean("Clear", v.Clear);
        w.WriteString("Shell", v.Shell);
        w.WriteString("Faction", v.Faction);
        w.WriteString("Permit", v.Permit);
        w.Put("Arm", v.Arm, o);
        w.WriteString("Drug", v.Drug);
    }
}

internal sealed class CombatOrderRecordConverter : ObjectCodec<CombatOrderRecord>
{
    protected override CombatOrderRecord Read(JsonElement e, Codecs o) =>
        new(
            e.Get<CombatOrder>("Order", o),
            e.Bool("Applied"),
            e.Bool("Uncertain"),
            e.Str("Refusal")
        );

    protected override void WriteFields(Utf8JsonWriter w, CombatOrderRecord v, Codecs o)
    {
        w.Put("Order", v.Order, o);
        w.WriteBoolean("Applied", v.Applied);
        w.WriteBoolean("Uncertain", v.Uncertain);
        w.WriteString("Refusal", v.Refusal);
    }
}

internal sealed class CombatRescueConverter : ObjectCodec<CombatRescue>
{
    protected override CombatRescue Read(JsonElement e, Codecs o) =>
        new()
        {
            Patient = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Patient", o),
            Rescuer = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Rescuer", o),
            Ordered = e.Get<Tick>("Ordered", o),
            Doors = e.Get<EquatableArray<Cell>>("Doors", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatRescue v, Codecs o)
    {
        w.Put("Patient", v.Patient, o);
        w.Put("Rescuer", v.Rescuer, o);
        w.Put("Ordered", v.Ordered, o);
        w.Put("Doors", v.Doors, o);
    }
}

internal sealed class CombatRestorationConverter : ObjectCodec<CombatRestoration>
{
    protected override CombatRestoration Read(JsonElement e, Codecs o) =>
        new(
            e.Get<World>("World", o),
            e.Get<PlanId>("Owner", o),
            e.Get<EquatableArray<CombatDoorRestoration>>("Doors", o),
            e.Get<EquatableArray<CombatAnimalRestoration>>("Animals", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, CombatRestoration v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Owner", v.Owner, o);
        w.Put("Doors", v.Doors, o);
        w.Put("Animals", v.Animals, o);
    }
}

internal sealed class CombatRoleConverter : ObjectCodec<CombatRole>
{
    protected override CombatRole Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            Cell = e.GetOr<Cell?>("Cell", null, o),
            Target = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Target", o),
            Ranged = e.Bool("Ranged"),
            Duty = e.Get<CombatDuty>("Duty", o),
            Retreat = e.Bool("Retreat"),
            Home = e.GetOr<Cell?>("Home", null, o),
            Mortar = e.GetOr<Cell?>("Mortar", null, o),
            Aim = e.GetOr<Cell?>("Aim", null, o),
            Shell = e.Str("Shell"),
            Ground = e.GetOr<Cell?>("Ground", null, o),
            Repair = e.GetOr<Cell?>("Repair", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatRole v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        if (v.Cell is { } vCell)
        {
            w.Put("Cell", vCell, o);
        }
        w.Put("Target", v.Target, o);
        w.WriteBoolean("Ranged", v.Ranged);
        w.Put("Duty", v.Duty, o);
        w.WriteBoolean("Retreat", v.Retreat);
        if (v.Home is { } vHome)
        {
            w.Put("Home", vHome, o);
        }
        if (v.Mortar is { } vMortar)
        {
            w.Put("Mortar", vMortar, o);
        }
        if (v.Aim is { } vAim)
        {
            w.Put("Aim", vAim, o);
        }
        w.WriteString("Shell", v.Shell);
        if (v.Ground is { } vGround)
        {
            w.Put("Ground", vGround, o);
        }
        if (v.Repair is { } vRepair)
        {
            w.Put("Repair", vRepair, o);
        }
    }
}

internal sealed class CombatRosterConverter : ObjectCodec<CombatRoster>
{
    protected override CombatRoster Read(JsonElement e, Codecs o) =>
        new(
            e.Get<World>("World", o),
            e.Get<EquatableArray<global::RimGovernor.Brain.Domain.PawnId>>("Pawns", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, CombatRoster v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Pawns", v.Pawns, o);
    }
}

internal sealed class CombatStopKindConverter() : StringCodec<CombatStopKind>(s => new(s), v => v.Value);
internal sealed class CombatStopRecordConverter : ObjectCodec<CombatStopRecord>
{
    protected override CombatStopRecord Read(JsonElement e, Codecs o) =>
        new(
            e.Get<PlanId>("Batch", o),
            e.Get<Tick>("Tick", o),
            e.Get<StopEvent>("Stop", o),
            e.Get<EquatableArray<CombatOrderRecord>>("Orders", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, CombatStopRecord v, Codecs o)
    {
        w.Put("Batch", v.Batch, o);
        w.Put("Tick", v.Tick, o);
        w.Put("Stop", v.Stop, o);
        w.Put("Orders", v.Orders, o);
    }
}

internal sealed class CombatTuningConverter : ObjectCodec<CombatTuning>
{
    protected override CombatTuning Read(JsonElement e, Codecs o) =>
        new()
        {
            DoorCloseRange = e.I32("DoorCloseRange"),
            DoorOpenRange = e.I32("DoorOpenRange"),
            DoorGunners = e.I32("DoorGunners"),
            FlankMinDefenders = e.I32("FlankMinDefenders"),
            FlankSize = e.I32("FlankSize"),
            FlankLateral = e.I32("FlankLateral"),
            GroupGap = e.I32("GroupGap"),
            KiteLeadRange = e.I32("KiteLeadRange"),
            KiteSpeedRatio = e.Dbl("KiteSpeedRatio"),
            ScatterCells = e.I32("ScatterCells"),
            DoseMargin = e.Dbl("DoseMargin"),
            RushRange = e.I32("RushRange"),
        };

    protected override void WriteFields(Utf8JsonWriter w, CombatTuning v, Codecs o)
    {
        w.WriteNumber("DoorCloseRange", v.DoorCloseRange);
        w.WriteNumber("DoorOpenRange", v.DoorOpenRange);
        w.WriteNumber("DoorGunners", v.DoorGunners);
        w.WriteNumber("FlankMinDefenders", v.FlankMinDefenders);
        w.WriteNumber("FlankSize", v.FlankSize);
        w.WriteNumber("FlankLateral", v.FlankLateral);
        w.WriteNumber("GroupGap", v.GroupGap);
        w.WriteNumber("KiteLeadRange", v.KiteLeadRange);
        w.WriteNumber("KiteSpeedRatio", v.KiteSpeedRatio);
        w.WriteNumber("ScatterCells", v.ScatterCells);
        w.WriteNumber("DoseMargin", v.DoseMargin);
        w.WriteNumber("RushRange", v.RushRange);
    }
}

internal sealed class ComfortHistoryConverter : ObjectCodec<ComfortHistory>
{
    protected override ComfortHistory Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ComfortUse>("Dining", o),
            e.Get<ComfortUse>("Recreation", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, ComfortHistory v, Codecs o)
    {
        w.Put("Dining", v.Dining, o);
        w.Put("Recreation", v.Recreation, o);
    }
}

internal sealed class ComfortUseConverter : ObjectCodec<ComfortUse>
{
    protected override ComfortUse Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Facility"),
            e.Get<Tick>("Tick", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, ComfortUse v, Codecs o)
    {
        w.WriteString("Facility", v.Facility ?? "");
        w.Put("Tick", v.Tick, o);
    }
}

internal sealed class ConcernProgressConverter : ObjectCodec<ConcernProgress>
{
    protected override ConcernProgress Read(JsonElement e, Codecs o) =>
        new()
        {
            Concern = e.Get<ConcernId>("Concern", o),
            Method = e.Str("Method"),
            Expected = e.Str("Expected"),
            LastProgress = e.Get<Tick>("LastProgress", o),
            NextReview = e.Get<Tick>("NextReview", o),
            Blocked = e.Get<BlockedReason>("Blocked", o),
            Cooldowns = e.Get<EquatableArray<ProgressCooldown>>("Cooldowns", o),
            Observed = e.GetOr<double?>("Observed", null, o),
            Planner = e.Get<Cause>("Planner", o),
            PlannerSubject = e.Str("PlannerSubject"),
            Open = e.Bool("Open"),
        };

    protected override void WriteFields(Utf8JsonWriter w, ConcernProgress v, Codecs o)
    {
        w.Put("Concern", v.Concern, o);
        w.WriteString("Method", v.Method);
        w.WriteString("Expected", v.Expected);
        w.Put("LastProgress", v.LastProgress, o);
        w.Put("NextReview", v.NextReview, o);
        w.Put("Blocked", v.Blocked, o);
        w.Put("Cooldowns", v.Cooldowns, o);
        if (v.Observed is { } vObserved)
        {
            w.WriteNumber("Observed", vObserved);
        }
        w.Put("Planner", v.Planner, o);
        w.WriteString("PlannerSubject", v.PlannerSubject);
        w.WriteBoolean("Open", v.Open);
    }
}

internal sealed class ConduitLinkConverter : ObjectCodec<ConduitLink>
{
    protected override ConduitLink Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ConduitRef>("From", o),
            e.Get<ConduitRef>("To", o),
            e.Get<EquatableArray<Cell>>("Cells", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, ConduitLink v, Codecs o)
    {
        w.Put("From", v.From, o);
        w.Put("To", v.To, o);
        w.Put("Cells", v.Cells, o);
    }
}

internal sealed class ConduitRefConverter() : StringCodec<ConduitRef>(s => new(s), v => v.Value);
internal sealed class ConstructionHelpRecordConverter : ObjectCodec<ConstructionHelpRecord>
{
    protected override ConstructionHelpRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            Tick = e.Get<Tick>("Tick", o),
            Idle = e.Get<EquatableArray<global::RimGovernor.Brain.Policy.PawnId>>("Idle", o),
            Helpers = e.Get<EquatableArray<global::RimGovernor.Brain.Policy.PawnId>>("Helpers", o),
            DemandTick = e.Get<Tick>("DemandTick", o),
            Ready = e.I32("Ready"),
            Unmet = e.I32("Unmet"),
            Reason = e.Str("Reason"),
            Withheld = e.Get<EquatableArray<string>>("Withheld", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ConstructionHelpRecord v, Codecs o)
    {
        w.Put("Tick", v.Tick, o);
        w.Put("Idle", v.Idle, o);
        w.Put("Helpers", v.Helpers, o);
        w.Put("DemandTick", v.DemandTick, o);
        w.WriteNumber("Ready", v.Ready);
        w.WriteNumber("Unmet", v.Unmet);
        w.WriteString("Reason", v.Reason);
        w.Put("Withheld", v.Withheld, o);
    }
}

internal sealed class CreatedZoneConverter : ObjectCodec<CreatedZone>
{
    protected override CreatedZone Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Id"),
            e.Get<EquatableArray<Cell>>("Cells", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, CreatedZone v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.Put("Cells", v.Cells, o);
    }
}

internal sealed class DashTripConverter : ObjectCodec<DashTrip>
{
    protected override DashTrip Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Str("Id"),
            Value = e.Dbl("Value"),
            Exposure = e.I32("Exposure"),
        };

    protected override void WriteFields(Utf8JsonWriter w, DashTrip v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.WriteNumber("Value", v.Value);
        w.WriteNumber("Exposure", v.Exposure);
    }
}

internal sealed class DashWindowConverter : ObjectCodec<DashWindow>
{
    protected override DashWindow Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            Until = e.Get<Tick>("Until", o),
            Trips = e.Get<EquatableArray<DashTrip>>("Trips", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, DashWindow v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Until", v.Until, o);
        w.Put("Trips", v.Trips, o);
    }
}

internal sealed class DecayingSkillConverter : ObjectCodec<DecayingSkill>
{
    protected override DecayingSkill Read(JsonElement e, Codecs o) =>
        new(
            e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            e.Str("Skill"),
            e.I32("Level")
        );

    protected override void WriteFields(Utf8JsonWriter w, DecayingSkill v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Skill", v.Skill);
        w.WriteNumber("Level", v.Level);
    }
}

internal sealed class DefenseBuildingConverter : ObjectCodec<DefenseBuilding>
{
    protected override DefenseBuilding Read(JsonElement e, Codecs o) =>
        new()
        {
            Definition = e.Str("Definition"),
            Cell = e.Get<Cell>("Cell", o),
            Rotation = e.Get<Rotation>("Rotation", o),
            Stuff = e.Str("Stuff"),
        };

    protected override void WriteFields(Utf8JsonWriter w, DefenseBuilding v, Codecs o)
    {
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
        w.Put("Rotation", v.Rotation, o);
        w.WriteString("Stuff", v.Stuff);
    }
}

internal sealed class DefenseLayoutRecordConverter : ObjectCodec<DefenseLayoutRecord>
{
    protected override DefenseLayoutRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            Standard = e.Get<StandardId>("Standard", o),
            Episode = e.U64("Episode"),
            Killbox = e.Str("Killbox"),
            KillboxDigest = e.Str("KillboxDigest"),
            TrapLane = e.Get<List<Cell>>("TrapLane", o),
            Firing = e.Get<List<Cell>>("Firing", o),
            Retreat = e.Get<List<Cell>>("Retreat", o),
            Entrances = e.Get<List<Cell>>("Entrances", o),
            Tiers = e.Get<List<DefenseTierRecord>>("Tiers", o),
            Complete = e.Bool("Complete"),
            VerifiedTick = e.Get<Tick>("VerifiedTick", o),
            VerifiedCombat = e.Str("VerifiedCombat"),
            TurretsProbedTick = e.Get<Tick>("TurretsProbedTick", o),
            PerimeterKey = e.Str("PerimeterKey"),
            PerimeterRevision = e.I32("PerimeterRevision"),
            PerimeterBridge = e.Str("PerimeterBridge"),
        };

    protected override void WriteFields(Utf8JsonWriter w, DefenseLayoutRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Standard", v.Standard, o);
        w.WriteNumber("Episode", v.Episode);
        w.WriteString("Killbox", v.Killbox);
        w.WriteString("KillboxDigest", v.KillboxDigest);
        w.Put("TrapLane", v.TrapLane, o);
        w.Put("Firing", v.Firing, o);
        w.Put("Retreat", v.Retreat, o);
        w.Put("Entrances", v.Entrances, o);
        w.Put("Tiers", v.Tiers, o);
        w.WriteBoolean("Complete", v.Complete);
        w.Put("VerifiedTick", v.VerifiedTick, o);
        w.WriteString("VerifiedCombat", v.VerifiedCombat);
        w.Put("TurretsProbedTick", v.TurretsProbedTick, o);
        w.WriteString("PerimeterKey", v.PerimeterKey);
        w.WriteNumber("PerimeterRevision", v.PerimeterRevision);
        w.WriteString("PerimeterBridge", v.PerimeterBridge);
    }
}

internal sealed class DefenseTierNameConverter() : StringCodec<DefenseTierName>(s => new(s), v => v.Value);
internal sealed class DefenseTierRecordConverter : ObjectCodec<DefenseTierRecord>
{
    protected override DefenseTierRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            Name = e.Get<DefenseTierName>("Name", o),
            Nodes = e.Get<List<string>>("Nodes", o),
            Buildings = e.Get<List<DefenseBuilding>>("Buildings", o),
            Refused = e.Get<List<Cell>>("Refused", o),
            Reserved = e.Get<List<Cell>>("Reserved", o),
            Attempts = e.I32("Attempts"),
            Built = e.Bool("Built"),
            Reopened = e.I32("Reopened"),
            Remove = e.Bool("Remove"),
            Bridge = e.Str("Bridge"),
            Foundation = e.Bool("Foundation"),
            Before = e.Bool("Before"),
        };

    protected override void WriteFields(Utf8JsonWriter w, DefenseTierRecord v, Codecs o)
    {
        w.Put("Name", v.Name, o);
        w.Put("Nodes", v.Nodes, o);
        w.Put("Buildings", v.Buildings, o);
        w.Put("Refused", v.Refused, o);
        w.Put("Reserved", v.Reserved, o);
        w.WriteNumber("Attempts", v.Attempts);
        w.WriteBoolean("Built", v.Built);
        w.WriteNumber("Reopened", v.Reopened);
        w.WriteBoolean("Remove", v.Remove);
        w.WriteString("Bridge", v.Bridge);
        w.WriteBoolean("Foundation", v.Foundation);
        w.WriteBoolean("Before", v.Before);
    }
}

internal sealed class DisasterConditionConverter : ObjectCodec<DisasterCondition>
{
    protected override DisasterCondition Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Str("Id"),
            Definition = e.Str("Definition"),
            Permanent = e.Bool("Permanent"),
            TicksLeft = e.GetOr<long?>("TicksLeft", null, o),
            DisablesPower = e.Bool("DisablesPower"),
        };

    protected override void WriteFields(Utf8JsonWriter w, DisasterCondition v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.WriteString("Definition", v.Definition);
        w.WriteBoolean("Permanent", v.Permanent);
        if (v.TicksLeft is { } vTicksLeft)
        {
            w.WriteNumber("TicksLeft", vTicksLeft);
        }
        w.WriteBoolean("DisablesPower", v.DisablesPower);
    }
}

internal sealed class DisasterEvidenceConverter : ObjectCodec<DisasterEvidence>
{
    protected override DisasterEvidence Read(JsonElement e, Codecs o) =>
        new(
            e.Get<DisasterService>("Service", o),
            e.Get<Finding>("Finding", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, DisasterEvidence v, Codecs o)
    {
        w.Put("Service", v.Service, o);
        w.Put("Finding", v.Finding, o);
    }
}

internal sealed class DisasterHistoryConverter : ObjectCodec<DisasterHistory>
{
    protected override DisasterHistory Read(JsonElement e, Codecs o) =>
        new()
        {
            ShortCircuitTick = e.GetOr<long?>("ShortCircuitTick", null, o),
            WorkKnown = e.Bool("WorkKnown"),
            Work = e.Get<EquatableArray<RecoveryWork>>("Work", o),
            Started = e.Get<Tick>("Started", o),
            Observed = e.Get<Tick>("Observed", o),
            Phase = e.Get<DisasterPhase>("Phase", o),
            Conditions = e.Get<EquatableArray<DisasterCondition>>("Conditions", o),
            Services = e.Get<EquatableArray<DisasterEvidence>>("Services", o),
            Affected = e.Get<EquatableArray<DisasterService>>("Affected", o),
            Damaged = e.Get<EquatableArray<string>>("Damaged", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, DisasterHistory v, Codecs o)
    {
        if (v.ShortCircuitTick is { } vShortCircuitTick)
        {
            w.WriteNumber("ShortCircuitTick", vShortCircuitTick);
        }
        w.WriteBoolean("WorkKnown", v.WorkKnown);
        w.Put("Work", v.Work, o);
        w.Put("Started", v.Started, o);
        w.Put("Observed", v.Observed, o);
        w.Put("Phase", v.Phase, o);
        w.Put("Conditions", v.Conditions, o);
        w.Put("Services", v.Services, o);
        w.Put("Affected", v.Affected, o);
        w.Put("Damaged", v.Damaged, o);
    }
}

internal sealed class DiseaseRestConverter : ObjectCodec<DiseaseRest>
{
    protected override DiseaseRest Read(JsonElement e, Codecs o) =>
        new(
            e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            e.Get<EquatableArray<string>>("Conditions", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, DiseaseRest v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Conditions", v.Conditions, o);
    }
}

internal sealed class EmpAdaptationConverter : ObjectCodec<EmpAdaptation>
{
    protected override EmpAdaptation Read(JsonElement e, Codecs o) =>
        new(
            e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            e.Get<Tick>("Until", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, EmpAdaptation v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Until", v.Until, o);
    }
}

internal sealed class EventLootHistoryConverter : ObjectCodec<EventLootHistory>
{
    protected override EventLootHistory Read(JsonElement e, Codecs o) =>
        new()
        {
            Pending = e.Get<IReadOnlyList<StartingSupply>>("Pending", o),
            Held = e.Get<IReadOnlyList<LootHold>>("Held", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, EventLootHistory v, Codecs o)
    {
        w.Put("Pending", v.Pending, o);
        w.Put("Held", v.Held, o);
    }
}

internal sealed class ExtentCellConverter : ObjectCodec<ExtentCell>
{
    protected override ExtentCell Read(JsonElement e, Codecs o) =>
        new()
        {
            Cell = e.Get<Cell>("Cell", o),
            Provenance = e.Get<EquatableArray<ExtentProvenance>>("Provenance", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ExtentCell v, Codecs o)
    {
        w.Put("Cell", v.Cell, o);
        w.Put("Provenance", v.Provenance, o);
    }
}

internal sealed class ExtentProvenanceConverter : ObjectCodec<ExtentProvenance>
{
    protected override ExtentProvenance Read(JsonElement e, Codecs o) =>
        new()
        {
            Origin = e.Get<ExtentOrigin>("Origin", o),
            Facility = e.Str("Facility"),
            Plan = e.Get<PlanId>("Plan", o),
            Action = e.Get<ActionId>("Action", o),
            Concern = e.Get<ConcernId>("Concern", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ExtentProvenance v, Codecs o)
    {
        w.Put("Origin", v.Origin, o);
        w.WriteString("Facility", v.Facility);
        w.Put("Plan", v.Plan, o);
        w.Put("Action", v.Action, o);
        w.Put("Concern", v.Concern, o);
    }
}

internal sealed class ExtentRegionConverter : ObjectCodec<ExtentRegion>
{
    protected override ExtentRegion Read(JsonElement e, Codecs o) =>
        new()
        {
            Cells = e.Get<EquatableArray<ExtentCell>>("Cells", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ExtentRegion v, Codecs o)
    {
        w.Put("Cells", v.Cells, o);
    }
}

internal sealed class FiringPositionConverter : ObjectCodec<FiringPosition>
{
    protected override FiringPosition Read(JsonElement e, Codecs o) =>
        new(
            e.Get<Cell>("Cell", o),
            e.Get<Cell>("Cover", o),
            e.Get<Cell>("Retreat", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, FiringPosition v, Codecs o)
    {
        w.Put("Cell", v.Cell, o);
        w.Put("Cover", v.Cover, o);
        w.Put("Retreat", v.Retreat, o);
    }
}

internal sealed class GeneEffectsConverter : ObjectCodec<GeneEffects>
{
    protected override GeneEffects Read(JsonElement e, Codecs o) =>
        new()
        {
            Stats = e.Get<IReadOnlyDictionary<string, StatModifier>>("Stats", o),
            DisabledNeeds = e.Get<IReadOnlySet<string>>("DisabledNeeds", o),
            EnabledNeeds = e.Get<IReadOnlySet<string>>("EnabledNeeds", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, GeneEffects v, Codecs o)
    {
        w.Put("Stats", v.Stats, o);
        w.Put("DisabledNeeds", v.DisabledNeeds, o);
        w.Put("EnabledNeeds", v.EnabledNeeds, o);
    }
}

internal sealed class HitRefusalConverter : ObjectCodec<HitRefusal>
{
    protected override HitRefusal Read(JsonElement e, Codecs o) =>
        new(
            e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            e.Get<global::RimGovernor.Brain.Policy.PawnId>("Target", o),
            e.Get<Cell>("From", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, HitRefusal v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Target", v.Target, o);
        w.Put("From", v.From, o);
    }
}

internal sealed class IssuedOrderConverter : ObjectCodec<IssuedOrder>
{
    protected override IssuedOrder Read(JsonElement e, Codecs o) =>
        new(
            e.Get<CombatOrder>("CombatOrder", o),
            e.Get<Tick>("Tick", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, IssuedOrder v, Codecs o)
    {
        w.Put("CombatOrder", v.CombatOrder, o);
        w.Put("Tick", v.Tick, o);
    }
}

internal sealed class LayoutPlanConverter : ObjectCodec<LayoutPlan>
{
    protected override LayoutPlan Read(JsonElement e, Codecs o) =>
        new()
        {
            Entrances = e.Get<IReadOnlyList<Cell>>("Entrances", o),
            Links = e.Get<IReadOnlyList<RoomLink>>("Links", o),
            Conduits = e.Get<IReadOnlyList<ConduitLink>>("Conduits", o),
            Owed = e.Get<IReadOnlyList<PlannedRole>>("Owed", o),
            Trips = e.Get<IReadOnlyList<RoomTrip>>("Trips", o),
            OutsideTrips = e.Get<IReadOnlyList<OutsideTrip>>("OutsideTrips", o),
            Rooms = e.Get<IReadOnlyList<PlannedRoom>>("Rooms", o),
            Zones = e.Get<IReadOnlyList<LayoutZone>>("Zones", o),
            Reservations = e.Get<IReadOnlyList<LayoutReservation>>("Reservations", o),
            Sites = e.Get<IReadOnlyList<PlannedSite>>("Sites", o),
            RetiredGround = e.Get<IReadOnlyList<Region>>("RetiredGround", o),
            Roofed = e.Get<Region>("Roofed", o),
            Lamps = e.Get<IReadOnlyDictionary<int, IReadOnlyList<PlannedLamp>>>("Lamps", o),
            Cold = e.Bool("Cold"),
            Hot = e.Bool("Hot"),
            NextRoomId = e.I32("NextRoomId"),
        };

    protected override void WriteFields(Utf8JsonWriter w, LayoutPlan v, Codecs o)
    {
        w.Put("Entrances", v.Entrances, o);
        w.Put("Links", v.Links, o);
        w.Put("Conduits", v.Conduits, o);
        w.Put("Owed", v.Owed, o);
        w.Put("Trips", v.Trips, o);
        w.Put("OutsideTrips", v.OutsideTrips, o);
        w.Put("Rooms", v.Rooms, o);
        w.Put("Zones", v.Zones, o);
        w.Put("Reservations", v.Reservations, o);
        w.Put("Sites", v.Sites, o);
        w.Put("RetiredGround", v.RetiredGround, o);
        w.Put("Roofed", v.Roofed, o);
        w.Put("Lamps", v.Lamps, o);
        w.WriteBoolean("Cold", v.Cold);
        w.WriteBoolean("Hot", v.Hot);
        w.WriteNumber("NextRoomId", v.NextRoomId);
    }
}

internal sealed class LayoutReservationConverter : ObjectCodec<LayoutReservation>
{
    protected override LayoutReservation Read(JsonElement e, Codecs o) =>
        new()
        {
            Kind = e.Get<ReservationKind>("Kind", o),
            Area = e.Get<Region>("Area", o),
            Facing = e.GetOr<Rotation?>("Facing", null, o),
            Herd = e.Str("Herd"),
        };

    protected override void WriteFields(Utf8JsonWriter w, LayoutReservation v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.Put("Area", v.Area, o);
        if (v.Facing is { } vFacing)
        {
            w.Put("Facing", vFacing, o);
        }
        w.WriteString("Herd", v.Herd);
    }
}

internal sealed class LayoutZoneConverter : ObjectCodec<LayoutZone>
{
    protected override LayoutZone Read(JsonElement e, Codecs o) =>
        new()
        {
            Kind = e.Get<global::RimGovernor.Brain.Policy.ZoneKind>("Kind", o),
            Runs = e.Get<IReadOnlyList<RowRun>>("Runs", o),
            Ore = e.Bool("Ore"),
        };

    protected override void WriteFields(Utf8JsonWriter w, LayoutZone v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.Put("Runs", v.Runs, o);
        w.WriteBoolean("Ore", v.Ore);
    }
}

internal sealed class LootHoldConverter : ObjectCodec<LootHold>
{
    protected override LootHold Read(JsonElement e, Codecs o) =>
        new()
        {
            Thing = e.Str("Thing"),
            Definition = e.Str("Definition"),
            Cell = e.Get<Cell>("Cell", o),
            Reason = e.Str("Reason"),
        };

    protected override void WriteFields(Utf8JsonWriter w, LootHold v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
        w.WriteString("Reason", v.Reason);
    }
}

internal sealed class MedicalCareHistoryConverter : ObjectCodec<MedicalCareHistory>
{
    protected override MedicalCareHistory Read(JsonElement e, Codecs o) =>
        new()
        {
            Resting = e.Get<IReadOnlyList<DiseaseRest>>("Resting", o),
            CensusKnown = e.Bool("CensusKnown"),
            Patients = e.Get<IReadOnlyList<global::RimGovernor.Brain.Policy.PawnId>>("Patients", o),
            Unknown = e.Get<IReadOnlyList<global::RimGovernor.Brain.Policy.PawnId>>("Unknown", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, MedicalCareHistory v, Codecs o)
    {
        w.Put("Resting", v.Resting, o);
        w.WriteBoolean("CensusKnown", v.CensusKnown);
        w.Put("Patients", v.Patients, o);
        w.Put("Unknown", v.Unknown, o);
    }
}

internal sealed class MentalStateConverter : ObjectCodec<MentalState>
{
    protected override MentalState Read(JsonElement e, Codecs o) =>
        new()
        {
            DefName = e.Str("DefName"),
            IsAggro = e.Bool("IsAggro"),
            TicksInState = e.I32("TicksInState"),
        };

    protected override void WriteFields(Utf8JsonWriter w, MentalState v, Codecs o)
    {
        w.WriteString("DefName", v.DefName);
        w.WriteBoolean("IsAggro", v.IsAggro);
        w.WriteNumber("TicksInState", v.TicksInState);
    }
}

internal sealed class MoodCauseConverter : ObjectCodec<MoodCause>
{
    protected override MoodCause Read(JsonElement e, Codecs o) =>
        new()
        {
            Need = e.Get<MoodNeed>("Need", o),
            Level = e.Get<Fact<double>>("Level", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodCause v, Codecs o)
    {
        w.Put("Need", v.Need, o);
        w.Put("Level", v.Level, o);
    }
}

internal sealed class MoodHistoryConverter : ObjectCodec<MoodHistory>
{
    protected override MoodHistory Read(JsonElement e, Codecs o) =>
        new()
        {
            States = e.Get<IReadOnlyList<MoodState>>("States", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodHistory v, Codecs o)
    {
        w.Put("States", v.States, o);
    }
}

internal sealed class MoodPawnConverter : ObjectCodec<MoodPawn>
{
    protected override MoodPawn Read(JsonElement e, Codecs o) =>
        new()
        {
            Break = e.Get<Fact<MentalState>>("Break", o),
            HighExpectations = e.Get<Fact<bool>>("HighExpectations", o),
            Id = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Id", o),
            Mood = e.Get<Fact<double>>("Mood", o),
            Threshold = e.Get<Fact<double>>("Threshold", o),
            Target = e.Get<Fact<double>>("Target", o),
            Food = e.Get<Fact<double>>("Food", o),
            Rest = e.Get<Fact<double>>("Rest", o),
            Joy = e.Get<Fact<double>>("Joy", o),
            Mental = e.Get<Fact<bool>>("Mental", o),
            Dead = e.Get<Fact<bool>>("Dead", o),
            Downed = e.Get<Fact<bool>>("Downed", o),
            Drafted = e.Get<Fact<bool>>("Drafted", o),
            PlayerForced = e.Get<Fact<bool>>("PlayerForced", o),
            Thoughts = e.Get<Fact<EquatableArray<MoodThought>>>("Thoughts", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodPawn v, Codecs o)
    {
        w.Put("Break", v.Break, o);
        w.Put("HighExpectations", v.HighExpectations, o);
        w.Put("Id", v.Id, o);
        w.Put("Mood", v.Mood, o);
        w.Put("Threshold", v.Threshold, o);
        w.Put("Target", v.Target, o);
        w.Put("Food", v.Food, o);
        w.Put("Rest", v.Rest, o);
        w.Put("Joy", v.Joy, o);
        w.Put("Mental", v.Mental, o);
        w.Put("Dead", v.Dead, o);
        w.Put("Downed", v.Downed, o);
        w.Put("Drafted", v.Drafted, o);
        w.Put("PlayerForced", v.PlayerForced, o);
        w.Put("Thoughts", v.Thoughts, o);
    }
}

internal sealed class MoodProvisionConverter : ObjectCodec<MoodProvision>
{
    protected override MoodProvision Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ConcernId>("Concern", o),
            e.Dbl("Offset")
        );

    protected override void WriteFields(Utf8JsonWriter w, MoodProvision v, Codecs o)
    {
        w.Put("Concern", v.Concern, o);
        w.WriteNumber("Offset", v.Offset);
    }
}

internal sealed class MoodStateConverter : ObjectCodec<MoodState>
{
    protected override MoodState Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<MoodPawn>("Pawn", o),
            Active = e.Bool("Active"),
            Missing = e.Bool("Missing"),
            MentalRisk = e.Bool("MentalRisk"),
            Causes = e.Get<IReadOnlyList<MoodCause>>("Causes", o),
            Provision = e.Get<IReadOnlyList<MoodProvision>>("Provision", o),
            Unowned = e.Get<IReadOnlyList<MoodThought>>("Unowned", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodState v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteBoolean("Active", v.Active);
        w.WriteBoolean("Missing", v.Missing);
        w.WriteBoolean("MentalRisk", v.MentalRisk);
        w.Put("Causes", v.Causes, o);
        w.Put("Provision", v.Provision, o);
        w.Put("Unowned", v.Unowned, o);
    }
}

internal sealed class MoodThoughtConverter : ObjectCodec<MoodThought>
{
    protected override MoodThought Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Def"),
            e.Dbl("Offset")
        );

    protected override void WriteFields(Utf8JsonWriter w, MoodThought v, Codecs o)
    {
        w.WriteString("Def", v.Def);
        w.WriteNumber("Offset", v.Offset);
    }
}

internal sealed class NativeRefusalConverter : ObjectCodec<NativeRefusal>
{
    protected override NativeRefusal Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Code"),
            e.Str("Reason"),
            e.Get<RefusalClass>("Class", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, NativeRefusal v, Codecs o)
    {
        w.WriteString("Code", v.Code);
        w.WriteString("Reason", v.Reason);
        w.Put("Class", v.Class, o);
    }
}

internal sealed class NoOpRecordConverter : ObjectCodec<NoOpRecord>
{
    protected override NoOpRecord Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ConcernId>("Concern", o),
            e.Get<NoOpReason>("Reason", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, NoOpRecord v, Codecs o)
    {
        w.Put("Concern", v.Concern, o);
        w.Put("Reason", v.Reason, o);
    }
}

internal sealed class ObservationConverter : ObjectCodec<Observation>
{
    protected override Observation Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ActionId>("Action", o),
            e.Get<AttemptId>("Attempt", o),
            e.Get<GenerationSnapshot>("Snapshot", o),
            e.Get<Tick>("Tick", o),
            e.Get<Effect>("Effect", o),
            e.Get<ObservationCausality>("Causality", o),
            e.Get<UnsuccessfulReason>("UnsuccessfulReason", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, Observation v, Codecs o)
    {
        w.Put("Action", v.Action, o);
        w.Put("Attempt", v.Attempt, o);
        w.Put("Snapshot", v.Snapshot, o);
        w.Put("Tick", v.Tick, o);
        w.Put("Effect", v.Effect, o);
        w.Put("Causality", v.Causality, o);
        w.Put("UnsuccessfulReason", v.UnsuccessfulReason, o);
    }
}

internal sealed class OutsideTripConverter : ObjectCodec<OutsideTrip>
{
    protected override OutsideTrip Read(JsonElement e, Codecs o) =>
        new(
            e.I32("Room"),
            e.Dbl("Weight")
        );

    protected override void WriteFields(Utf8JsonWriter w, OutsideTrip v, Codecs o)
    {
        w.WriteNumber("Room", v.Room);
        w.WriteNumber("Weight", v.Weight);
    }
}

internal sealed class PawnBiotechConverter : ObjectCodec<PawnBiotech>
{
    protected override PawnBiotech Read(JsonElement e, Codecs o) =>
        new()
        {
            LifeStage = e.Get<Fact<string>>("LifeStage", o),
            DevelopmentalStage = e.Get<Fact<string>>("DevelopmentalStage", o),
            Learning = e.Get<Fact<double>>("Learning", o),
            LearningCategory = e.Get<Fact<string>>("LearningCategory", o),
            Genes = e.Get<Fact<EquatableArray<PawnGene>>>("Genes", o),
            Xenotype = e.Get<Fact<string>>("Xenotype", o),
            XenotypeName = e.Get<Fact<string>>("XenotypeName", o),
            Hybrid = e.Get<Fact<bool>>("Hybrid", o),
            Effects = e.Get<Fact<GeneEffects>>("Effects", o),
            WorkMinAges = e.Get<Fact<IReadOnlyDictionary<WorkType, int>>>("WorkMinAges", o),
            Mechanitor = e.Get<Fact<PawnMechanitor?>>("Mechanitor", o),
            Mech = e.Get<Fact<PawnMech?>>("Mech", o),
            Deathrest = e.Get<Fact<PawnDeathrest?>>("Deathrest", o),
            XenogermRegrowTicksLeft = e.Get<Fact<int>>("XenogermRegrowTicksLeft", o),
            XenogermComaTicksLeft = e.Get<Fact<int>>("XenogermComaTicksLeft", o),
            InExtractor = e.Get<Fact<bool>>("InExtractor", o),
            Extractable = e.Get<Fact<bool>>("Extractable", o),
            ExtractableReason = e.Get<Fact<string>>("ExtractableReason", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnBiotech v, Codecs o)
    {
        w.Put("LifeStage", v.LifeStage, o);
        w.Put("DevelopmentalStage", v.DevelopmentalStage, o);
        w.Put("Learning", v.Learning, o);
        w.Put("LearningCategory", v.LearningCategory, o);
        w.Put("Genes", v.Genes, o);
        w.Put("Xenotype", v.Xenotype, o);
        w.Put("XenotypeName", v.XenotypeName, o);
        w.Put("Hybrid", v.Hybrid, o);
        w.Put("Effects", v.Effects, o);
        w.Put("WorkMinAges", v.WorkMinAges, o);
        w.Put("Mechanitor", v.Mechanitor, o);
        w.Put("Mech", v.Mech, o);
        w.Put("Deathrest", v.Deathrest, o);
        w.Put("XenogermRegrowTicksLeft", v.XenogermRegrowTicksLeft, o);
        w.Put("XenogermComaTicksLeft", v.XenogermComaTicksLeft, o);
        w.Put("InExtractor", v.InExtractor, o);
        w.Put("Extractable", v.Extractable, o);
        w.Put("ExtractableReason", v.ExtractableReason, o);
    }
}

internal sealed class PawnDeathrestConverter : ObjectCodec<PawnDeathrest>
{
    protected override PawnDeathrest Read(JsonElement e, Codecs o) =>
        new()
        {
            Deathresting = e.Get<Fact<bool>>("Deathresting", o),
            Level = e.Get<Fact<double>>("Level", o),
            LastDeathrestTick = e.Get<Fact<int>>("LastDeathrestTick", o),
            DeathrestPercent = e.Get<Fact<double>>("DeathrestPercent", o),
            Capacity = e.Get<Fact<int>>("Capacity", o),
            BoundBuildings = e.Get<Fact<int>>("BoundBuildings", o),
            AutoWake = e.Get<Fact<bool>>("AutoWake", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnDeathrest v, Codecs o)
    {
        w.Put("Deathresting", v.Deathresting, o);
        w.Put("Level", v.Level, o);
        w.Put("LastDeathrestTick", v.LastDeathrestTick, o);
        w.Put("DeathrestPercent", v.DeathrestPercent, o);
        w.Put("Capacity", v.Capacity, o);
        w.Put("BoundBuildings", v.BoundBuildings, o);
        w.Put("AutoWake", v.AutoWake, o);
    }
}

internal sealed class PawnGeneConverter : ObjectCodec<PawnGene>
{
    protected override PawnGene Read(JsonElement e, Codecs o) =>
        new()
        {
            Name = e.Str("Name"),
            Xenogene = e.Get<Fact<bool>>("Xenogene", o),
            Active = e.Get<Fact<bool>>("Active", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnGene v, Codecs o)
    {
        w.WriteString("Name", v.Name);
        w.Put("Xenogene", v.Xenogene, o);
        w.Put("Active", v.Active, o);
    }
}

internal sealed class PolicyPawnIdConverter() : StringCodec<global::RimGovernor.Brain.Policy.PawnId>(s => new(s), v => v.Value);
internal sealed class PawnMechConverter : ObjectCodec<PawnMech>
{
    protected override PawnMech Read(JsonElement e, Codecs o) =>
        new()
        {
            Overseer = e.Str("Overseer"),
            WorkMode = e.Get<Fact<string>>("WorkMode", o),
            ControlGroup = e.Get<Fact<int>>("ControlGroup", o),
            Energy = e.Get<Fact<double>>("Energy", o),
            RechargeBelow = e.Get<Fact<double>>("RechargeBelow", o),
            RechargeAbove = e.Get<Fact<double>>("RechargeAbove", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnMech v, Codecs o)
    {
        w.WriteString("Overseer", v.Overseer);
        w.Put("WorkMode", v.WorkMode, o);
        w.Put("ControlGroup", v.ControlGroup, o);
        w.Put("Energy", v.Energy, o);
        w.Put("RechargeBelow", v.RechargeBelow, o);
        w.Put("RechargeAbove", v.RechargeAbove, o);
    }
}

internal sealed class PawnMechanitorConverter : ObjectCodec<PawnMechanitor>
{
    protected override PawnMechanitor Read(JsonElement e, Codecs o) =>
        new()
        {
            UsedBandwidth = e.Get<Fact<int>>("UsedBandwidth", o),
            TotalBandwidth = e.Get<Fact<int>>("TotalBandwidth", o),
            GestationBandwidth = e.Get<Fact<int>>("GestationBandwidth", o),
            ControlGroups = e.Get<Fact<int>>("ControlGroups", o),
            ControlledMechs = e.Get<IReadOnlyList<string>>("ControlledMechs", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnMechanitor v, Codecs o)
    {
        w.Put("UsedBandwidth", v.UsedBandwidth, o);
        w.Put("TotalBandwidth", v.TotalBandwidth, o);
        w.Put("GestationBandwidth", v.GestationBandwidth, o);
        w.Put("ControlGroups", v.ControlGroups, o);
        w.Put("ControlledMechs", v.ControlledMechs, o);
    }
}

internal sealed class PawnProfileConverter : ObjectCodec<PawnProfile>
{
    protected override PawnProfile Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Id", o),
            Skills = e.Get<IReadOnlyDictionary<string, ProfileSkill>>("Skills", o),
            Traits = e.Get<IReadOnlyList<PawnTrait>>("Traits", o),
            Effects = e.Get<TraitEffects>("Effects", o),
            Incapable = e.Get<IReadOnlyDictionary<WorkType, bool>>("Incapable", o),
            WorkSkill = e.Get<IReadOnlyDictionary<WorkType, string>>("WorkSkill", o),
            Age = e.Dbl("Age"),
            Child = e.Bool("Child"),
            Biotech = e.Get<Fact<PawnBiotech>>("Biotech", o),
            Genes = e.Get<GeneEffects>("Genes", o),
            WorkMinAge = e.Get<IReadOnlyDictionary<WorkType, int>>("WorkMinAge", o),
            Ranged = e.Bool("Ranged"),
            Hunts = e.Bool("Hunts"),
            Inspiration = e.Get<Fact<string>>("Inspiration", o),
            WorkHours = e.Get<Fact<int>>("WorkHours", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnProfile v, Codecs o)
    {
        w.Put("Id", v.Id, o);
        w.Put("Skills", v.Skills, o);
        w.Put("Traits", v.Traits, o);
        w.Put("Effects", v.Effects, o);
        w.Put("Incapable", v.Incapable, o);
        w.Put("WorkSkill", v.WorkSkill, o);
        w.WriteNumber("Age", v.Age);
        w.WriteBoolean("Child", v.Child);
        w.Put("Biotech", v.Biotech, o);
        w.Put("Genes", v.Genes, o);
        w.Put("WorkMinAge", v.WorkMinAge, o);
        w.WriteBoolean("Ranged", v.Ranged);
        w.WriteBoolean("Hunts", v.Hunts);
        w.Put("Inspiration", v.Inspiration, o);
        w.Put("WorkHours", v.WorkHours, o);
    }
}

internal sealed class PawnTraitConverter : ObjectCodec<PawnTrait>
{
    protected override PawnTrait Read(JsonElement e, Codecs o) =>
        new()
        {
            Name = e.Str("Name"),
            Degree = e.I32("Degree"),
            Effects = e.Get<TraitEffects>("Effects", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnTrait v, Codecs o)
    {
        w.WriteString("Name", v.Name);
        w.WriteNumber("Degree", v.Degree);
        w.Put("Effects", v.Effects, o);
    }
}

internal sealed class PhaseConverter() : StringCodec<Phase>(s => new(s), v => v.Value);
internal sealed class PlannedLampConverter : ObjectCodec<PlannedLamp>
{
    protected override PlannedLamp Read(JsonElement e, Codecs o) =>
        new()
        {
            Def = e.Str("Def"),
            Cell = e.Get<Cell>("Cell", o),
            Facing = e.Get<Rotation>("Facing", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PlannedLamp v, Codecs o)
    {
        w.WriteString("Def", v.Def);
        w.Put("Cell", v.Cell, o);
        w.Put("Facing", v.Facing, o);
    }
}

internal sealed class PlannedRoomConverter : ObjectCodec<PlannedRoom>
{
    protected override PlannedRoom Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.I32("Id"),
            Replaces = e.I32("Replaces"),
            Role = e.Get<PlannedRole>("Role", o),
            Region = e.Get<Region>("Region", o),
            Door = e.Get<Cell>("Door", o),
            DoorRot = e.GetOr<Rotation?>("DoorRot", null, o),
            Crossing = e.GetOr<RoomCrossing?>("Crossing", null, o),
            Through = e.Bool("Through"),
            Dug = e.Bool("Dug"),
            Outdoor = e.Bool("Outdoor"),
            Facing = e.GetOr<Rotation?>("Facing", null, o),
            Markers = e.I32("Markers"),
            Parent = e.I32("Parent"),
            WorkChairs = e.Get<IReadOnlyDictionary<string, string>>("WorkChairs", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PlannedRoom v, Codecs o)
    {
        w.WriteNumber("Id", v.Id);
        w.WriteNumber("Replaces", v.Replaces);
        w.Put("Role", v.Role, o);
        w.Put("Region", v.Region, o);
        w.Put("Door", v.Door, o);
        if (v.DoorRot is { } vDoorRot)
        {
            w.Put("DoorRot", vDoorRot, o);
        }
        if (v.Crossing is { } vCrossing)
        {
            w.Put("Crossing", vCrossing, o);
        }
        w.WriteBoolean("Through", v.Through);
        w.WriteBoolean("Dug", v.Dug);
        w.WriteBoolean("Outdoor", v.Outdoor);
        if (v.Facing is { } vFacing)
        {
            w.Put("Facing", vFacing, o);
        }
        w.WriteNumber("Markers", v.Markers);
        w.WriteNumber("Parent", v.Parent);
        w.Put("WorkChairs", v.WorkChairs, o);
    }
}

internal sealed class PlannedSiteConverter : ObjectCodec<PlannedSite>
{
    protected override PlannedSite Read(JsonElement e, Codecs o) =>
        new()
        {
            Node = e.Str("Node"),
            Origin = e.Get<Cell>("Origin", o),
            Footprint = e.Get<Region>("Footprint", o),
            Lanes = e.Get<IReadOnlyList<SiteLane>>("Lanes", o),
            Rotation = e.GetOr<Rotation?>("Rotation", null, o),
            Anchored = e.Bool("Anchored"),
            Replaces = e.I32("Replaces"),
            Kind = e.Get<NodeKind>("Kind", o),
            ReplacesSites = e.Get<IReadOnlyList<string>>("ReplacesSites", o),
            Reason = e.Str("Reason"),
            Ring = e.GetOr<RingFacts?>("Ring", null, o),
            Tier = e.GetOr<TierFacts?>("Tier", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PlannedSite v, Codecs o)
    {
        w.WriteString("Node", v.Node);
        w.Put("Origin", v.Origin, o);
        w.Put("Footprint", v.Footprint, o);
        w.Put("Lanes", v.Lanes, o);
        if (v.Rotation is { } vRotation)
        {
            w.Put("Rotation", vRotation, o);
        }
        w.WriteBoolean("Anchored", v.Anchored);
        w.WriteNumber("Replaces", v.Replaces);
        w.Put("Kind", v.Kind, o);
        w.Put("ReplacesSites", v.ReplacesSites, o);
        w.WriteString("Reason", v.Reason);
        if (v.Ring is { } vRing)
        {
            w.Put("Ring", vRing, o);
        }
        if (v.Tier is { } vTier)
        {
            w.Put("Tier", vTier, o);
        }
    }
}

internal sealed class PodArrivalConverter : ObjectCodec<PodArrival>
{
    protected override PodArrival Read(JsonElement e, Codecs o) =>
        new()
        {
            Landing = e.Get<EquatableArray<Cell>>("Landing", o),
            Open = e.Get<Tick>("Open", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PodArrival v, Codecs o)
    {
        w.Put("Landing", v.Landing, o);
        w.Put("Open", v.Open, o);
    }
}

internal sealed class PodDoorConverter : ObjectCodec<PodDoor>
{
    protected override PodDoor Read(JsonElement e, Codecs o) =>
        new()
        {
            Cell = e.Get<Cell>("Cell", o),
            Mode = e.Get<DoorMode>("Mode", o),
            Sent = e.Bool("Sent"),
            Repairer = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Repairer", o),
            Opener = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Opener", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PodDoor v, Codecs o)
    {
        w.Put("Cell", v.Cell, o);
        w.Put("Mode", v.Mode, o);
        w.WriteBoolean("Sent", v.Sent);
        w.Put("Repairer", v.Repairer, o);
        w.Put("Opener", v.Opener, o);
    }
}

internal sealed class ProductionLadderRecordConverter : ObjectCodec<ProductionLadderRecord>
{
    protected override ProductionLadderRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            Tick = e.Get<Tick>("Tick", o),
            Resource = e.Get<Resource>("Resource", o),
            Bench = e.Str("Bench"),
            Recipe = e.Str("Recipe"),
            Research = e.Get<IReadOnlyList<string>>("Research", o),
            Concern = e.Get<ConcernId>("Concern", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ProductionLadderRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Tick", v.Tick, o);
        w.Put("Resource", v.Resource, o);
        w.WriteString("Bench", v.Bench);
        w.WriteString("Recipe", v.Recipe);
        w.Put("Research", v.Research, o);
        w.Put("Concern", v.Concern, o);
    }
}

internal sealed class ProfileSkillConverter : ObjectCodec<ProfileSkill>
{
    protected override ProfileSkill Read(JsonElement e, Codecs o) =>
        new()
        {
            Name = e.Str("Name"),
            Level = e.I32("Level"),
            Stored = e.I32("Stored"),
            Passion = e.Str("Passion"),
            Disabled = e.Bool("Disabled"),
        };

    protected override void WriteFields(Utf8JsonWriter w, ProfileSkill v, Codecs o)
    {
        w.WriteString("Name", v.Name);
        w.WriteNumber("Level", v.Level);
        w.WriteNumber("Stored", v.Stored);
        w.WriteString("Passion", v.Passion);
        w.WriteBoolean("Disabled", v.Disabled);
    }
}

internal sealed class ProgressCooldownConverter : ObjectCodec<ProgressCooldown>
{
    protected override ProgressCooldown Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Key"),
            e.Get<Tick>("Until", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, ProgressCooldown v, Codecs o)
    {
        w.WriteString("Key", v.Key ?? "");
        w.Put("Until", v.Until, o);
    }
}

internal sealed class ReadyClaimConverter : ObjectCodec<ReadyClaim>
{
    protected override ReadyClaim Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Kind"),
            e.Str("Key")
        );

    protected override void WriteFields(Utf8JsonWriter w, ReadyClaim v, Codecs o)
    {
        w.WriteString("Kind", v.Kind ?? "");
        w.WriteString("Key", v.Key ?? "");
    }
}

internal sealed class ReadyDeferralConverter : ObjectCodec<ReadyDeferral>
{
    protected override ReadyDeferral Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ConcernId>("Concern", o),
            e.Str("Reason"),
            e.I32("Count")
        );

    protected override void WriteFields(Utf8JsonWriter w, ReadyDeferral v, Codecs o)
    {
        w.Put("Concern", v.Concern, o);
        w.WriteString("Reason", v.Reason);
        w.WriteNumber("Count", v.Count);
    }
}

internal sealed class ReadyWorkConverter : ObjectCodec<ReadyWork>
{
    protected override ReadyWork Read(JsonElement e, Codecs o) =>
        new()
        {
            ID = e.Get<ReadyWorkId>("ID", o),
            Concerns = e.Get<EquatableArray<ConcernId>>("Concerns", o),
            Method = e.Get<MethodId>("Method", o),
            Plan = e.Get<PlanId>("Plan", o),
            Action = e.Get<ActionId>("Action", o),
            Stage = e.Str("Stage"),
            Work = e.Get<EquatableArray<WorkType>>("Work", o),
            State = e.Get<ReadyState>("State", o),
            Reason = e.Str("Reason"),
            Requires = e.Get<EquatableArray<string>>("Requires", o),
            Claims = e.Get<EquatableArray<ReadyClaim>>("Claims", o),
            Alternative = e.Str("Alternative"),
            Parallelism = e.I32("Parallelism"),
        };

    protected override void WriteFields(Utf8JsonWriter w, ReadyWork v, Codecs o)
    {
        w.Put("ID", v.ID, o);
        w.Put("Concerns", v.Concerns, o);
        w.Put("Method", v.Method, o);
        w.Put("Plan", v.Plan, o);
        w.Put("Action", v.Action, o);
        w.WriteString("Stage", v.Stage);
        w.Put("Work", v.Work, o);
        w.Put("State", v.State, o);
        w.WriteString("Reason", v.Reason);
        w.Put("Requires", v.Requires, o);
        w.Put("Claims", v.Claims, o);
        w.WriteString("Alternative", v.Alternative);
        w.WriteNumber("Parallelism", v.Parallelism);
    }
}

internal sealed class ReadyWorkIdConverter() : StringCodec<ReadyWorkId>(s => new(s), v => v.Value);
internal sealed class ReadyWorkReportConverter : ObjectCodec<ReadyWorkReport>
{
    protected override ReadyWorkReport Read(JsonElement e, Codecs o) =>
        new()
        {
            Colony = e.Get<ColonyId>("Colony", o),
            Map = e.Get<MapId>("Map", o),
            Load = e.Get<LoadId>("Load", o),
            Tick = e.Get<Tick>("Tick", o),
            Candidates = e.Get<EquatableArray<ReadyWork>>("Candidates", o),
            Deferred = e.Get<EquatableArray<ReadyDeferral>>("Deferred", o),
            Continuation = e.Str("Continuation"),
        };

    protected override void WriteFields(Utf8JsonWriter w, ReadyWorkReport v, Codecs o)
    {
        w.Put("Colony", v.Colony, o);
        w.Put("Map", v.Map, o);
        w.Put("Load", v.Load, o);
        w.Put("Tick", v.Tick, o);
        w.Put("Candidates", v.Candidates, o);
        w.Put("Deferred", v.Deferred, o);
        w.WriteString("Continuation", v.Continuation);
    }
}

internal sealed class RecoveryEntryConverter : ObjectCodec<RecoveryEntry>
{
    protected override RecoveryEntry Read(JsonElement e, Codecs o) =>
        new()
        {
            ID = e.Str("ID"),
            Def = e.Str("Def"),
            Kind = e.Get<RemoteWorkKind>("Kind", o),
            Tier = e.Get<RecoveryTier>("Tier", o),
            Distance = e.Dbl("Distance"),
            Status = e.Get<RecoveryStatus>("Status", o),
            Reason = e.Str("Reason"),
            Short = e.Get<EquatableArray<Resource>>("Short", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RecoveryEntry v, Codecs o)
    {
        w.WriteString("ID", v.ID);
        w.WriteString("Def", v.Def);
        w.Put("Kind", v.Kind, o);
        w.Put("Tier", v.Tier, o);
        w.WriteNumber("Distance", v.Distance);
        w.Put("Status", v.Status, o);
        w.WriteString("Reason", v.Reason);
        w.Put("Short", v.Short, o);
    }
}

internal sealed class RecoveryQueueConverter : ObjectCodec<RecoveryQueue>
{
    protected override RecoveryQueue Read(JsonElement e, Codecs o) =>
        new()
        {
            Entries = e.Get<EquatableArray<RecoveryEntry>>("Entries", o),
            Admitted = e.Str("Admitted"),
        };

    protected override void WriteFields(Utf8JsonWriter w, RecoveryQueue v, Codecs o)
    {
        w.Put("Entries", v.Entries, o);
        w.WriteString("Admitted", v.Admitted);
    }
}

internal sealed class RecoveryWorkConverter : ObjectCodec<RecoveryWork>
{
    protected override RecoveryWork Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Building"),
            e.Get<global::RimGovernor.Brain.Policy.RecoveryMethod>("Method", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, RecoveryWork v, Codecs o)
    {
        w.WriteString("Building", v.Building);
        w.Put("Method", v.Method, o);
    }
}

internal sealed class RectangleConverter : ObjectCodec<Rectangle>
{
    protected override Rectangle Read(JsonElement e, Codecs o) =>
        new(
            e.I32("X"),
            e.I32("Z"),
            e.I32("Width"),
            e.I32("Height")
        );

    protected override void WriteFields(Utf8JsonWriter w, Rectangle v, Codecs o)
    {
        w.WriteNumber("X", v.X);
        w.WriteNumber("Z", v.Z);
        w.WriteNumber("Width", v.Width);
        w.WriteNumber("Height", v.Height);
    }
}

internal sealed class RegionConverter : ObjectCodec<Region>
{
    protected override Region Read(JsonElement e, Codecs o) =>
        new()
        {
            Runs = e.Get<EquatableArray<RowRun>>("Runs", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, Region v, Codecs o)
    {
        w.Put("Runs", v.Runs, o);
    }
}

internal sealed class ReserveSupplyConverter : ObjectCodec<ReserveSupply>
{
    protected override ReserveSupply Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Thing"),
            e.Str("Definition"),
            e.Bool("Forbid")
        );

    protected override void WriteFields(Utf8JsonWriter w, ReserveSupply v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.WriteBoolean("Forbid", v.Forbid);
    }
}

internal sealed class ResourceConverter() : StringCodec<Resource>(s => new(s), v => v.Value);
internal sealed class RingFactsConverter : ObjectCodec<RingFacts>
{
    protected override RingFacts Read(JsonElement e, Codecs o) =>
        new()
        {
            Light = e.Get<Region>("Light", o),
            Bridged = e.Get<Region>("Bridged", o),
            Gaps = e.Get<Region>("Gaps", o),
            Inner = e.Get<Region>("Inner", o),
            Sealed = e.Get<Region>("Sealed", o),
            Opening = e.Str("Opening"),
            Revision = e.I32("Revision"),
        };

    protected override void WriteFields(Utf8JsonWriter w, RingFacts v, Codecs o)
    {
        w.Put("Light", v.Light, o);
        w.Put("Bridged", v.Bridged, o);
        w.Put("Gaps", v.Gaps, o);
        w.Put("Inner", v.Inner, o);
        w.Put("Sealed", v.Sealed, o);
        w.WriteString("Opening", v.Opening);
        w.WriteNumber("Revision", v.Revision);
    }
}

internal sealed class RoomCrossingConverter : ObjectCodec<RoomCrossing>
{
    protected override RoomCrossing Read(JsonElement e, Codecs o) =>
        new()
        {
            Cold = e.Bool("Cold"),
            Beds = e.I32("Beds"),
            Clean = e.Bool("Clean"),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoomCrossing v, Codecs o)
    {
        w.WriteBoolean("Cold", v.Cold);
        w.WriteNumber("Beds", v.Beds);
        w.WriteBoolean("Clean", v.Clean);
    }
}

internal sealed class RoomLinkConverter : ObjectCodec<RoomLink>
{
    protected override RoomLink Read(JsonElement e, Codecs o) =>
        new(
            e.I32("A"),
            e.I32("B"),
            e.Get<Cell>("Cell", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, RoomLink v, Codecs o)
    {
        w.WriteNumber("A", v.A);
        w.WriteNumber("B", v.B);
        w.Put("Cell", v.Cell, o);
    }
}

internal sealed class RoomTripConverter : ObjectCodec<RoomTrip>
{
    protected override RoomTrip Read(JsonElement e, Codecs o) =>
        new(
            e.I32("A"),
            e.I32("B"),
            e.Dbl("Weight")
        );

    protected override void WriteFields(Utf8JsonWriter w, RoomTrip v, Codecs o)
    {
        w.WriteNumber("A", v.A);
        w.WriteNumber("B", v.B);
        w.WriteNumber("Weight", v.Weight);
    }
}

internal sealed class RoundsConverter : ObjectCodec<Rounds>
{
    protected override Rounds Read(JsonElement e, Codecs o) =>
        new()
        {
            Emergency = e.Get<EquatableArray<ConcernId>>("Emergency", o),
            Unsafe = e.Get<EquatableArray<string>>("Unsafe", o),
            BrewingFinished = e.Bool("BrewingFinished"),
            ResourceRunways = e.Get<EquatableArray<ResourceRunwayRecord>>("ResourceRunways", o),
            ReserveSupplies = e.Get<EquatableArray<ReserveSupply>>("ReserveSupplies", o),
            LarderSupplies = e.Get<EquatableArray<StartingSupply>>("LarderSupplies", o),
            RecoveryQueue = e.GetOr<RecoveryQueue?>("RecoveryQueue", null, o),
            ShrineHolds = e.Get<EquatableArray<ShrineHold>>("ShrineHolds", o),
            ShrineStep = e.GetOr<RoundsShrineStep?>("ShrineStep", null, o),
            Recovery = e.GetOr<RoundsRecovery?>("Recovery", null, o),
            Disaster = e.GetOr<DisasterHistory?>("Disaster", null, o),
            Mood = e.GetOr<RoundsMood?>("Mood", null, o),
            MoodMethods = e.Get<EquatableArray<RoundsMoodMethod>>("MoodMethods", o),
            Sleeping = e.Get<SleepingHistory>("Sleeping", o),
            Revision = e.U64("Revision"),
            Snapshot = e.Get<GenerationSnapshot>("Snapshot", o),
            Tick = e.Get<Tick>("Tick", o),
            OrdinaryTick = e.Get<Tick>("OrdinaryTick", o),
            Immediate = e.Bool("Immediate"),
            Enabled = e.Bool("Enabled"),
            Latches = e.Get<RoundsLatches>("Latches", o),
            MedicalCare = e.Get<MedicalCareHistory>("MedicalCare", o),
            EventLoot = e.Get<EventLootHistory>("EventLoot", o),
            Comfort = e.Get<ComfortHistory>("Comfort", o),
            Standards = e.Get<EquatableArray<RoundsStandard>>("Standards", o),
            Incidents = e.Get<EquatableArray<RoundsIncident>>("Incidents", o),
            Roster = e.GetOr<WorkRosterReport?>("Roster", null, o),
            Progress = e.Get<EquatableArray<ConcernProgress>>("Progress", o),
            Stage = e.GetOr<ColonyStageRecord?>("Stage", null, o),
            ReadyWork = e.GetOr<ReadyWorkReport?>("ReadyWork", null, o),
            Built = e.Get<EquatableArray<ActionId>>("Built", o),
            WallCells = e.Get<EquatableArray<WallCell>>("WallCells", o),
            Dependencies = e.Get<EquatableArray<DependencyRecord>>("Dependencies", o),
            NoOps = e.Get<EquatableArray<NoOpRecord>>("NoOps", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, Rounds v, Codecs o)
    {
        w.Put("Emergency", v.Emergency, o);
        w.Put("Unsafe", v.Unsafe, o);
        w.WriteBoolean("BrewingFinished", v.BrewingFinished);
        w.Put("ResourceRunways", v.ResourceRunways, o);
        w.Put("ReserveSupplies", v.ReserveSupplies, o);
        w.Put("LarderSupplies", v.LarderSupplies, o);
        if (v.RecoveryQueue is { } vRecoveryQueue)
        {
            w.Put("RecoveryQueue", vRecoveryQueue, o);
        }
        w.Put("ShrineHolds", v.ShrineHolds, o);
        if (v.ShrineStep is { } vShrineStep)
        {
            w.Put("ShrineStep", vShrineStep, o);
        }
        if (v.Recovery is { } vRecovery)
        {
            w.Put("Recovery", vRecovery, o);
        }
        if (v.Disaster is { } vDisaster)
        {
            w.Put("Disaster", vDisaster, o);
        }
        if (v.Mood is { } vMood)
        {
            w.Put("Mood", vMood, o);
        }
        w.Put("MoodMethods", v.MoodMethods, o);
        w.Put("Sleeping", v.Sleeping, o);
        w.WriteNumber("Revision", v.Revision);
        w.Put("Snapshot", v.Snapshot, o);
        w.Put("Tick", v.Tick, o);
        w.Put("OrdinaryTick", v.OrdinaryTick, o);
        w.WriteBoolean("Immediate", v.Immediate);
        w.WriteBoolean("Enabled", v.Enabled);
        w.Put("Latches", v.Latches, o);
        w.Put("MedicalCare", v.MedicalCare, o);
        w.Put("EventLoot", v.EventLoot, o);
        w.Put("Comfort", v.Comfort, o);
        w.Put("Standards", v.Standards, o);
        w.Put("Incidents", v.Incidents, o);
        if (v.Roster is { } vRoster)
        {
            w.Put("Roster", vRoster, o);
        }
        w.Put("Progress", v.Progress, o);
        if (v.Stage is { } vStage)
        {
            w.Put("Stage", vStage, o);
        }
        if (v.ReadyWork is { } vReadyWork)
        {
            w.Put("ReadyWork", vReadyWork, o);
        }
        w.Put("Built", v.Built, o);
        w.Put("WallCells", v.WallCells, o);
        w.Put("Dependencies", v.Dependencies, o);
        w.Put("NoOps", v.NoOps, o);
    }
}

internal sealed class RoundsLatchesConverter : ObjectCodec<RoundsLatches>
{
    protected override RoundsLatches Read(JsonElement e, Codecs o) =>
        new()
        {
            HomeCoverage = e.Bool("HomeCoverage"),
            StoneShell = e.Bool("StoneShell"),
            Sleeping = e.Bool("Sleeping"),
            Animals = e.Get<AnimalUpkeepHistory>("Animals", o),
            MedicalReserve = e.Bool("MedicalReserve"),
            FoodStorage = e.Bool("FoodStorage"),
            Refrigeration = e.Bool("Refrigeration"),
            RefrigerationSince = e.Get<Tick>("RefrigerationSince", o),
            Lighting = e.Get<EquatableArray<string>>("Lighting", o),
            Flooring = e.Get<EquatableArray<string>>("Flooring", o),
            Routes = e.Get<EquatableArray<string>>("Routes", o),
            Food = e.Bool("Food"),
            Cold = e.Bool("Cold"),
            Hot = e.Bool("Hot"),
            Wood = e.Bool("Wood"),
            Upkeep = e.Get<UpkeepHistory>("Upkeep", o),
            Dash = e.Get<DashWindow>("Dash", o),
            Soldiers = e.Bool("Soldiers"),
            FoodFirst = e.Bool("FoodFirst"),
            Housing = e.Get<Phase>("Housing", o),
            Comfort = e.Get<Phase>("Comfort", o),
            Medical = e.Get<Phase>("Medical", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsLatches v, Codecs o)
    {
        w.WriteBoolean("HomeCoverage", v.HomeCoverage);
        w.WriteBoolean("StoneShell", v.StoneShell);
        w.WriteBoolean("Sleeping", v.Sleeping);
        w.Put("Animals", v.Animals, o);
        w.WriteBoolean("MedicalReserve", v.MedicalReserve);
        w.WriteBoolean("FoodStorage", v.FoodStorage);
        w.WriteBoolean("Refrigeration", v.Refrigeration);
        w.Put("RefrigerationSince", v.RefrigerationSince, o);
        w.Put("Lighting", v.Lighting, o);
        w.Put("Flooring", v.Flooring, o);
        w.Put("Routes", v.Routes, o);
        w.WriteBoolean("Food", v.Food);
        w.WriteBoolean("Cold", v.Cold);
        w.WriteBoolean("Hot", v.Hot);
        w.WriteBoolean("Wood", v.Wood);
        w.Put("Upkeep", v.Upkeep, o);
        w.Put("Dash", v.Dash, o);
        w.WriteBoolean("Soldiers", v.Soldiers);
        w.WriteBoolean("FoodFirst", v.FoodFirst);
        w.Put("Housing", v.Housing, o);
        w.Put("Comfort", v.Comfort, o);
        w.Put("Medical", v.Medical, o);
    }
}

internal sealed class RoundsStandardConverter : ObjectCodec<RoundsStandard>
{
    protected override RoundsStandard Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ConcernId>("Concern", o),
            e.Get<StandardId>("Standard", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, RoundsStandard v, Codecs o)
    {
        w.Put("Concern", v.Concern, o);
        w.Put("Standard", v.Standard, o);
    }
}

internal sealed class RowRunConverter : ObjectCodec<RowRun>
{
    protected override RowRun Read(JsonElement e, Codecs o) =>
        new(
            e.I32("Z"),
            e.I32("X"),
            e.I32("Length")
        );

    protected override void WriteFields(Utf8JsonWriter w, RowRun v, Codecs o)
    {
        w.WriteNumber("Z", v.Z);
        w.WriteNumber("X", v.X);
        w.WriteNumber("Length", v.Length);
    }
}

internal sealed class SafeguardProposalConverter : ObjectCodec<SafeguardProposal>
{
    protected override SafeguardProposal Read(JsonElement e, Codecs o) =>
        new()
        {
            Need = e.Get<ConcernId>("Need", o),
            Priority = e.I32("Priority"),
        };

    protected override void WriteFields(Utf8JsonWriter w, SafeguardProposal v, Codecs o)
    {
        w.Put("Need", v.Need, o);
        w.WriteNumber("Priority", v.Priority);
    }
}

internal sealed class ShrineHoldConverter : ObjectCodec<ShrineHold>
{
    protected override ShrineHold Read(JsonElement e, Codecs o) =>
        new()
        {
            Shrine = e.Str("Shrine"),
            Reason = e.Str("Reason"),
            Wall = e.Str("Wall"),
            Casket = e.Str("Casket"),
            Occupant = e.Str("Occupant"),
            Planner = e.Str("Planner"),
        };

    protected override void WriteFields(Utf8JsonWriter w, ShrineHold v, Codecs o)
    {
        w.WriteString("Shrine", v.Shrine);
        w.WriteString("Reason", v.Reason);
        w.WriteString("Wall", v.Wall);
        w.WriteString("Casket", v.Casket);
        w.WriteString("Occupant", v.Occupant);
        w.WriteString("Planner", v.Planner);
    }
}

internal sealed class SiteLaneConverter : ObjectCodec<SiteLane>
{
    protected override SiteLane Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Str("Id"),
            Region = e.Get<Region>("Region", o),
            Reason = e.Str("Reason"),
            Wind = e.Bool("Wind"),
            Blocked = e.I32("Blocked"),
        };

    protected override void WriteFields(Utf8JsonWriter w, SiteLane v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.Put("Region", v.Region, o);
        w.WriteString("Reason", v.Reason);
        w.WriteBoolean("Wind", v.Wind);
        w.WriteNumber("Blocked", v.Blocked);
    }
}

internal sealed class SleepingHistoryConverter : ObjectCodec<SleepingHistory>
{
    protected override SleepingHistory Read(JsonElement e, Codecs o) =>
        new()
        {
            Uses = e.Get<EquatableArray<SleepingUse>>("Uses", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, SleepingHistory v, Codecs o)
    {
        w.Put("Uses", v.Uses, o);
    }
}

internal sealed class SleepingUseConverter : ObjectCodec<SleepingUse>
{
    protected override SleepingUse Read(JsonElement e, Codecs o) =>
        new(
            e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            e.Str("Bed"),
            e.Get<Tick>("Tick", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, SleepingUse v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteString("Bed", v.Bed);
        w.Put("Tick", v.Tick, o);
    }
}

internal sealed class SoldierSquadRecordConverter : ObjectCodec<SoldierSquadRecord>
{
    protected override SoldierSquadRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            Members = e.Get<IReadOnlyList<global::RimGovernor.Brain.Domain.PawnId>>("Members", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, SoldierSquadRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Members", v.Members, o);
    }
}

internal sealed class StartingSupplyConverter : ObjectCodec<StartingSupply>
{
    protected override StartingSupply Read(JsonElement e, Codecs o) =>
        new()
        {
            Thing = e.Str("Thing"),
            Definition = e.Str("Definition"),
            Cell = e.Get<Cell>("Cell", o),
            Forbid = e.Bool("Forbid"),
        };

    protected override void WriteFields(Utf8JsonWriter w, StartingSupply v, Codecs o)
    {
        w.WriteString("Thing", v.Thing);
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
        w.WriteBoolean("Forbid", v.Forbid);
    }
}

internal sealed class StatModifierConverter : ObjectCodec<StatModifier>
{
    protected override StatModifier Read(JsonElement e, Codecs o) =>
        new(
            e.Dbl("Offset"),
            e.Dbl("Factor")
        );

    protected override void WriteFields(Utf8JsonWriter w, StatModifier v, Codecs o)
    {
        w.WriteNumber("Offset", v.Offset);
        w.WriteNumber("Factor", v.Factor);
    }
}

internal sealed class StopEventConverter : ObjectCodec<StopEvent>
{
    protected override StopEvent Read(JsonElement e, Codecs o) =>
        new()
        {
            Kind = e.Get<CombatStopKind>("Kind", o),
            Pawn = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Pawn", o),
            Target = e.Get<global::RimGovernor.Brain.Policy.PawnId>("Target", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, StopEvent v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.Put("Pawn", v.Pawn, o);
        w.Put("Target", v.Target, o);
    }
}

internal sealed class TierFactsConverter : ObjectCodec<TierFacts>
{
    protected override TierFacts Read(JsonElement e, Codecs o) =>
        new()
        {
            Under = e.Str("Under"),
            Tier = e.Get<DefenseTierName>("Tier", o),
            Places = e.Get<EquatableArray<TierPlacement>>("Places", o),
            Reserved = e.Get<EquatableArray<Cell>>("Reserved", o),
            Path = e.Get<EquatableArray<Cell>>("Path", o),
            Firing = e.Get<EquatableArray<FiringPosition>>("Firing", o),
            Pinned = e.Bool("Pinned"),
        };

    protected override void WriteFields(Utf8JsonWriter w, TierFacts v, Codecs o)
    {
        w.WriteString("Under", v.Under);
        w.Put("Tier", v.Tier, o);
        w.Put("Places", v.Places, o);
        w.Put("Reserved", v.Reserved, o);
        w.Put("Path", v.Path, o);
        w.Put("Firing", v.Firing, o);
        w.WriteBoolean("Pinned", v.Pinned);
    }
}

internal sealed class TierPlacementConverter : ObjectCodec<TierPlacement>
{
    protected override TierPlacement Read(JsonElement e, Codecs o) =>
        new(
            e.Str("Definition"),
            e.Get<Cell>("Cell", o),
            e.Get<Rotation>("Rotation", o),
            e.Str("Stuff")
        );

    protected override void WriteFields(Utf8JsonWriter w, TierPlacement v, Codecs o)
    {
        w.WriteString("Definition", v.Definition);
        w.Put("Cell", v.Cell, o);
        w.Put("Rotation", v.Rotation, o);
        w.WriteString("Stuff", v.Stuff);
    }
}

internal sealed class TraitEffectsConverter : ObjectCodec<TraitEffects>
{
    protected override TraitEffects Read(JsonElement e, Codecs o) =>
        new()
        {
            WorkSpeed = e.Dbl("WorkSpeed"),
            LearnRate = e.Dbl("LearnRate"),
            MoveSpeed = e.Dbl("MoveSpeed"),
            QuickSleeper = e.Bool("QuickSleeper"),
            NightShift = e.Bool("NightShift"),
            MeleeOnly = e.Bool("MeleeOnly"),
            FrontLine = e.Bool("FrontLine"),
            RearRanged = e.Bool("RearRanged"),
            DisabledWork = e.Get<IReadOnlyList<WorkType>>("DisabledWork", o),
            Pyromaniac = e.Bool("Pyromaniac"),
            Sociable = e.I32("Sociable"),
            Execution = e.Bool("Execution"),
            SurgeonSafe = e.Bool("SurgeonSafe"),
            TaintFree = e.Bool("TaintFree"),
            HumanButcher = e.Bool("HumanButcher"),
            Nudist = e.Bool("Nudist"),
            Ascetic = e.Bool("Ascetic"),
            Cannibal = e.Bool("Cannibal"),
            Gourmand = e.Bool("Gourmand"),
            ChemicalInterest = e.I32("ChemicalInterest"),
            Undergrounder = e.Bool("Undergrounder"),
            Greedy = e.Bool("Greedy"),
            Jealous = e.Bool("Jealous"),
        };

    protected override void WriteFields(Utf8JsonWriter w, TraitEffects v, Codecs o)
    {
        w.WriteNumber("WorkSpeed", v.WorkSpeed);
        w.WriteNumber("LearnRate", v.LearnRate);
        w.WriteNumber("MoveSpeed", v.MoveSpeed);
        w.WriteBoolean("QuickSleeper", v.QuickSleeper);
        w.WriteBoolean("NightShift", v.NightShift);
        w.WriteBoolean("MeleeOnly", v.MeleeOnly);
        w.WriteBoolean("FrontLine", v.FrontLine);
        w.WriteBoolean("RearRanged", v.RearRanged);
        w.Put("DisabledWork", v.DisabledWork, o);
        w.WriteBoolean("Pyromaniac", v.Pyromaniac);
        w.WriteNumber("Sociable", v.Sociable);
        w.WriteBoolean("Execution", v.Execution);
        w.WriteBoolean("SurgeonSafe", v.SurgeonSafe);
        w.WriteBoolean("TaintFree", v.TaintFree);
        w.WriteBoolean("HumanButcher", v.HumanButcher);
        w.WriteBoolean("Nudist", v.Nudist);
        w.WriteBoolean("Ascetic", v.Ascetic);
        w.WriteBoolean("Cannibal", v.Cannibal);
        w.WriteBoolean("Gourmand", v.Gourmand);
        w.WriteNumber("ChemicalInterest", v.ChemicalInterest);
        w.WriteBoolean("Undergrounder", v.Undergrounder);
        w.WriteBoolean("Greedy", v.Greedy);
        w.WriteBoolean("Jealous", v.Jealous);
    }
}

internal sealed class TransitionConverter : ObjectCodec<Transition>
{
    protected override Transition Read(JsonElement e, Codecs o) =>
        new(
            e.Get<TransitionKind>("Kind", o)
        )
        {
            Combat = e.Get<EquatableArray<CombatResult>>("Combat", o),
            Snapshot = e.Get<GenerationSnapshot>("Snapshot", o),
            Tick = e.Get<Tick>("Tick", o),
            Attempt = e.Get<AttemptId>("Attempt", o),
            Receipt = e.Get<Receipt>("Receipt", o),
            Observation = e.GetOr<Observation?>("Observation", null, o),
            Zone = e.Str("Zone"),
            Stockpiles = e.Get<EquatableArray<CreatedZone>>("Stockpiles", o),
            Bill = e.Str("Bill"),
            HeldReasons = e.Get<EquatableArray<HeldReason>>("HeldReasons", o),
            Refusal = e.GetOr<NativeRefusal?>("Refusal", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, Transition v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.Put("Combat", v.Combat, o);
        w.Put("Snapshot", v.Snapshot, o);
        w.Put("Tick", v.Tick, o);
        w.Put("Attempt", v.Attempt, o);
        w.Put("Receipt", v.Receipt, o);
        if (v.Observation is { } vObservation)
        {
            w.Put("Observation", vObservation, o);
        }
        w.WriteString("Zone", v.Zone);
        w.Put("Stockpiles", v.Stockpiles, o);
        w.WriteString("Bill", v.Bill);
        w.Put("HeldReasons", v.HeldReasons, o);
        if (v.Refusal is { } vRefusal)
        {
            w.Put("Refusal", vRefusal, o);
        }
    }
}

internal sealed class UpkeepHistoryConverter : ObjectCodec<UpkeepHistory>
{
    protected override UpkeepHistory Read(JsonElement e, Codecs o) =>
        new()
        {
            Clearance = e.Bool("Clearance"),
            Shrine = e.Bool("Shrine"),
            Fire = e.Bool("Fire"),
            Repairs = e.Bool("Repairs"),
        };

    protected override void WriteFields(Utf8JsonWriter w, UpkeepHistory v, Codecs o)
    {
        w.WriteBoolean("Clearance", v.Clearance);
        w.WriteBoolean("Shrine", v.Shrine);
        w.WriteBoolean("Fire", v.Fire);
        w.WriteBoolean("Repairs", v.Repairs);
    }
}

internal sealed class WaitDoorConverter : ObjectCodec<WaitDoor>
{
    protected override WaitDoor Read(JsonElement e, Codecs o) =>
        new(
            e.Get<Cell>("Door", o),
            e.Get<Cell>("Inside", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, WaitDoor v, Codecs o)
    {
        w.Put("Door", v.Door, o);
        w.Put("Inside", v.Inside, o);
    }
}

internal sealed class WorkCoverageConverter : ObjectCodec<WorkCoverage>
{
    protected override WorkCoverage Read(JsonElement e, Codecs o) =>
        new()
        {
            Work = e.Get<WorkType>("Work", o),
            Demand = e.I32("Demand"),
            Owners = e.I32("Owners"),
            Capable = e.I32("Capable"),
        };

    protected override void WriteFields(Utf8JsonWriter w, WorkCoverage v, Codecs o)
    {
        w.Put("Work", v.Work, o);
        w.WriteNumber("Demand", v.Demand);
        w.WriteNumber("Owners", v.Owners);
        w.WriteNumber("Capable", v.Capable);
    }
}

internal sealed class WorkRosterReportConverter : ObjectCodec<WorkRosterReport>
{
    protected override WorkRosterReport Read(JsonElement e, Codecs o) =>
        new()
        {
            Tick = e.Get<Tick>("Tick", o),
            Coverage = e.Get<EquatableArray<WorkCoverage>>("Coverage", o),
            Decaying = e.Get<EquatableArray<DecayingSkill>>("Decaying", o),
            Profiles = e.Get<EquatableArray<PawnProfile>>("Profiles", o),
            Help = e.GetOr<ConstructionHelpRecord?>("Help", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, WorkRosterReport v, Codecs o)
    {
        w.Put("Tick", v.Tick, o);
        w.Put("Coverage", v.Coverage, o);
        w.Put("Decaying", v.Decaying, o);
        w.Put("Profiles", v.Profiles, o);
        if (v.Help is { } vHelp)
        {
            w.Put("Help", vHelp, o);
        }
    }
}

internal sealed class WorldConverter : ObjectCodec<World>
{
    protected override World Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ColonyId>("Colony", o),
            e.Get<LoadId>("Load", o),
            e.Get<MapId>("Map", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, World v, Codecs o)
    {
        w.Put("Colony", v.Colony, o);
        w.Put("Load", v.Load, o);
        w.Put("Map", v.Map, o);
    }
}

