using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using BuildingHitPoints = RimGovernor.Contracts.Wire.Observations.BuildingHitPoints;

namespace RimGovernor.Brain.Store;

/// <summary>Shared null handling for the review records: a pointer field without omitempty writes null.</summary>
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
            Stock = e.I64("Stock"),
            SurfaceOre = e.I64("SurfaceOre"),
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
        w.WriteNum("WindowDays", v.WindowDays);
        w.WriteNum("Reserve", v.Reserve);
        w.WriteNum("Stock", v.Stock);
        w.WriteNum("SurfaceOre", v.SurfaceOre);
        w.PutNullable("ConsumptionPerDay", v.ConsumptionPerDay, o);
        w.PutNullable("StockDays", v.StockDays, o);
        w.PutNullable("DaysLeft", v.DaysLeft, o);
        w.PutNullable("Deficit", v.Deficit, o);
        w.WriteNum("Target", v.Target);
    }
}

public sealed class RoundsRecoveryRestrictionConverter : ObjectCodec<RoundsRecoveryRestriction>
{
    protected override RoundsRecoveryRestriction Read(JsonElement e, Codecs o) =>
        new(new PawnId(e.Str("Pawn")), e.Str("Area"));

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecoveryRestriction v, Codecs o)
    {
        w.WriteString("Pawn", v.Pawn.Value);
        w.WriteString("Area", v.Area);
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
            Pawn = new PawnId(e.Str("Pawn")),
            Dead = e.Bool("Dead"),
            Downed = e.Bool("Downed"),
            Drafted = e.Bool("Drafted"),
            Mental = e.Bool("Mental"),
            PlayerForced = e.Bool("PlayerForced"),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecoveryWorker v, Codecs o)
    {
        w.WriteString("Pawn", v.Pawn.Value);
        w.WriteBoolean("Dead", v.Dead);
        w.WriteBoolean("Downed", v.Downed);
        w.WriteBoolean("Drafted", v.Drafted);
        w.WriteBoolean("Mental", v.Mental);
        w.WriteBoolean("PlayerForced", v.PlayerForced);
    }
}

/// <summary>The review's retained recovery selection (#3304: it had no codec, so a disaster review could not be written).</summary>
public sealed class RecoverySelectionConverter : ObjectCodec<RecoverySelection>
{
    protected override RecoverySelection Read(JsonElement e, Codecs o) =>
        new()
        {
            Tick = e.Get<Tick>("Tick", o),
            Reason = e.Get<RecoverySelectionReason>("Reason", o),
            Candidates = e.GetOr("Candidates", default(EquatableArray<RecoveryCandidate>), o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RecoverySelection v, Codecs o)
    {
        w.Put("Tick", v.Tick, o);
        w.Put("Reason", v.Reason, o);
        w.Put("Candidates", v.Candidates, o);
    }
}

/// <summary>One retained recovery candidate; an unknown prior area persists as null.</summary>
public sealed class RecoveryCandidateConverter : ObjectCodec<RecoveryCandidate>
{
    protected override RecoveryCandidate Read(JsonElement e, Codecs o) =>
        new()
        {
            ID = new MethodId(e.Str("ID")),
            Kind = e.Get<RecoveryProposalKind>("Kind", o),
            Pawn = new PawnId(e.Str("Pawn")),
            Building = e.Str("Building"),
            Method = e.Get<RecoveryMethod>("Method", o),
            HitPoints = e.GetNullable<int>("HitPoints", o) is { } current
                ? new BuildingHitPoints.Tracked { Current = current, Maximum = e.I32("MaxHitPoints") }
                : new BuildingHitPoints.Untracked(),
            Fuel = e.GetNullable<double>("Fuel", o) is { } level ? new RecoveryFuel.Refuelable(level, e.Dbl("FuelTarget")) : new RecoveryFuel.NotRefuelable(),
            PriorArea = e.Has("PriorArea") ? e.Str("PriorArea") : null,
        };

    protected override void WriteFields(Utf8JsonWriter w, RecoveryCandidate v, Codecs o)
    {
        w.WriteString("ID", v.ID.Value);
        w.Put("Kind", v.Kind, o);
        w.WriteString("Pawn", v.Pawn.Value);
        w.WriteString("Building", v.Building);
        w.Put("Method", v.Method, o);
        if (v.HitPoints is BuildingHitPoints.Tracked hp)
        {
            w.WriteNum("HitPoints", hp.Current);
            w.WriteNum("MaxHitPoints", hp.Maximum);
        }
        if (v.Fuel is RecoveryFuel.Refuelable fuel)
        {
            w.WriteNum("Fuel", fuel.Level);
            w.WriteNum("FuelTarget", fuel.Target);
        }
        if (v.PriorArea is null)
        {
            w.WriteNull("PriorArea");
        }
        else
        {
            w.WriteString("PriorArea", v.PriorArea);
        }
    }
}

public sealed class RoundsRecoveryBuildingConverter : ObjectCodec<RoundsRecoveryBuilding>
{
    protected override RoundsRecoveryBuilding Read(JsonElement e, Codecs o) =>
        new()
        {
            Id = e.Str("ID"),
            Broken = e.Bool("Broken"),
            Forbidden = e.Bool("Forbidden"),
            Burning = e.Bool("Burning"),
            HitPoints = e.GetNullable<int>("HitPoints", o),
            MaxHitPoints = e.GetNullable<int>("MaxHitPoints", o),
            Fuel = e.GetNullable<double>("Fuel", o),
            FuelTarget = e.GetNullable<double>("FuelTarget", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecoveryBuilding v, Codecs o)
    {
        w.WriteString("ID", v.Id);
        w.WriteBoolean("Broken", v.Broken);
        w.WriteBoolean("Forbidden", v.Forbidden);
        w.WriteBoolean("Burning", v.Burning);
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
            Need = new NeedId(e.Str("Need")),
            World = e.Get<World>("World", o),
            Used = e.GetOr("Used", default(EquatableArray<MethodId>), o),
            Safety = e.Get<RoundsRecoverySafety>("Safety", o),
            Workers = e.GetNullable<EquatableArray<RoundsRecoveryWorker>>("Workers", o),
            Buildings = e.Get<EquatableArray<RoundsRecoveryBuilding>>("Buildings", o),
            Selection = e.Get<RecoverySelection>("Selection", o),
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsRecovery v, Codecs o)
    {
        w.WriteString("Need", v.Need.Value);
        w.Put("World", v.World, o);
        w.Put("Used", v.Used, o);
        w.Put("Safety", v.Safety, o);
        w.PutNullable("Workers", v.Workers, o);
        w.Put("Buildings", v.Buildings, o);
        w.Put("Selection", v.Selection, o);
    }
}

public sealed class RoundsMoodCauseConverter : ObjectCodec<RoundsMoodCause>
{
    protected override RoundsMoodCause Read(JsonElement e, Codecs o) => new(e.Get<MoodNeed>("Need", o), e.Dbl("Level"));

    protected override void WriteFields(Utf8JsonWriter w, RoundsMoodCause v, Codecs o)
    {
        w.Put("Need", v.Need, o);
        w.WriteNum("Level", v.Level);
    }
}

public sealed class RoundsMoodPawnConverter : ObjectCodec<RoundsMoodPawn>
{
    protected override RoundsMoodPawn Read(JsonElement e, Codecs o) =>
        new()
        {
            Break = e.GetNullableRef<MentalState>("Break", o),
            Id = new PawnId(e.Str("ID")),
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
        };

    protected override void WriteFields(Utf8JsonWriter w, RoundsMoodPawn v, Codecs o)
    {
        if (v.Break is { } brk)
        {
            w.Put("Break", brk, o);
        }
        w.WriteString("ID", v.Id.Value);
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
            Pawn = new PawnId(e.Str("Pawn")),
            Need = e.Get<MoodNeed>("Need", o),
            Reason = e.Get<MoodMethodReason>("Reason", o),
            Concern = e.GetOr("Concern", default(ConcernId), o),
            Thought = e.StrOr("Thought"),
            Target = e.Dbl("Target"),
            NeedBenefit = e.Dbl("NeedBenefit"),
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
        w.WriteNum("Target", v.Target);
        w.WriteNum("NeedBenefit", v.NeedBenefit);
    }
}

public sealed class RoundsCombatConverter : ObjectCodec<RoundsCombat>
{
    protected override RoundsCombat Read(JsonElement e, Codecs o) =>
        new(e.Get<Situation>("Situation", o), e.Get<EquatableArray<PawnId>>("Prey", o), e.Get<EquatableArray<PawnId>>("Hunters", o));

    protected override void WriteFields(Utf8JsonWriter w, RoundsCombat v, Codecs o)
    {
        w.Put("Situation", v.Situation, o);
        w.Put("Prey", v.Prey, o);
        w.Put("Hunters", v.Hunters, o);
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
