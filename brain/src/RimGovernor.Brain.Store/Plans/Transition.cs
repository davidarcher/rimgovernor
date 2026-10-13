using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>The kinds of progress transition the journal records (Go <c>transition.Kind</c>).</summary>
internal enum TransitionKind
{
    Prepare,
    Dispatch,
    Receipt,
    Observe,
    Hold,
    Cancel,
    Withdraw,
}

/// <summary>
/// One journaled step of an action's progress: a private persistence boundary, not a second progress model
/// (Go <c>transition</c>). Replaying an action's transitions through <see cref="Apply"/> rebuilds its
/// <see cref="Progress"/>. Only the fields a kind uses are written.
/// </summary>
internal sealed record Transition(TransitionKind Kind)
{
    public EquatableArray<CombatResult> Combat { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public AttemptId Attempt { get; init; }

    public Receipt Receipt { get; init; }

    public Observation? Observation { get; init; }

    public string Zone { get; init; } = "";

    public EquatableArray<CreatedZone> Stockpiles { get; init; }

    public string Bill { get; init; } = "";

    public EquatableArray<HeldReason> HeldReasons { get; init; }

    public NativeRefusal? Refusal { get; init; }

    /// <summary>Replays this transition onto <paramref name="p"/> through the domain's own checked methods.</summary>
    public Progress Apply(Progress p)
    {
        switch (Kind)
        {
            case TransitionKind.Prepare:
                return p.Prepare(Snapshot, Tick);
            case TransitionKind.Dispatch:
                return p.MarkDispatched(Snapshot, Tick);
            case TransitionKind.Receipt:
                if (p.Action.Kind == ActionKind.CombatBatch)
                {
                    return p.RecordCombatReceipt(Attempt, Receipt, Combat);
                }
                if (Stockpiles.Count > 0)
                {
                    return p.RecordStockpileReceipt(Attempt, Receipt, Stockpiles);
                }
                if (Zone.Length != 0)
                {
                    return p.RecordZoneReceipt(Attempt, Receipt, Zone);
                }
                if (Bill.Length != 0)
                {
                    return p.RecordBillReceipt(Attempt, Receipt, Bill);
                }
                return Refusal is not null ? p.RecordRefusal(Attempt, Refusal) : p.RecordReceipt(Attempt, Receipt);
            case TransitionKind.Observe:
                return p.Observe(Observation ?? throw new DomainException("observation required"), Snapshot);
            case TransitionKind.Hold:
                return p.Hold(HeldReasons, Tick);
            case TransitionKind.Cancel:
                return p.Cancel();
            case TransitionKind.Withdraw:
                return p.Withdraw(Snapshot, Tick);
            default:
                throw new DomainException($"unknown transition {Kind}");
        }
    }
}

public sealed class ObservationConverter : ObjectCodec<Observation>
{
    protected override Observation Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ActionId>("Action", o),
            e.Get<AttemptId>("Attempt", o),
            e.Get<GenerationSnapshot>("Snapshot", o),
            e.Get<Tick>("Tick", o),
            e.Get<Effect>("Effect", o),
            e.GetOr("Causality", ObservationCausality.None, o),
            e.GetOr("UnsuccessfulReason", UnsuccessfulReason.None, o)
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

public sealed class CreatedZoneConverter : ObjectCodec<CreatedZone>
{
    protected override CreatedZone Read(JsonElement e, Codecs o) => new(e.Str("Id"), e.Get<EquatableArray<Cell>>("Cells", o));

    protected override void WriteFields(Utf8JsonWriter w, CreatedZone v, Codecs o)
    {
        w.WriteString("Id", v.Id);
        w.Put("Cells", v.Cells, o);
    }
}

public sealed class NativeRefusalConverter : ObjectCodec<NativeRefusal>
{
    protected override NativeRefusal Read(JsonElement e, Codecs o) =>
        new(e.Str("Code"), e.Str("Reason"), e.GetOr("Class", RefusalClass.Unknown, o));

    protected override void WriteFields(Utf8JsonWriter w, NativeRefusal v, Codecs o)
    {
        w.WriteString("Code", v.Code);
        w.WriteString("Reason", v.Reason);
        w.Put("Class", v.Class, o);
    }
}

internal sealed class TransitionConverter : ObjectCodec<Transition>
{
    protected override Transition Read(JsonElement e, Codecs o) =>
        new(e.Get<TransitionKind>("Kind", o))
        {
            Combat = e.GetOr("Combat", default(EquatableArray<CombatResult>), o),
            Snapshot = e.GetOr("Snapshot", default(GenerationSnapshot), o),
            Tick = e.GetOr("Tick", default(Tick), o),
            Attempt = e.GetOr("Attempt", default(AttemptId), o),
            Receipt = e.GetOr("Receipt", Receipt.Accepted, o),
            Observation = e.GetOr<Observation?>("Observation", null, o),
            Zone = e.StrOr("Zone"),
            Stockpiles = e.GetOr("Stockpiles", default(EquatableArray<CreatedZone>), o),
            Bill = e.StrOr("Bill"),
            HeldReasons = e.GetOr("HeldReasons", default(EquatableArray<HeldReason>), o),
            Refusal = e.GetOr<NativeRefusal?>("Refusal", null, o),
        };

    protected override void WriteFields(Utf8JsonWriter w, Transition v, Codecs o)
    {
        w.Put("Kind", v.Kind, o);
        if (v.Combat.Count > 0)
        {
            w.Put("Combat", v.Combat, o);
        }
        if (v.Kind is TransitionKind.Prepare or TransitionKind.Dispatch or TransitionKind.Observe or TransitionKind.Withdraw)
        {
            w.Put("Snapshot", v.Snapshot, o);
        }
        if (v.Kind is TransitionKind.Prepare or TransitionKind.Dispatch or TransitionKind.Hold or TransitionKind.Withdraw)
        {
            w.Put("Tick", v.Tick, o);
        }
        if (v.Kind == TransitionKind.Receipt)
        {
            w.Put("Attempt", v.Attempt, o);
            w.Put("Receipt", v.Receipt, o);
        }
        if (v.Observation is { } observation)
        {
            w.Put("Observation", observation, o);
        }
        if (v.Zone.Length != 0)
        {
            w.WriteString("Zone", v.Zone);
        }
        if (v.Stockpiles.Count > 0)
        {
            w.Put("Stockpiles", v.Stockpiles, o);
        }
        if (v.Bill.Length != 0)
        {
            w.WriteString("Bill", v.Bill);
        }
        if (v.HeldReasons.Count > 0)
        {
            w.Put("HeldReasons", v.HeldReasons, o);
        }
        if (v.Refusal is { } refusal)
        {
            w.Put("Refusal", refusal, o);
        }
    }
}

public sealed class AdmissionConverter : ObjectCodec<Admission>
{
    protected override Admission Read(JsonElement e, Codecs o) =>
        new(e.Get<GenerationSnapshot>("Snapshot", o), e.Get<Tick>("Tick", o), e.Get<EquatableArray<Cell>>("Footprint", o));

    protected override void WriteFields(Utf8JsonWriter w, Admission v, Codecs o)
    {
        w.Put("Snapshot", v.Snapshot, o);
        w.Put("Tick", v.Tick, o);
        w.Put("Footprint", v.Footprint, o);
    }
}
