using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using BuildingHitPoints = RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints;

namespace RimGovernor.Brain.Store;

/// <summary>Shared null handling for the review records: a pointer field without omitempty writes null.</summary>
internal static class CodecKit
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

public sealed class ReviewMoodCauseConverter : ObjectCodec<ReviewMoodCause>
{
    protected override ReviewMoodCause Read(JsonElement e, Codecs o) => new(e.Get<MoodNeed>("Need", o), e.Dbl("Level"));

    protected override void WriteFields(Utf8JsonWriter w, ReviewMoodCause v, Codecs o)
    {
        w.Put("Need", v.Need, o);
        w.WriteNum("Level", v.Level);
    }
}

public sealed class ReviewMoodStateConverter : ObjectCodec<ReviewMoodState>
{
    protected override ReviewMoodState Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = e.Get<MoodPawn>("Pawn", o),
            Active = e.Bool("Active"),
            Missing = e.Bool("Missing"),
            MentalRisk = e.Bool("MentalRisk"),
            Causes = e.GetOr("Causes", default(EquatableArray<ReviewMoodCause>), o),
            Provision = e.GetOr("Provision", default(EquatableArray<MoodProvision>), o),
            Unowned = e.GetOr("Unowned", default(EquatableArray<MoodThought>), o),
        };

    protected override void WriteFields(Utf8JsonWriter w, ReviewMoodState v, Codecs o)
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

public sealed class MoodConverter : ObjectCodec<Mood>
{
    protected override Mood Read(JsonElement e, Codecs o) => new() { States = e.GetOr("States", default(EquatableArray<ReviewMoodState>), o) };

    protected override void WriteFields(Utf8JsonWriter w, Mood v, Codecs o) => w.Put("States", v.States, o);
}

public sealed class MoodMethodConverter : ObjectCodec<MoodMethod>
{
    protected override MoodMethod Read(JsonElement e, Codecs o) =>
        new()
        {
            Pawn = new PawnId(e.Str("Pawn")),
            Need = e.Get<MoodNeed>("Need", o),
            Reason = e.Get<MoodMethodReason>("Reason", o),
            Concern = e.GetOr("Concern", default(FilingKey), o),
            Thought = e.StrOr("Thought"),
            Target = e.Dbl("Target"),
            NeedBenefit = e.Dbl("NeedBenefit"),
        };

    protected override void WriteFields(Utf8JsonWriter w, MoodMethod v, Codecs o)
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

public sealed class CombatConverter : ObjectCodec<ReviewCombat>
{
    protected override ReviewCombat Read(JsonElement e, Codecs o) =>
        new(e.Get<Situation>("Situation", o), e.Get<EquatableArray<PawnId>>("Prey", o), e.Get<EquatableArray<PawnId>>("Hunters", o));

    protected override void WriteFields(Utf8JsonWriter w, ReviewCombat v, Codecs o)
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
