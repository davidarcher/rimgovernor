using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using PolicyPawnId = RimGovernor.Brain.Policy.PawnId;

namespace RimGovernor.Brain.Store;

/// <summary>A persisted pawn's area restriction; an unknown area persists as null (Go <c>RoundsRecoveryRestriction</c>).</summary>
public sealed record RoundsRecoveryRestriction(PolicyPawnId Pawn, string? Area);

/// <summary>The persisted recovery safety evidence (Go <c>RoundsRecoverySafety</c>).</summary>
public sealed record RoundsRecoverySafety
{
    public EquatableArray<RoundsRecoveryRestriction> Restrictions { get; init; }
}

/// <summary>A persisted worker's observed state; every observation is unknown (null) or a value (Go <c>RoundsRecoveryWorker</c>).</summary>
public sealed record RoundsRecoveryWorker
{
    public PolicyPawnId Pawn { get; init; }

    public bool? Dead { get; init; }

    public bool? Downed { get; init; }

    public bool? Drafted { get; init; }

    public bool? Mental { get; init; }

    public bool? PlayerForced { get; init; }
}

/// <summary>A persisted building's observed state; every observation is unknown (null) or a value (Go <c>RoundsRecoveryBuilding</c>).</summary>
public sealed record RoundsRecoveryBuilding
{
    public string Id { get; init; } = "";

    public bool? UsesHitPoints { get; init; }

    public bool? Broken { get; init; }

    public bool? Forbidden { get; init; }

    public bool? Burning { get; init; }

    public bool? Refuelable { get; init; }

    public long? HitPoints { get; init; }

    public long? MaxHitPoints { get; init; }

    public double? Fuel { get; init; }

    public double? FuelTarget { get; init; }
}

/// <summary>
/// Retains the exact inputs for reproducible proposal readback. Nullable values are storage boundaries, not known
/// zero/false domain observations (Go <c>RoundsRecovery</c>).
/// </summary>
public sealed record RoundsRecovery
{
    /// <summary>The RecoverDisasterServices occurrence the selection serves.</summary>
    public IncidentId Incident { get; init; }

    public EquatableArray<MethodId> Used { get; init; }

    /// <summary>Null when the safety census was unknown.</summary>
    public RoundsRecoverySafety? Safety { get; init; }

    /// <summary>Null when the worker census was unknown.</summary>
    public EquatableArray<RoundsRecoveryWorker>? Workers { get; init; }

    /// <summary>Null when the building census was unknown.</summary>
    public EquatableArray<RoundsRecoveryBuilding>? Buildings { get; init; }

    public RecoverySelection Selection { get; init; } = new();

    /// <summary>The planning facts this record retains, lifted back into policy terms (Go <c>planning</c>).</summary>
    internal RecoveryPlanning Planning()
    {
        var planning = new RecoveryPlanning();
        if (Safety is { } safety)
        {
            planning = planning with
            {
                Safety = Fact.Known(
                    new RecoverySafety { Restrictions = EquatableArray.From(safety.Restrictions.Select(v => new RecoveryRestriction(v.Pawn, PersistedFact.OfReference(v.Area)))) }
                ),
            };
        }
        if (Workers is { } workers)
        {
            planning = planning with
            {
                Workers = Fact.Known(
                    EquatableArray.From(
                        workers.Select(v => new RecoveryWorker
                        {
                            Pawn = v.Pawn,
                            Dead = PersistedFact.Of(v.Dead),
                            Downed = PersistedFact.Of(v.Downed),
                            Drafted = PersistedFact.Of(v.Drafted),
                            Mental = PersistedFact.Of(v.Mental),
                            PlayerForced = PersistedFact.Of(v.PlayerForced),
                        })
                    )
                ),
            };
        }
        if (Buildings is { } buildings)
        {
            planning = planning with
            {
                Buildings = Fact.Known(
                    EquatableArray.From(
                        buildings.Select(v => new RecoveryBuilding
                        {
                            Id = v.Id,
                            UsesHitPoints = PersistedFact.Of(v.UsesHitPoints),
                            Broken = PersistedFact.Of(v.Broken),
                            Forbidden = PersistedFact.Of(v.Forbidden),
                            Burning = PersistedFact.Of(v.Burning),
                            Refuelable = PersistedFact.Of(v.Refuelable),
                            HitPoints = PersistedFact.Of(v.HitPoints),
                            MaxHitPoints = PersistedFact.Of(v.MaxHitPoints),
                            Fuel = PersistedFact.Of(v.Fuel),
                            FuelTarget = PersistedFact.Of(v.FuelTarget),
                        })
                    )
                ),
            };
        }
        return planning;
    }
}

public sealed partial class Store
{
    /// <summary>The retained planning inputs, with no incident, used methods or selection yet (Go <c>recoveryRecord</c>).</summary>
    internal static RoundsRecovery RecoveryRecord(RecoveryPlanning p)
    {
        var r = new RoundsRecovery();
        if (p.Safety.TryGetValue(out var safety))
        {
            r = r with
            {
                Safety = new RoundsRecoverySafety
                {
                    Restrictions = EquatableArray.From(safety.Restrictions.Select(v => new RoundsRecoveryRestriction(v.Pawn, PersistedFact.Reference(v.Area)))),
                },
            };
        }
        if (p.Workers.TryGetValue(out var workers))
        {
            r = r with
            {
                Workers = EquatableArray.From(
                    workers.Select(v => new RoundsRecoveryWorker
                    {
                        Pawn = v.Pawn,
                        Dead = PersistedFact.Value(v.Dead),
                        Downed = PersistedFact.Value(v.Downed),
                        Drafted = PersistedFact.Value(v.Drafted),
                        Mental = PersistedFact.Value(v.Mental),
                        PlayerForced = PersistedFact.Value(v.PlayerForced),
                    })
                ),
            };
        }
        if (p.Buildings.TryGetValue(out var buildings))
        {
            r = r with
            {
                Buildings = EquatableArray.From(
                    buildings.Select(v => new RoundsRecoveryBuilding
                    {
                        Id = v.Id,
                        UsesHitPoints = PersistedFact.Value(v.UsesHitPoints),
                        Broken = PersistedFact.Value(v.Broken),
                        Forbidden = PersistedFact.Value(v.Forbidden),
                        Burning = PersistedFact.Value(v.Burning),
                        Refuelable = PersistedFact.Value(v.Refuelable),
                        HitPoints = PersistedFact.Value(v.HitPoints),
                        MaxHitPoints = PersistedFact.Value(v.MaxHitPoints),
                        Fuel = PersistedFact.Value(v.Fuel),
                        FuelTarget = PersistedFact.Value(v.FuelTarget),
                    })
                ),
            };
        }
        return r;
    }

    /// <summary>
    /// Re-derives the stored recovery selection from its retained inputs and refuses a review whose selection is
    /// unowned, from the future, invalid or not what those inputs select (Go <c>validateRoundsRecovery</c>).
    /// </summary>
    internal static void ValidateRoundsRecovery(Rounds r)
    {
        if (r.Recovery is not { } v)
        {
            return;
        }
        if (!r.Enabled || r.Disaster is null || v.Selection.Tick > r.Tick || (!r.Immediate && v.Selection.Tick != r.Tick))
        {
            throw new DomainException("invalid routine recovery selection");
        }
        v.Selection.Validate();
        var binding = r.IncidentBinding(v.Incident);
        if (binding is null || binding.Kind != PolicyFunctions.RecoverDisasterServices)
        {
            throw new DomainException("recovery selection lost incident ownership");
        }
        var selection = PolicyFunctions.SelectRecoveryMethods(v.Planning(), r.Disaster, v.Used, v.Selection.Tick);
        if (!string.Equals(StoreJson.Serialize(v.Selection), StoreJson.Serialize(selection), StringComparison.Ordinal))
        {
            throw new DomainException("recovery proposal differs from observed inputs");
        }
    }

    /// <summary>
    /// The recovery selection of this review: null without a disaster history, an owned RecoverDisasterServices
    /// occurrence or an admitting Safeguard (Go <c>roundsRecovery</c>).
    /// </summary>
    internal static RoundsRecovery? RoundsRecoveryFor(StoreTransaction tx, RoundsFacts f, DisasterHistory? h, Rounds review, Tick tick)
    {
        if (h is null)
        {
            return null;
        }
        var binding = review.Incident(PolicyFunctions.RecoverDisasterServices);
        if (binding is null)
        {
            return null;
        }
        var state = LoadIncident(tx, binding.Incident);
        if (review.VetoIncident(state.Incident).Length != 0)
        {
            return null;
        }
        var r = RecoveryRecord(new RecoveryPlanning { Safety = f.RecoverySafety, Workers = f.RecoveryWorkers, Buildings = f.RecoveryBuildings });
        var used = tx.Query(
            "SELECT method_id FROM incident_methods WHERE incident_id=$id ORDER BY method_id LIMIT 257",
            [("$id", binding.Incident)],
            row => new MethodId(row.GetString(0))
        );
        r = r with { Incident = binding.Incident, Used = EquatableArray.From(used) };
        return r with { Selection = PolicyFunctions.SelectRecoveryMethods(r.Planning(), h, r.Used, tick) };
    }
}
