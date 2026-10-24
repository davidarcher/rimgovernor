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
            Pawn = e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
            Kind = e.Get<CombatOrderKind>("Kind", o),
            Cell = e.Get<Cell>("Cell", o),
            Target = e.Get<RimGovernor.Brain.Domain.PawnId>("Target", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, AnimalOrder v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.Put("Kind", v.Kind, o);
        w.Put("Cell", v.Cell, o);
        w.Put("Target", v.Target, o);
    }
}

internal sealed class BlockedReasonConverter() : StringCodec<BlockedReason>(s => new(s), v => v.Value);
internal sealed class CastMarkConverter : ObjectCodec<CastMark>
{
    protected override CastMark Read(JsonElement e, Codecs o) =>
        new(
            e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
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
        w.WriteNum("Psyfocus", v.Psyfocus);
        w.WriteNum("Entropy", v.Entropy);
        w.WriteNum("Cooldown", v.Cooldown);
    }
}

internal sealed class CauseConverter() : StringCodec<Cause>(s => new(s), v => v.Value);
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
            Pawns = e.Get<List<RimGovernor.Brain.Domain.PawnId>>("Pawns", o),
            Cells = e.Get<List<Cell>>("Cells", o),
            Open = e.Bool("Open"),
            Held = e.Get<List<RimGovernor.Brain.Domain.PawnId>>("Held", o),
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
            Hostiles = e.Get<List<RimGovernor.Brain.Domain.PawnId>>("Hostiles", o),
            Pawns = e.Get<List<RimGovernor.Brain.Domain.PawnId>>("Pawns", o),
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
            Relieved = e.Get<List<RimGovernor.Brain.Domain.PawnId>>("Relieved", o),
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
            Kiter = e.Get<RimGovernor.Brain.Domain.PawnId>("Kiter", o),
            Leading = e.Bool("Leading"),
            Driller = e.Get<RimGovernor.Brain.Domain.PawnId>("Driller", o),
            Wait = e.Bool("Wait"),
            WaitSince = e.Get<Tick>("WaitSince", o),
            WaitDoors = e.Get<EquatableArray<PodDoor>>("WaitDoors", o),
            WaitRooms = e.Get<EquatableArray<WaitDoor>>("WaitRooms", o),
            Scattered = e.Bool("Scattered"),
            Repairs = e.Get<List<Cell>>("Repairs", o),
            LineHeld = e.I32("LineHeld"),
            SapperGuard = e.Has("SapperBreach")
                ? new BreachGuard.Guarding(e.Get<Cell>("SapperBreach", o))
                : new BreachGuard.Unguarded(),
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
            Untrained = e.Get<List<RimGovernor.Brain.Domain.PawnId>>("Untrained", o),
            Groups = e.Get<List<CombatGroup>>("Groups", o),
            NoShells = e.Get<List<string>>("NoShells", o),
            Dosed = e.Get<List<RimGovernor.Brain.Domain.PawnId>>("Dosed", o),
            Permitted = e.Get<EquatableArray<string>>("Permitted", o),
            Casts = e.Get<List<CastMark>>("Casts", o),
            EmpAdapted = e.Get<List<EmpAdaptation>>("EmpAdapted", o),
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
        w.WriteNum("LineHeld", v.LineHeld);
        if (v.SapperGuard is BreachGuard.Guarding guarding)
        {
            w.Put("SapperBreach", guarding.Wall, o);
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
            Pawn = e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
            Kind = e.Get<CombatOrderKind>("Kind", o),
            Cell = e.Get<Cell>("Cell", o),
            Target = e.Get<RimGovernor.Brain.Domain.PawnId>("Target", o),
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

internal sealed class CombatRescueConverter : ObjectCodec<CombatRescue>
{
    protected override CombatRescue Read(JsonElement e, Codecs o) =>
        new()
        {
            Patient = e.Get<RimGovernor.Brain.Domain.PawnId>("Patient", o),
            Rescuer = e.Get<RimGovernor.Brain.Domain.PawnId>("Rescuer", o),
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
            Pawn = e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
            Post = ReadPost(e, "Cell", o),
            Target = e.Get<RimGovernor.Brain.Domain.PawnId>("Target", o),
            Ranged = e.Bool("Ranged"),
            Duty = e.Get<CombatDuty>("Duty", o),
            Retreat = e.Bool("Retreat"),
            Standby = ReadPost(e, "Home", o),
            Fire = e.Has("Mortar")
                ? new RoleFire.CrewMortar(e.Get<Cell>("Mortar", o), e.Get<Cell>("Aim", o), e.Str("Shell"))
                : e.Has("Ground")
                    ? new RoleFire.AtGround(e.Get<Cell>("Ground", o))
                    : new RoleFire.Own(),
            RepairDoor = e.Has("Repair") ? new RoleRepair.Door(e.Get<Cell>("Repair", o)) : new RoleRepair.NoDoor(),
        };

    private static CombatPost ReadPost(JsonElement e, string key, Codecs o) =>
        e.Has(key) ? new CombatPost.At(e.Get<Cell>(key, o)) : new CombatPost.Free();

    protected override void WriteFields(Utf8JsonWriter w, CombatRole v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        if (v.Post is CombatPost.At post)
        {
            w.Put("Cell", post.Cell, o);
        }
        w.Put("Target", v.Target, o);
        w.WriteBoolean("Ranged", v.Ranged);
        w.Put("Duty", v.Duty, o);
        w.WriteBoolean("Retreat", v.Retreat);
        if (v.Standby is CombatPost.At standby)
        {
            w.Put("Home", standby.Cell, o);
        }
        switch (v.Fire)
        {
            case RoleFire.CrewMortar crew:
                w.Put("Mortar", crew.Mortar, o);
                w.Put("Aim", crew.Aim, o);
                w.WriteString("Shell", crew.Shell);
                break;
            case RoleFire.AtGround ground:
                w.Put("Ground", ground.Cell, o);
                break;
            case RoleFire.Own:
                break;
        }
        if (v.RepairDoor is RoleRepair.Door door)
        {
            w.Put("Repair", door.Cell, o);
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
        w.WriteNum("DoorCloseRange", v.DoorCloseRange);
        w.WriteNum("DoorOpenRange", v.DoorOpenRange);
        w.WriteNum("DoorGunners", v.DoorGunners);
        w.WriteNum("FlankMinDefenders", v.FlankMinDefenders);
        w.WriteNum("FlankSize", v.FlankSize);
        w.WriteNum("FlankLateral", v.FlankLateral);
        w.WriteNum("GroupGap", v.GroupGap);
        w.WriteNum("KiteLeadRange", v.KiteLeadRange);
        w.WriteNum("KiteSpeedRatio", v.KiteSpeedRatio);
        w.WriteNum("ScatterCells", v.ScatterCells);
        w.WriteNum("DoseMargin", v.DoseMargin);
        w.WriteNum("RushRange", v.RushRange);
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
            w.WriteNum("Observed", vObserved);
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
            Idle = e.Get<EquatableArray<RimGovernor.Brain.Domain.PawnId>>("Idle", o),
            Helpers = e.Get<EquatableArray<RimGovernor.Brain.Domain.PawnId>>("Helpers", o),
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
        w.WriteNum("Ready", v.Ready);
        w.WriteNum("Unmet", v.Unmet);
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
        w.WriteNum("Value", v.Value);
        w.WriteNum("Exposure", v.Exposure);
    }
}

internal sealed class DashWindowConverter : ObjectCodec<DashWindow>
{
    protected override DashWindow Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
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

/// <summary>The defense layout record; an older save's <c>Standard</c> and <c>Episode</c> (the retired EnsureDefensiveLayout Standard, #3314) are ignored.</summary>
internal sealed class DefenseLayoutRecordConverter : ObjectCodec<DefenseLayoutRecord>
{
    protected override DefenseLayoutRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            Killbox = e.Str("Killbox"),
            KillboxDigest = e.Str("KillboxDigest"),
            Entrances = e.Get<List<Cell>>("Entrances", o),
            Tiers = e.Get<List<DefenseTierRecord>>("Tiers", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, DefenseLayoutRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.WriteString("Killbox", v.Killbox);
        w.WriteString("KillboxDigest", v.KillboxDigest);
        w.Put("Entrances", v.Entrances, o);
        w.Put("Tiers", v.Tiers, o);
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
            Attempts = e.I32("Attempts"),
            Built = e.Bool("Built"),
            Remove = e.Bool("Remove"),
        };

    protected override void WriteFields(Utf8JsonWriter w, DefenseTierRecord v, Codecs o)
    {
        w.Put("Name", v.Name, o);
        w.Put("Nodes", v.Nodes, o);
        w.Put("Buildings", v.Buildings, o);
        w.WriteNum("Attempts", v.Attempts);
        w.WriteBoolean("Built", v.Built);
        w.WriteBoolean("Remove", v.Remove);
    }
}

internal sealed class DisasterConditionConverter : ObjectCodec<DisasterCondition>
{
    protected override DisasterCondition Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Str("Id"),
            Definition = e.Str("Definition"),
            Duration = e.Bool("Permanent")
                ? new RimGovernor.Contracts.Wire.Observations.ConditionDuration.Permanent()
                : new RimGovernor.Contracts.Wire.Observations.ConditionDuration.Timed { Ticks = e.I64("TicksLeft") },
            DisablesPower = e.Bool("DisablesPower"),
        };

    protected override void WriteFields(Utf8JsonWriter w, DisasterCondition v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.WriteString("Definition", v.Definition);
        w.WriteBoolean("Permanent", v.Duration is RimGovernor.Contracts.Wire.Observations.ConditionDuration.Permanent);
        if (v.Duration is RimGovernor.Contracts.Wire.Observations.ConditionDuration.Timed timed)
        {
            w.WriteNum("TicksLeft", timed.Ticks);
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
            ShortCircuit = e.Has("ShortCircuitTick")
                ? new DisasterShortCircuit.Seen(e.Get<Tick>("ShortCircuitTick", o))
                : new DisasterShortCircuit.NoneSeen(),
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
        if (v.ShortCircuit is DisasterShortCircuit.Seen seen)
        {
            w.Put("ShortCircuitTick", seen.Tick, o);
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
            e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
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
            e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
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
            e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
            e.Get<RimGovernor.Brain.Domain.PawnId>("Target", o),
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
    protected override LayoutPlan Read(JsonElement e, Codecs o)
    {
        // A plan saved before pens and yards were rooms holds them (and the barns, which are dropped) as reservations: pens and yards come in as rooms.
        var reservations = new List<LayoutReservation>();
        var legacy = new List<(string Kind, Region Area, string Herd)>();
        if (e.TryGetProperty("Reservations", out var held) && held.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in held.EnumerateArray())
            {
                var kind = item.Str("Kind");
                if (kind is "barn" or "pen" or "yard")
                {
                    legacy.Add((kind, item.Get<Region>("Area", o), item.Str("Herd")));
                }
                else
                {
                    reservations.Add(o.Read<LayoutReservation>(item));
                }
            }
        }
        // A plan saved while barns were rooms holds them as rooms: they leave, with the links that joined them.
        var rooms = new List<PlannedRoom>();
        var dropped = new HashSet<int>();
        if (e.TryGetProperty("Rooms", out var held2) && held2.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in held2.EnumerateArray())
            {
                if (item.Str("Role") == "barn")
                {
                    dropped.Add(item.I32("Id"));
                    continue;
                }
                rooms.Add(o.Read<PlannedRoom>(item));
            }
        }
        return new LayoutPlan
        {
            Entrances = e.Get<IReadOnlyList<Cell>>("Entrances", o),
            Links = [.. e.Get<IReadOnlyList<RoomLink>>("Links", o).Where(l => !dropped.Contains(l.A) && !dropped.Contains(l.B))],
            Conduits = e.Get<IReadOnlyList<ConduitLink>>("Conduits", o),
            Owed = e.Get<IReadOnlyList<PlannedRole>>("Owed", o),
            Trips = e.Get<IReadOnlyList<RoomTrip>>("Trips", o),
            OutsideTrips = e.Get<IReadOnlyList<OutsideTrip>>("OutsideTrips", o),
            Rooms = rooms,
            Zones = e.Get<IReadOnlyList<LayoutZone>>("Zones", o),
            Reservations = reservations,
            Sites = e.Get<IReadOnlyList<PlannedSite>>("Sites", o),
            RetiredGround = e.Get<IReadOnlyList<Region>>("RetiredGround", o),
            Roofed = e.Get<Region>("Roofed", o),
            Lamps = e.Get<IReadOnlyDictionary<int, IReadOnlyList<PlannedLamp>>>("Lamps", o),
            Pieces = e.GetOr<IReadOnlyDictionary<int, IReadOnlyList<InteriorPiece>>>("Pieces", new Dictionary<int, IReadOnlyList<InteriorPiece>>(), o),
            Cold = e.Bool("Cold"),
            Hot = e.Bool("Hot"),
            NextRoomId = e.I32("NextRoomId"),
        }.WithLegacySites(legacy);
    }

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
        w.Put("Pieces", v.Pieces, o);
        w.WriteBoolean("Cold", v.Cold);
        w.WriteBoolean("Hot", v.Hot);
        w.WriteNum("NextRoomId", v.NextRoomId);
    }
}

internal sealed class LayoutReservationConverter : ObjectCodec<LayoutReservation>
{
    protected override LayoutReservation Read(JsonElement e, Codecs o) =>
        new()
        {
            Kind = e.Get<ReservationKind>("Kind", o),
            Area = e.Get<Region>("Area", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, LayoutReservation v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        w.Put("Area", v.Area, o);
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
            Patients = e.Get<IReadOnlyList<RimGovernor.Brain.Domain.PawnId>>("Patients", o),
            Unknown = e.Get<IReadOnlyList<RimGovernor.Brain.Domain.PawnId>>("Unknown", o),
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
        w.WriteNum("TicksInState", v.TicksInState);
    }
}

internal sealed class MoodCauseConverter : ObjectCodec<MoodCause>
{
    protected override MoodCause Read(JsonElement e, Codecs o) =>
        new()
        {
            Need = e.Get<MoodNeed>("Need", o),
            Level = e.Dbl("Level"),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodCause v, Codecs o)
    {
        w.Put("Need", v.Need, o);
        w.WriteNum("Level", v.Level);
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
            Break = e.GetOr<MentalState?>("Break", null, o),
            HighExpectations = e.Bool("HighExpectations"),
            Id = e.Get<RimGovernor.Brain.Domain.PawnId>("Id", o),
            Mood = e.Get<MoodLevel>("Mood", o),
            Threshold = e.Dbl("Threshold"),
            Food = e.Get<RimGovernor.Contracts.Wire.Observations.NeedLevel>("Food", o),
            Rest = e.Get<RimGovernor.Contracts.Wire.Observations.NeedLevel>("Rest", o),
            Joy = e.Get<RimGovernor.Contracts.Wire.Observations.NeedLevel>("Joy", o),
            Mental = e.Bool("Mental"),
            Dead = e.Bool("Dead"),
            Downed = e.Bool("Downed"),
            Drafted = e.Bool("Drafted"),
            PlayerForced = e.Bool("PlayerForced"),
            Thoughts = e.Get<EquatableArray<MoodThought>>("Thoughts", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodPawn v, Codecs o)
    {
        if (v.Break is { } brk)
        {
            w.Put("Break", brk, o);
        }
        w.WriteBoolean("HighExpectations", v.HighExpectations);
        w.Put("Id", v.Id, o);
        w.Put("Mood", v.Mood, o);
        w.WriteNum("Threshold", v.Threshold);
        w.Put("Food", v.Food, o);
        w.Put("Rest", v.Rest, o);
        w.Put("Joy", v.Joy, o);
        w.WriteBoolean("Mental", v.Mental);
        w.WriteBoolean("Dead", v.Dead);
        w.WriteBoolean("Downed", v.Downed);
        w.WriteBoolean("Drafted", v.Drafted);
        w.WriteBoolean("PlayerForced", v.PlayerForced);
        w.Put("Thoughts", v.Thoughts, o);
    }
}

/// <summary>A pawn's need level as a persisted state: <c>has</c> with its level, or <c>lacks</c> (#3768).</summary>
internal sealed class NeedLevelConverter : ObjectCodec<RimGovernor.Contracts.Wire.Observations.NeedLevel>
{
    protected override RimGovernor.Contracts.Wire.Observations.NeedLevel Read(JsonElement e, Codecs o) =>
        e.Str("Kind") switch
        {
            "has" => new RimGovernor.Contracts.Wire.Observations.NeedLevel.Has(e.Dbl("Level")),
            "lacks" => new RimGovernor.Contracts.Wire.Observations.NeedLevel.Lacks(),
            var kind => throw new JsonException($"unknown need level {kind}"),
        };

    protected override void WriteFields(Utf8JsonWriter w, RimGovernor.Contracts.Wire.Observations.NeedLevel v, Codecs o)
    {
        if (v is RimGovernor.Contracts.Wire.Observations.NeedLevel.Has has)
        {
            w.WriteString("Kind", "has");
            w.WriteNum("Level", has.Level);
        }
        else if (v is RimGovernor.Contracts.Wire.Observations.NeedLevel.Lacks)
        {
            w.WriteString("Kind", "lacks");
        }
        else
        {
            throw new JsonException($"unknown need level {v.GetType().Name}");
        }
    }
}

/// <summary>A colonist's mood as a persisted state: <c>has</c> with its level and target, or <c>lacks</c> (#3897).</summary>
internal sealed class MoodLevelConverter : ObjectCodec<MoodLevel>
{
    protected override MoodLevel Read(JsonElement e, Codecs o) =>
        e.Str("Kind") switch
        {
            "has" => new MoodLevel.Has(e.Dbl("Level"), e.Dbl("Target")),
            "lacks" => new MoodLevel.Lacks(),
            var kind => throw new JsonException($"unknown mood level {kind}"),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodLevel v, Codecs o)
    {
        if (v is MoodLevel.Has has)
        {
            w.WriteString("Kind", "has");
            w.WriteNum("Level", has.Level);
            w.WriteNum("Target", has.Target);
        }
        else if (v is MoodLevel.Lacks)
        {
            w.WriteString("Kind", "lacks");
        }
        else
        {
            throw new JsonException($"unknown mood level {v.GetType().Name}");
        }
    }
}

internal sealed class MoodProvisionConverter : ObjectCodec<MoodProvision>
{
    protected override MoodProvision Read(JsonElement e, Codecs o) =>
        new(
            new NeedId(e.Get<ConcernId>("Concern", o).Value),
            e.Dbl("Offset")
        );

    protected override void WriteFields(Utf8JsonWriter w, MoodProvision v, Codecs o)
    {
        w.Put("Concern", v.Need.FilingKey, o);
        w.WriteNum("Offset", v.Offset);
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
        w.WriteNum("Offset", v.Offset);
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
        w.WriteNum("Room", v.Room);
        w.WriteNum("Weight", v.Weight);
    }
}

internal sealed class PawnBiotechConverter : ObjectCodec<PawnBiotech>
{
    protected override PawnBiotech Read(JsonElement e, Codecs o) =>
        new()
        {
            DevelopmentalStage = e.Get<RimGovernor.Contracts.Wire.Defs.DevelopmentalStage>("DevelopmentalStage", o),
            Genes = e.Get<EquatableArray<PawnGene>>("Genes", o),
            Effects = e.Get<Fact<GeneEffects>>("Effects", o),
            WorkMinAges = e.Get<Fact<IReadOnlyDictionary<WorkType, int>>>("WorkMinAges", o),
            Mechanitor = Absent(e, "Mechanitor") ? null : e.Get<PawnMechanitor>("Mechanitor", o),
            Mech = Absent(e, "Mech") ? null : e.Get<PawnMech>("Mech", o),
            Deathrest = Absent(e, "Deathrest") ? null : e.Get<PawnDeathrest>("Deathrest", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnBiotech v, Codecs o)
    {
        w.Put("DevelopmentalStage", v.DevelopmentalStage, o);
        w.Put("Genes", v.Genes, o);
        w.Put("Effects", v.Effects, o);
        w.Put("WorkMinAges", v.WorkMinAges, o);
        PutOrNull(w, "Mechanitor", v.Mechanitor, o);
        PutOrNull(w, "Mech", v.Mech, o);
        PutOrNull(w, "Deathrest", v.Deathrest, o);
    }

    /// <summary>Whether a required nullable record field is JSON null (the pawn has none).</summary>
    private static bool Absent(JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) ? field.ValueKind == JsonValueKind.Null : throw new StoreCorruptException($"missing field {name}");

    private static void PutOrNull<T>(Utf8JsonWriter w, string name, T? value, Codecs o)
        where T : class
    {
        if (value is not null)
        {
            w.Put(name, value, o);
        }
        else
        {
            w.WriteNull(name);
        }
    }
}

internal sealed class PawnDeathrestConverter : ObjectCodec<PawnDeathrest>
{
    protected override PawnDeathrest Read(JsonElement e, Codecs o) =>
        new()
        {
            Deathresting = e.Bool("Deathresting"),
            Capacity = e.I32("Capacity"),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnDeathrest v, Codecs o)
    {
        w.WriteBoolean("Deathresting", v.Deathresting);
        w.WriteNum("Capacity", v.Capacity);
    }
}

internal sealed class PawnGeneConverter : ObjectCodec<PawnGene>
{
    protected override PawnGene Read(JsonElement e, Codecs o) =>
        new()
        {
            Name = e.Str("Name"),
            Active = e.Bool("Active"),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnGene v, Codecs o)
    {
        w.WriteString("Name", v.Name);
        w.WriteBoolean("Active", v.Active);
    }
}

internal sealed class PawnMechConverter : ObjectCodec<PawnMech>
{
    protected override PawnMech Read(JsonElement e, Codecs o) =>
        new()
        {
            Overseer = e.Str("Overseer"),
            WorkMode = e.GetProperty("WorkMode") is { ValueKind: JsonValueKind.String } mode ? mode.GetString() : null,
            Energy = e.GetProperty("Energy") is { ValueKind: JsonValueKind.Number } energy
                ? new MechEnergy.Level(energy.GetDouble())
                : new MechEnergy.NoEnergyNeed(),
            Group = e.GetProperty("Group") is { ValueKind: JsonValueKind.Object } g
                ? new PawnMechGroup(g.I32("Index"), g.Dbl("RechargeBelow"), g.Dbl("RechargeAbove"))
                : null,
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnMech v, Codecs o)
    {
        w.WriteString("Overseer", v.Overseer);
        w.WriteString("WorkMode", v.WorkMode);
        if (v.Energy is MechEnergy.Level level)
        {
            w.WriteNum("Energy", level.Fraction);
        }
        else if (v.Energy is MechEnergy.NoEnergyNeed)
        {
            w.WriteNull("Energy");
        }
        else
        {
            throw new JsonException($"unknown mech energy {v.Energy.GetType().Name}");
        }
        if (v.Group is { } group)
        {
            w.WriteStartObject("Group");
            w.WriteNum("Index", group.Index);
            w.WriteNum("RechargeBelow", group.RechargeBelow);
            w.WriteNum("RechargeAbove", group.RechargeAbove);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("Group");
        }
    }
}

internal sealed class PawnMechanitorConverter : ObjectCodec<PawnMechanitor>
{
    protected override PawnMechanitor Read(JsonElement e, Codecs o) =>
        new()
        {
            UsedBandwidth = e.I32("UsedBandwidth"),
            TotalBandwidth = e.I32("TotalBandwidth"),
            GestationBandwidth = e.I32("GestationBandwidth"),
            ControlGroups = e.I32("ControlGroups"),
            ControlledMechs = e.Get<IReadOnlyList<string>>("ControlledMechs", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PawnMechanitor v, Codecs o)
    {
        w.WriteNum("UsedBandwidth", v.UsedBandwidth);
        w.WriteNum("TotalBandwidth", v.TotalBandwidth);
        w.WriteNum("GestationBandwidth", v.GestationBandwidth);
        w.WriteNum("ControlGroups", v.ControlGroups);
        w.Put("ControlledMechs", v.ControlledMechs, o);
    }
}

internal sealed class PawnProfileConverter : ObjectCodec<PawnProfile>
{
    protected override PawnProfile Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Get<RimGovernor.Brain.Domain.PawnId>("Id", o),
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
        w.WriteNum("Age", v.Age);
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
        w.WriteNum("Degree", v.Degree);
        w.Put("Effects", v.Effects, o);
    }
}

internal sealed class PhaseConverter() : StringCodec<Phase>(s => new(s), v => v.Value);
internal sealed class InteriorPieceConverter : ObjectCodec<InteriorPiece>
{
    protected override InteriorPiece Read(JsonElement e, Codecs o) =>
        new()
        {
            Slot = e.Str("Slot"),
            Def = e.Str("Def"),
            Size = e.Get<Cell>("Size", o),
            Rot = e.Get<Rotation>("Rot", o),
            Rect = e.Get<Rectangle>("Rect", o),
            InteractionOffset = e.GetOr<Cell?>("InteractionOffset", null, o),
            Row = e.Str("Row"),
            Beside = e.Str("Beside"),
            Clear = e.I32("Clear"),
        };

    protected override void WriteFields(Utf8JsonWriter w, InteriorPiece v, Codecs o)
    {
        w.WriteString("Slot", v.Slot);
        w.WriteString("Def", v.Def);
        w.Put("Size", v.Size, o);
        w.Put("Rot", v.Rot, o);
        w.Put("Rect", v.Rect, o);
        if (v.InteractionOffset is { } offset)
        {
            w.Put("InteractionOffset", offset, o);
        }
        w.WriteString("Row", v.Row);
        w.WriteString("Beside", v.Beside);
        w.WriteNum("Clear", v.Clear);
    }
}

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
            DoorSide = e.Has("DoorRot") ? new DoorSide.Doored(e.Get<Rotation>("DoorRot", o)) : new DoorSide.Doorless(),
            Crossing = e.GetOr<RoomCrossing?>("Crossing", null, o),
            Through = e.Bool("Through"),
            Dug = e.Bool("Dug"),
            Outdoor = e.Bool("Outdoor"),
            Facing = e.Has("Facing") ? new Heading.Toward(e.Get<Rotation>("Facing", o)) : new Heading.Unturned(),
            Markers = e.I32("Markers"),
            Parent = e.I32("Parent"),
            Herd = e.Str("Herd"),
            WorkChairs = e.Get<IReadOnlyDictionary<string, string>>("WorkChairs", o),
            FilthRuns = e.Get<IReadOnlyDictionary<string, FilthRuns>>("FilthRuns", o),
            Meals = e.Dbl("Meals"),
        };

    protected override void WriteFields(Utf8JsonWriter w, PlannedRoom v, Codecs o)
    {
        w.WriteNum("Id", v.Id);
        w.WriteNum("Replaces", v.Replaces);
        w.Put("Role", v.Role, o);
        w.Put("Region", v.Region, o);
        w.Put("Door", v.Door, o);
        if (v.DoorSide is DoorSide.Doored doored)
        {
            w.Put("DoorRot", doored.Side, o);
        }
        if (v.Crossing is { } vCrossing)
        {
            w.Put("Crossing", vCrossing, o);
        }
        w.WriteBoolean("Through", v.Through);
        w.WriteBoolean("Dug", v.Dug);
        w.WriteBoolean("Outdoor", v.Outdoor);
        if (v.Facing is Heading.Toward facing)
        {
            w.Put("Facing", facing.Side, o);
        }
        w.WriteNum("Markers", v.Markers);
        w.WriteNum("Parent", v.Parent);
        w.WriteString("Herd", v.Herd);
        w.Put("WorkChairs", v.WorkChairs, o);
        w.Put("FilthRuns", v.FilthRuns, o);
        w.WriteNum("Meals", v.Meals);
    }
}

internal sealed class FilthRunsConverter : ObjectCodec<FilthRuns>
{
    protected override FilthRuns Read(JsonElement e, Codecs o) =>
        new()
        {
            Runs = e.Dbl("Runs"),
            Ticks = e.Dbl("Ticks"),
        };

    protected override void WriteFields(Utf8JsonWriter w, FilthRuns v, Codecs o)
    {
        w.WriteNum("Runs", v.Runs);
        w.WriteNum("Ticks", v.Ticks);
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
            Rotation = e.Has("Rotation") ? new Heading.Toward(e.Get<Rotation>("Rotation", o)) : new Heading.Unturned(),
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
        if (v.Rotation is Heading.Toward turned)
        {
            w.Put("Rotation", turned.Side, o);
        }
        w.WriteBoolean("Anchored", v.Anchored);
        w.WriteNum("Replaces", v.Replaces);
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
            Repairer = e.Get<RimGovernor.Brain.Domain.PawnId>("Repairer", o),
            Opener = e.Get<RimGovernor.Brain.Domain.PawnId>("Opener", o),
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
    /// <summary>The retired Concern an equipment rung was filed under before the military/equipment Need (#3259).</summary>
    private static readonly ConcernId LegacyEquipment = new("MaintainEquipment");

    protected override ProductionLadderRecord Read(JsonElement e, Codecs o)
    {
        var concern = e.Get<ConcernId>("Concern", o);
        return new()
        {
            World = e.Get<World>("World", o),
            Tick = e.Get<Tick>("Tick", o),
            Resource = e.Get<Resource>("Resource", o),
            Bench = e.Str("Bench"),
            Recipe = e.Str("Recipe"),
            Research = e.Get<IReadOnlyList<string>>("Research", o),
            // A rung saved before the Need reads as the Need's.
            Concern = concern == LegacyEquipment ? MilitaryFamily.Equipment.FilingKey : concern,
        };
    }

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
        w.WriteNum("Level", v.Level);
        w.WriteNum("Stored", v.Stored);
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
        w.WriteNum("Count", v.Count);
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
        w.WriteNum("Parallelism", v.Parallelism);
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
        w.WriteNum("Distance", v.Distance);
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
            e.Get<RimGovernor.Brain.Domain.RecoveryMethod>("Method", o)
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
        w.WriteNum("X", v.X);
        w.WriteNum("Z", v.Z);
        w.WriteNum("Width", v.Width);
        w.WriteNum("Height", v.Height);
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
        w.WriteNum("Revision", v.Revision);
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
        w.WriteNum("Beds", v.Beds);
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
        w.WriteNum("A", v.A);
        w.WriteNum("B", v.B);
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
        w.WriteNum("A", v.A);
        w.WriteNum("B", v.B);
        w.WriteNum("Weight", v.Weight);
    }
}

internal sealed class RoundsConverter : ObjectCodec<Rounds>
{
    protected override Rounds Read(JsonElement e, Codecs o) =>
        new()
        {
            WorkEvidence = e.GetOr<WorkAdmissionEvidence?>("WorkEvidence", null, o),
            Unsafe = e.Get<EquatableArray<string>>("Unsafe", o),
            BrewingFinished = e.Bool("BrewingFinished"),
            ResourceRunways = e.Get<EquatableArray<ResourceRunwayRecord>>("ResourceRunways", o),
            ReserveSupplies = e.Get<EquatableArray<ReserveSupply>>("ReserveSupplies", o),
            LarderSupplies = e.Get<EquatableArray<StartingSupply>>("LarderSupplies", o),
            PollutionSupplies = e.Get<EquatableArray<StartingSupply>>("PollutionSupplies", o),
            RecoveryQueue = e.GetOr<RecoveryQueue?>("RecoveryQueue", null, o),
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
            Combat = e.GetOr<RoundsCombat?>("Combat", null, o),
            Roster = e.GetOr<WorkRosterReport?>("Roster", null, o),
            Progress = e.Get<EquatableArray<ConcernProgress>>("Progress", o),
            ReadyWork = e.GetOr<ReadyWorkReport?>("ReadyWork", null, o),
            Built = e.Get<EquatableArray<ActionId>>("Built", o),
            WallCells = e.Get<EquatableArray<WallCell>>("WallCells", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, Rounds v, Codecs o)
    {
        if (v.WorkEvidence is { } materials) w.Put("WorkEvidence", materials, o);
        w.Put("Unsafe", v.Unsafe, o);
        w.WriteBoolean("BrewingFinished", v.BrewingFinished);
        w.Put("ResourceRunways", v.ResourceRunways, o);
        w.Put("ReserveSupplies", v.ReserveSupplies, o);
        w.Put("LarderSupplies", v.LarderSupplies, o);
        w.Put("PollutionSupplies", v.PollutionSupplies, o);
        if (v.RecoveryQueue is { } vRecoveryQueue)
        {
            w.Put("RecoveryQueue", vRecoveryQueue, o);
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
        w.WriteNum("Revision", v.Revision);
        w.Put("Snapshot", v.Snapshot, o);
        w.Put("Tick", v.Tick, o);
        w.Put("OrdinaryTick", v.OrdinaryTick, o);
        w.WriteBoolean("Immediate", v.Immediate);
        w.WriteBoolean("Enabled", v.Enabled);
        w.Put("Latches", v.Latches, o);
        w.Put("MedicalCare", v.MedicalCare, o);
        w.Put("EventLoot", v.EventLoot, o);
        w.Put("Comfort", v.Comfort, o);
        if (v.Combat is { } vCombat)
        {
            w.Put("Combat", vCombat, o);
        }
        if (v.Roster is { } vRoster)
        {
            w.Put("Roster", vRoster, o);
        }
        w.Put("Progress", v.Progress, o);
        if (v.ReadyWork is { } vReadyWork)
        {
            w.Put("ReadyWork", vReadyWork, o);
        }
        w.Put("Built", v.Built, o);
        w.Put("WallCells", v.WallCells, o);
    }
}

internal sealed class RoundsLatchesConverter : ObjectCodec<RoundsLatches>
{
    protected override RoundsLatches Read(JsonElement e, Codecs o) =>
        new()
        {
            MedicalReserve = e.Bool("MedicalReserve"),
            Dash = e.Get<DashWindow>("Dash", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsLatches v, Codecs o)
    {
        w.WriteBoolean("MedicalReserve", v.MedicalReserve);
        w.Put("Dash", v.Dash, o);
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
        w.WriteNum("Z", v.Z);
        w.WriteNum("X", v.X);
        w.WriteNum("Length", v.Length);
    }
}

internal sealed class SafeguardProposalConverter : ObjectCodec<SafeguardProposal>
{
    protected override SafeguardProposal Read(JsonElement e, Codecs o) =>
        new()
        {
            Need = e.Get<ConcernId>("Need", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, SafeguardProposal v, Codecs o)
    {
        w.Put("Need", v.Need, o);
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
        w.WriteNum("Blocked", v.Blocked);
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
            e.Get<RimGovernor.Brain.Domain.PawnId>("Pawn", o),
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

internal sealed class IdeologyReformRecordConverter : ObjectCodec<IdeologyReformRecord>
{
    protected override IdeologyReformRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            Reform = e.Get<global::RimGovernor.Brain.Domain.IdeoligionReform>("Reform", o),
            Goal = e.Get<int?>("Goal", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, IdeologyReformRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Reform", v.Reform, o);
        w.Put("Goal", v.Goal, o);
    }
}

internal sealed class CreepJoinersRecordConverter : ObjectCodec<CreepJoinersRecord>
{
    protected override CreepJoinersRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            Ordered = e.Get<IReadOnlyList<global::RimGovernor.Brain.Domain.PawnId>>("Ordered", o),
            Done = e.Get<IReadOnlyList<global::RimGovernor.Brain.Domain.PawnId>>("Done", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, CreepJoinersRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Ordered", v.Ordered, o);
        w.Put("Done", v.Done, o);
    }
}

internal sealed class TradeMissionEntryConverter : ObjectCodec<TradeMissionEntry>
{
    protected override TradeMissionEntry Read(JsonElement e, Codecs o) => new(e.Str("Id"), e.Get<TradeMission>("Mission", o));

    protected override void WriteFields(Utf8JsonWriter w, TradeMissionEntry v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.Put("Mission", v.Mission, o);
    }
}

internal sealed class TradeMissionsRecordConverter : ObjectCodec<TradeMissionsRecord>
{
    protected override TradeMissionsRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            Missions = e.Get<IReadOnlyList<TradeMissionEntry>>("Missions", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, TradeMissionsRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.Put("Missions", v.Missions, o);
    }
}

internal sealed class GoalRecordConverter : ObjectCodec<GoalRecord>
{
    protected override GoalRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.I32("Id"),
            Kind = e.Get<GoalKind>("Kind", o),
            Target = e.Str("Target"),
            Qualifier = e.Str("Qualifier"),
            Count = e.I32("Count"),
            Importance = e.Get<Importance>("Importance", o),
            DeadlineDay = e.Get<int?>("DeadlineDay", o),
            Theme = e.Get<int?>("Theme", o),
            ImportanceOverride = e.Get<Importance?>("ImportanceOverride", o),
            DeadlineOverride = e.Get<int?>("DeadlineOverride", o),
            SiteMap = e.Get<MapId?>("SiteMap", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, GoalRecord v, Codecs o)
    {
        w.WriteNum("Id", v.Id);
        w.Put("Kind", v.Kind, o);
        w.WriteString("Target", v.Target);
        w.WriteString("Qualifier", v.Qualifier);
        w.WriteNum("Count", v.Count);
        w.Put("Importance", v.Importance, o);
        w.Put("DeadlineDay", v.DeadlineDay, o);
        w.Put("Theme", v.Theme, o);
        w.Put("ImportanceOverride", v.ImportanceOverride, o);
        w.Put("DeadlineOverride", v.DeadlineOverride, o);
        w.Put("SiteMap", v.SiteMap, o);
    }
}

internal sealed class ThemeRecordConverter : ObjectCodec<ThemeRecord>
{
    protected override ThemeRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.I32("Id"),
            Name = e.Str("Name"),
            Preset = e.Str("Preset"),
            Importance = e.Get<Importance>("Importance", o),
            DeadlineDay = e.Get<int?>("DeadlineDay", o),
            Members = e.Get<IReadOnlyList<int>>("Members", o),
            RemovedPresetMembers = e.Get<IReadOnlyList<string>>("RemovedPresetMembers", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ThemeRecord v, Codecs o)
    {
        w.WriteNum("Id", v.Id);
        w.WriteString("Name", v.Name);
        w.WriteString("Preset", v.Preset);
        w.Put("Importance", v.Importance, o);
        w.Put("DeadlineDay", v.DeadlineDay, o);
        w.Put("Members", v.Members, o);
        w.Put("RemovedPresetMembers", v.RemovedPresetMembers, o);
    }
}

internal sealed class PreferencesRecordConverter : ObjectCodec<PreferencesRecord>
{
    protected override PreferencesRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            RiskProfile = e.Get<RiskProfile>("RiskProfile", o),
            Pace = e.Get<Pace>("Pace", o),
            WealthCap = e.Get<int?>("WealthCap", o),
            Forbidden = e.Get<IReadOnlyList<ForbiddenMethod>>("Forbidden", o),
            ProtectedThings = e.Get<IReadOnlyList<string>>("ProtectedThings", o),
            ProtectedPawns = e.Get<IReadOnlyList<string>>("ProtectedPawns", o),
            Diet = e.Get<IReadOnlyList<DietOverride>>("Diet", o),
            Apparel = e.Get<ApparelPreference?>("Apparel", o),
            DrugPolicy = e.Get<DrugPolicyPreference?>("DrugPolicy", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, PreferencesRecord v, Codecs o)
    {
        w.Put("RiskProfile", v.RiskProfile, o);
        w.Put("Pace", v.Pace, o);
        w.Put("WealthCap", v.WealthCap, o);
        w.Put("Forbidden", v.Forbidden, o);
        w.Put("ProtectedThings", v.ProtectedThings, o);
        w.Put("ProtectedPawns", v.ProtectedPawns, o);
        w.Put("Diet", v.Diet, o);
        w.Put("Apparel", v.Apparel, o);
        w.Put("DrugPolicy", v.DrugPolicy, o);
    }
}

internal sealed class GoalsRecordConverter : ObjectCodec<GoalsRecord>
{
    protected override GoalsRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            World = e.Get<World>("World", o),
            NextId = e.I32("NextId"),
            Goals = e.Get<IReadOnlyList<GoalRecord>>("Goals", o),
            Themes = e.Get<IReadOnlyList<ThemeRecord>>("Themes", o),
            Preferences = e.Get<PreferencesRecord>("Preferences", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, GoalsRecord v, Codecs o)
    {
        w.Put("World", v.World, o);
        w.WriteNum("NextId", v.NextId);
        w.Put("Goals", v.Goals, o);
        w.Put("Themes", v.Themes, o);
        w.Put("Preferences", v.Preferences, o);
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
        w.WriteNum("Offset", v.Offset);
        w.WriteNum("Factor", v.Factor);
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
        w.WriteNum("WorkSpeed", v.WorkSpeed);
        w.WriteNum("LearnRate", v.LearnRate);
        w.WriteNum("MoveSpeed", v.MoveSpeed);
        w.WriteBoolean("QuickSleeper", v.QuickSleeper);
        w.WriteBoolean("NightShift", v.NightShift);
        w.WriteBoolean("MeleeOnly", v.MeleeOnly);
        w.WriteBoolean("FrontLine", v.FrontLine);
        w.WriteBoolean("RearRanged", v.RearRanged);
        w.Put("DisabledWork", v.DisabledWork, o);
        w.WriteBoolean("Pyromaniac", v.Pyromaniac);
        w.WriteNum("Sociable", v.Sociable);
        w.WriteBoolean("Execution", v.Execution);
        w.WriteBoolean("SurgeonSafe", v.SurgeonSafe);
        w.WriteBoolean("HumanButcher", v.HumanButcher);
        w.WriteBoolean("Nudist", v.Nudist);
        w.WriteBoolean("Ascetic", v.Ascetic);
        w.WriteBoolean("Cannibal", v.Cannibal);
        w.WriteBoolean("Gourmand", v.Gourmand);
        w.WriteNum("ChemicalInterest", v.ChemicalInterest);
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
        w.WriteNum("Demand", v.Demand);
        w.WriteNum("Owners", v.Owners);
        w.WriteNum("Capable", v.Capable);
    }
}

internal sealed class WorkRosterReportConverter : ObjectCodec<WorkRosterReport>
{
    protected override WorkRosterReport Read(JsonElement e, Codecs o) =>
        new()
        {
            Tick = e.Get<Tick>("Tick", o),
            Coverage = e.Get<EquatableArray<WorkCoverage>>("Coverage", o),
            Profiles = e.Get<EquatableArray<PawnProfile>>("Profiles", o),
            Help = e.GetOr<ConstructionHelpRecord?>("Help", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, WorkRosterReport v, Codecs o)
    {
        w.Put("Tick", v.Tick, o);
        w.Put("Coverage", v.Coverage, o);
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

