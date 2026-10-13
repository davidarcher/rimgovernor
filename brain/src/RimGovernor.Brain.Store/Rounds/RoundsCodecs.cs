using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using PolicyPawnId = RimGovernor.Brain.Policy.PawnId;

namespace RimGovernor.Brain.Store;

/// <summary>Shared null handling for the review records: a Go pointer field without omitempty writes null.</summary>
internal static class RoundsCodecKit
{
    internal static void PutNullable<T>(this Utf8JsonWriter w, string name, T? value, Codecs o)
        where T : struct
    {
        if (value is { } v)
        {
            w.Put(name, v, o);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    internal static void PutNullableRef<T>(this Utf8JsonWriter w, string name, T? value, Codecs o)
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

    internal static T? GetNullable<T>(this JsonElement e, string name, Codecs o)
        where T : struct => e.Has(name) ? e.Get<T>(name, o) : null;

    internal static T? GetNullableRef<T>(this JsonElement e, string name, Codecs o)
        where T : class => e.Has(name) ? e.Get<T>(name, o) : null;
}

public sealed class ResourceRunwayRecordConverter : ObjectCodec<ResourceRunwayRecord>
{
    protected override ResourceRunwayRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            Resource = new Resource(e.Str("Resource")),
            Tick = e.Get<Tick>("Tick", o),
            WindowDays = e.Dbl("WindowDays"),
            Reserve = e.I64("Reserve"),
            Stock = e.GetNullable<long>("Stock", o),
            SurfaceOre = e.GetNullable<long>("SurfaceOre", o),
            ConsumptionPerDay = e.GetNullable<double>("ConsumptionPerDay", o),
            StockDays = e.GetNullable<double>("StockDays", o),
            DaysLeft = e.GetNullable<double>("DaysLeft", o),
            Deficit = e.GetNullable<bool>("Deficit", o),
            Target = e.I64("Target"),
        };

    protected override void WriteFields(Utf8JsonWriter w, ResourceRunwayRecord v, Codecs o)
    {
        w.WriteString("Resource", v.Resource.Value);
        w.Put("Tick", v.Tick, o);
        w.WriteNumber("WindowDays", v.WindowDays);
        w.WriteNumber("Reserve", v.Reserve);
        w.PutNullable("Stock", v.Stock, o);
        w.PutNullable("SurfaceOre", v.SurfaceOre, o);
        w.PutNullable("ConsumptionPerDay", v.ConsumptionPerDay, o);
        w.PutNullable("StockDays", v.StockDays, o);
        w.PutNullable("DaysLeft", v.DaysLeft, o);
        w.PutNullable("Deficit", v.Deficit, o);
        w.WriteNumber("Target", v.Target);
    }
}

public sealed class RoundsShrineStepConverter : ObjectCodec<RoundsShrineStep>
{
    protected override RoundsShrineStep Read(JsonElement e, Codecs o) =>
        new()
        {
            Tick = e.Get<Tick>("Tick", o),
            Reason = e.Str("Reason"),
            Shrine = e.StrOr("Shrine"),
            Hold = e.StrOr("Hold"),
            Plan = e.GetOr("Plan", default(PlanId), o),
            Skipped = e.GetOr("Skipped", default(EquatableArray<ShrineHold>), o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsShrineStep v, Codecs o)
    {
        w.Put("Tick", v.Tick, o);
        w.WriteString("Reason", v.Reason);
        if (v.Shrine.Length != 0)
        {
            w.WriteString("Shrine", v.Shrine);
        }
        if (v.Hold.Length != 0)
        {
            w.WriteString("Hold", v.Hold);
        }
        if (v.Plan.Value.Length != 0)
        {
            w.Put("Plan", v.Plan, o);
        }
        if (v.Skipped.Count > 0)
        {
            w.Put("Skipped", v.Skipped, o);
        }
    }
}

public sealed class RoundsRecoveryRestrictionConverter : ObjectCodec<RoundsRecoveryRestriction>
{
    protected override RoundsRecoveryRestriction Read(JsonElement e, Codecs o) =>
        new(new PolicyPawnId(e.Str("Pawn")), e.Has("Area") ? e.Str("Area") : null);

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecoveryRestriction v, Codecs o)
    {
        w.WriteString("Pawn", v.Pawn.Value);
        if (v.Area is null)
        {
            w.WriteNull("Area");
        }
        else
        {
            w.WriteString("Area", v.Area);
        }
    }
}

public sealed class RoundsRecoverySafetyConverter : ObjectCodec<RoundsRecoverySafety>
{
    protected override RoundsRecoverySafety Read(JsonElement e, Codecs o) =>
        new() { Restrictions = e.GetOr("Restrictions", default(EquatableArray<RoundsRecoveryRestriction>), o) };

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecoverySafety v, Codecs o) => w.Put("Restrictions", v.Restrictions, o);
}

public sealed class RoundsRecoveryWorkerConverter : ObjectCodec<RoundsRecoveryWorker>
{
    protected override RoundsRecoveryWorker Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = new PolicyPawnId(e.Str("Pawn")),
            Dead = e.GetNullable<bool>("Dead", o),
            Downed = e.GetNullable<bool>("Downed", o),
            Drafted = e.GetNullable<bool>("Drafted", o),
            Mental = e.GetNullable<bool>("Mental", o),
            PlayerForced = e.GetNullable<bool>("PlayerForced", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecoveryWorker v, Codecs o)
    {
        w.WriteString("Pawn", v.Pawn.Value);
        w.PutNullable("Dead", v.Dead, o);
        w.PutNullable("Downed", v.Downed, o);
        w.PutNullable("Drafted", v.Drafted, o);
        w.PutNullable("Mental", v.Mental, o);
        w.PutNullable("PlayerForced", v.PlayerForced, o);
    }
}

public sealed class RoundsRecoveryBuildingConverter : ObjectCodec<RoundsRecoveryBuilding>
{
    protected override RoundsRecoveryBuilding Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Str("ID"),
            UsesHitPoints = e.GetNullable<bool>("UsesHitPoints", o),
            Broken = e.GetNullable<bool>("Broken", o),
            Forbidden = e.GetNullable<bool>("Forbidden", o),
            Burning = e.GetNullable<bool>("Burning", o),
            Refuelable = e.GetNullable<bool>("Refuelable", o),
            HitPoints = e.GetNullable<long>("HitPoints", o),
            MaxHitPoints = e.GetNullable<long>("MaxHitPoints", o),
            Fuel = e.GetNullable<double>("Fuel", o),
            FuelTarget = e.GetNullable<double>("FuelTarget", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecoveryBuilding v, Codecs o)
    {
        w.WriteString("ID", v.Id);
        w.PutNullable("UsesHitPoints", v.UsesHitPoints, o);
        w.PutNullable("Broken", v.Broken, o);
        w.PutNullable("Forbidden", v.Forbidden, o);
        w.PutNullable("Burning", v.Burning, o);
        w.PutNullable("Refuelable", v.Refuelable, o);
        w.PutNullable("HitPoints", v.HitPoints, o);
        w.PutNullable("MaxHitPoints", v.MaxHitPoints, o);
        w.PutNullable("Fuel", v.Fuel, o);
        w.PutNullable("FuelTarget", v.FuelTarget, o);
    }
}

public sealed class RoundsRecoveryConverter : ObjectCodec<RoundsRecovery>
{
    protected override RoundsRecovery Read(JsonElement e, Codecs o) =>
        new()
        {
            Incident = e.Get<IncidentId>("Incident", o),
            Used = e.GetOr("Used", default(EquatableArray<MethodId>), o),
            Safety = e.GetNullableRef<RoundsRecoverySafety>("Safety", o),
            Workers = e.GetNullable<EquatableArray<RoundsRecoveryWorker>>("Workers", o),
            Buildings = e.GetNullable<EquatableArray<RoundsRecoveryBuilding>>("Buildings", o),
            Selection = e.Get<RecoverySelection>("Selection", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecovery v, Codecs o)
    {
        w.Put("Incident", v.Incident, o);
        w.Put("Used", v.Used, o);
        w.PutNullableRef("Safety", v.Safety, o);
        w.PutNullable("Workers", v.Workers, o);
        w.PutNullable("Buildings", v.Buildings, o);
        w.Put("Selection", v.Selection, o);
    }
}

public sealed class RoundsMoodCauseConverter : ObjectCodec<RoundsMoodCause>
{
    protected override RoundsMoodCause Read(JsonElement e, Codecs o) => new(e.Get<MoodNeed>("Need", o), e.GetNullable<double>("Level", o));

    protected override void WriteFields(Utf8JsonWriter w, RoundsMoodCause v, Codecs o)
    {
        w.Put("Need", v.Need, o);
        w.PutNullable("Level", v.Level, o);
    }
}

public sealed class RoundsMoodPawnConverter : ObjectCodec<RoundsMoodPawn>
{
    protected override RoundsMoodPawn Read(JsonElement e, Codecs o) =>
        new()
        {
            Break = e.GetNullableRef<MentalState>("Break", o),
            Id = new PolicyPawnId(e.Str("ID")),
            Mood = e.GetNullable<double>("Mood", o),
            Threshold = e.GetNullable<double>("Threshold", o),
            Target = e.GetNullable<double>("Target", o),
            Food = e.GetNullable<double>("Food", o),
            Rest = e.GetNullable<double>("Rest", o),
            Joy = e.GetNullable<double>("Joy", o),
            Mental = e.GetNullable<bool>("Mental", o),
            Dead = e.GetNullable<bool>("Dead", o),
            Downed = e.GetNullable<bool>("Downed", o),
            Drafted = e.GetNullable<bool>("Drafted", o),
            PlayerForced = e.GetNullable<bool>("PlayerForced", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsMoodPawn v, Codecs o)
    {
        if (v.Break is { } brk)
        {
            w.Put("Break", brk, o);
        }
        w.WriteString("ID", v.Id.Value);
        w.PutNullable("Mood", v.Mood, o);
        w.PutNullable("Threshold", v.Threshold, o);
        w.PutNullable("Target", v.Target, o);
        w.PutNullable("Food", v.Food, o);
        w.PutNullable("Rest", v.Rest, o);
        w.PutNullable("Joy", v.Joy, o);
        w.PutNullable("Mental", v.Mental, o);
        w.PutNullable("Dead", v.Dead, o);
        w.PutNullable("Downed", v.Downed, o);
        w.PutNullable("Drafted", v.Drafted, o);
        w.PutNullable("PlayerForced", v.PlayerForced, o);
    }
}

public sealed class RoundsMoodStateConverter : ObjectCodec<RoundsMoodState>
{
    protected override RoundsMoodState Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<RoundsMoodPawn>("Pawn", o),
            Active = e.Bool("Active"),
            Missing = e.Bool("Missing"),
            MentalRisk = e.Bool("MentalRisk"),
            Causes = e.GetOr("Causes", default(EquatableArray<RoundsMoodCause>), o),
            Provision = e.GetOr("Provision", default(EquatableArray<MoodProvision>), o),
            Unowned = e.GetOr("Unowned", default(EquatableArray<MoodThought>), o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsMoodState v, Codecs o)
    {
        w.Put("Pawn", v.Pawn, o);
        w.WriteBoolean("Active", v.Active);
        w.WriteBoolean("Missing", v.Missing);
        w.WriteBoolean("MentalRisk", v.MentalRisk);
        w.Put("Causes", v.Causes, o);
        if (v.Provision.Count > 0)
        {
            w.Put("Provision", v.Provision, o);
        }
        if (v.Unowned.Count > 0)
        {
            w.Put("Unowned", v.Unowned, o);
        }
    }
}

public sealed class RoundsMoodConverter : ObjectCodec<RoundsMood>
{
    protected override RoundsMood Read(JsonElement e, Codecs o) => new() { States = e.GetOr("States", default(EquatableArray<RoundsMoodState>), o) };

    protected override void WriteFields(Utf8JsonWriter w, RoundsMood v, Codecs o) => w.Put("States", v.States, o);
}

public sealed class RoundsMoodMethodConverter : ObjectCodec<RoundsMoodMethod>
{
    protected override RoundsMoodMethod Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = new PolicyPawnId(e.Str("Pawn")),
            Need = e.Get<MoodNeed>("Need", o),
            Reason = e.Get<MoodMethodReason>("Reason", o),
            Concern = e.GetOr("Concern", default(ConcernId), o),
            Thought = e.StrOr("Thought"),
            Target = e.Dbl("Target"),
            NeedBenefit = e.GetNullable<double>("NeedBenefit", o),
            MoodBenefit = e.GetNullable<double>("MoodBenefit", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsMoodMethod v, Codecs o)
    {
        w.WriteString("Pawn", v.Pawn.Value);
        w.Put("Need", v.Need, o);
        w.Put("Reason", v.Reason, o);
        if (v.Concern.Value.Length != 0)
        {
            w.Put("Concern", v.Concern, o);
        }
        if (v.Thought.Length != 0)
        {
            w.WriteString("Thought", v.Thought);
        }
        w.WriteNumber("Target", v.Target);
        w.PutNullable("NeedBenefit", v.NeedBenefit, o);
        w.PutNullable("MoodBenefit", v.MoodBenefit, o);
    }
}

public sealed class RoundsIncidentConverter : ObjectCodec<RoundsIncident>
{
    protected override RoundsIncident Read(JsonElement e, Codecs o) =>
        new()
        {
            Kind = e.Get<ConcernId>("Kind", o),
            Subject = e.GetOr("Subject", default(PawnId), o),
            Incident = e.Get<IncidentId>("Incident", o),
            Situation = e.Get<Situation>("Situation", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsIncident v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        if (!v.Subject.IsEmpty)
        {
            w.Put("Subject", v.Subject, o);
        }
        w.Put("Incident", v.Incident, o);
        w.Put("Situation", v.Situation, o);
    }
}

public sealed class DependencyCostConverter : ObjectCodec<DependencyCost>
{
    protected override DependencyCost Read(JsonElement e, Codecs o) => new(e.Get<ActionId>("Action", o), e.I64("Count"));

    protected override void WriteFields(Utf8JsonWriter w, DependencyCost v, Codecs o)
    {
        w.Put("Action", v.Action, o);
        w.WriteNumber("Count", v.Count);
    }
}

public sealed class DependencyRecordConverter : ObjectCodec<DependencyRecord>
{
    protected override DependencyRecord Read(JsonElement e, Codecs o) =>
        new()
        {
            Need = e.Get<ConcernId>("Need", o),
            Standard = e.Get<StandardId>("Standard", o),
            Episode = e.U64("Episode"),
            Method = e.Get<MethodId>("Method", o),
            Plan = e.Get<PlanId>("Plan", o),
            Resource = new Resource(e.Str("Resource")),
            Costs = e.GetOr("Costs", default(EquatableArray<DependencyCost>), o),
            Available = e.I64("Available"),
            Observed = e.Get<Tick>("Observed", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, DependencyRecord v, Codecs o)
    {
        w.Put("Need", v.Need, o);
        w.Put("Standard", v.Standard, o);
        w.WriteNumber("Episode", v.Episode);
        w.Put("Method", v.Method, o);
        w.Put("Plan", v.Plan, o);
        w.WriteString("Resource", v.Resource.Value);
        w.Put("Costs", v.Costs, o);
        w.WriteNumber("Available", v.Available);
        w.Put("Observed", v.Observed, o);
    }
}

public sealed class WallCellConverter : ObjectCodec<WallCell>
{
    protected override WallCell Read(JsonElement e, Codecs o) => new(e.Get<Cell>("Cell", o), e.BoolOr("Standing"));

    protected override void WriteFields(Utf8JsonWriter w, WallCell v, Codecs o)
    {
        w.Put("Cell", v.Cell, o);
        if (v.Standing)
        {
            w.WriteBoolean("Standing", true);
        }
    }
}
