using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using BuildingHitPoints = RimGovernor.Contracts.Wire.Observations.BuildingHitPoints;

namespace RimGovernor.Brain.Store;

/// <summary>A persisted pawn's area restriction: the area's load id, or empty when the pawn is unrestricted.</summary>
public sealed record RoundsRecoveryRestriction(PawnId Pawn, string Area);

/// <summary>The persisted recovery safety evidence.</summary>
public sealed record RoundsRecoverySafety
{
    public EquatableArray<RoundsRecoveryRestriction> Restrictions { get; init; }
}

/// <summary>A persisted worker's observed state, read whole from its pawn row (#3768).</summary>
public sealed record RoundsRecoveryWorker
{
    public PawnId Pawn { get; init; }

    public bool Dead { get; init; }

    public bool Downed { get; init; }

    public bool Drafted { get; init; }

    public bool Mental { get; init; }

    public bool PlayerForced { get; init; }
}

/// <summary>
/// A persisted building's observed state, read whole: the hit points are null for a definition without them, the
/// fuel and its target null for a building without a refuelable comp.
/// </summary>
public sealed record RoundsRecoveryBuilding
{
    public string Id { get; init; } = "";

    public bool Broken { get; init; }

    public bool Forbidden { get; init; }

    public bool Burning { get; init; }

    public int? HitPoints { get; init; }

    public int? MaxHitPoints { get; init; }

    public double? Fuel { get; init; }

    public double? FuelTarget { get; init; }

    /// <summary>The policy row this record persists.</summary>
    internal RecoveryBuilding Building() => new()
    {
        Id = Id,
        HitPoints = (HitPoints, MaxHitPoints) is ({ } current, { } maximum)
            ? new BuildingHitPoints.Tracked { Current = current, Maximum = maximum }
            : new BuildingHitPoints.Untracked(),
        Broken = Broken,
        Forbidden = Forbidden,
        Burning = Burning,
        Fuel = (Fuel, FuelTarget) is ({ } level, { } target) ? new RecoveryFuel.Refuelable(level, target) : new RecoveryFuel.NotRefuelable(),
    };

    /// <summary>The record persisting a policy row.</summary>
    internal static RoundsRecoveryBuilding Of(RecoveryBuilding b)
    {
        var r = new RoundsRecoveryBuilding { Id = b.Id, Broken = b.Broken, Forbidden = b.Forbidden, Burning = b.Burning };
        if (b.HitPoints is BuildingHitPoints.Tracked hp)
        {
            r = r with { HitPoints = hp.Current, MaxHitPoints = hp.Maximum };
        }
        if (b.Fuel is RecoveryFuel.Refuelable fuel)
        {
            r = r with { Fuel = fuel.Level, FuelTarget = fuel.Target };
        }
        return r;
    }
}

/// <summary>
/// Retains the exact inputs for reproducible proposal readback. Nullable values are storage boundaries, not known
/// zero/false domain observations. It is keyed by the Need it serves and that Need's
/// world (#3304); a record an older journal keyed by its incident is dropped on load.
/// </summary>
public sealed record RoundsRecovery
{
    /// <summary>The Need the selection serves: <see cref="DisasterFamily.Services"/>.</summary>
    public NeedId Need { get; init; }

    /// <summary>The world whose Need the selection serves.</summary>
    public World World { get; init; }

    /// <summary>The Need's bound Methods whose plans are not retired, once each in id order: the MethodIds the selection skips.</summary>
    public EquatableArray<MethodId> Used { get; init; }

    public RoundsRecoverySafety Safety { get; init; } = new();

    public EquatableArray<RoundsRecoveryWorker> Workers { get; init; }

    public EquatableArray<RoundsRecoveryBuilding> Buildings { get; init; }

    public RecoverySelection Selection { get; init; } = new();

    /// <summary>The planning facts this record retains, lifted back into policy terms.</summary>
    internal RecoveryPlanning Planning()
    {
        return new RecoveryPlanning
        {
            Safety = new RecoverySafety { Restrictions = EquatableArray.From(Safety.Restrictions.Select(v => new RecoveryRestriction(v.Pawn, v.Area))) },
            Buildings = EquatableArray.From(Buildings.Select(v => v.Building())),
            Workers = EquatableArray.From(Workers.Select(v => new RecoveryWorker
            {
                Pawn = v.Pawn,
                Dead = v.Dead,
                Downed = v.Downed,
                Drafted = v.Drafted,
                Mental = v.Mental,
                PlayerForced = v.PlayerForced,
            })),
        };
    }
}

public sealed partial class Store
{
    /// <summary>The retained planning inputs, with no Need, used methods or selection yet.</summary>
    internal static RoundsRecovery RecoveryRecord(RecoveryPlanning p)
    {
        return new RoundsRecovery
        {
            Safety = new RoundsRecoverySafety
            {
                Restrictions = EquatableArray.From(p.Safety.Restrictions.Select(v => new RoundsRecoveryRestriction(v.Pawn, v.Area))),
            },
            Buildings = EquatableArray.From(p.Buildings.Select(RoundsRecoveryBuilding.Of)),
            Workers = EquatableArray.From(p.Workers.Select(v => new RoundsRecoveryWorker
            {
                Pawn = v.Pawn,
                Dead = v.Dead,
                Downed = v.Downed,
                Drafted = v.Drafted,
                Mental = v.Mental,
                PlayerForced = v.PlayerForced,
            })),
        };
    }

    /// <summary>
    /// Re-derives the stored recovery selection from its retained inputs and refuses a review whose selection is
    /// unowned, from the future, invalid or not what those inputs select.
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
        if (v.Need != DisasterFamily.Services || v.World != new World(r.Snapshot.Colony, r.Snapshot.Load, r.Snapshot.Map))
        {
            throw new DomainException("recovery selection lost need ownership");
        }
        var selection = RecoveryRules.SelectRecoveryMethods(v.Planning(), r.Disaster, v.Used, v.Selection.Tick);
        if (!string.Equals(StoreJson.Serialize(v.Selection), StoreJson.Serialize(selection), StringComparison.Ordinal))
        {
            throw new DomainException("recovery proposal differs from observed inputs");
        }
    }

    /// <summary>
    /// The recovery selection of this review for the <see cref="DisasterFamily.Services"/> Need in its world: null
    /// without a disaster history or while a Safeguard vetoes routine work.
    /// </summary>
    internal static RoundsRecovery? RoundsRecoveryFor(StoreTransaction tx, RoundsFacts f, DisasterHistory? h, Rounds review, Tick tick)
    {
        if (h is null || review.VetoNeed(default).Length != 0)
        {
            return null;
        }
        var r = RecoveryRecord(new RecoveryPlanning { Safety = f.RecoverySafety, Workers = f.RecoveryWorkers, Buildings = f.RecoveryBuildings });
        var owner = LoadNeedIn(tx, DisasterFamily.Services, review.Snapshot);
        r = r with
        {
            Need = owner.Need,
            World = new World(review.Snapshot.Colony, review.Snapshot.Load, review.Snapshot.Map),
            Used = RecoveryMethodsUsed(owner),
        };
        return r with { Selection = RecoveryRules.SelectRecoveryMethods(r.Planning(), h, r.Used, tick) };
    }

    /// <summary>
    /// The MethodIds a recovery selection skips: <paramref name="owner"/>'s bound Methods whose plans are not retired,
    /// once each, in id order. A retired service may be selected again while its building still needs it.
    /// </summary>
    public static EquatableArray<MethodId> RecoveryMethodsUsed(NeedOwner owner) =>
        EquatableArray.From(owner.Methods.Select(m => m.Method).Distinct().Order());
}
