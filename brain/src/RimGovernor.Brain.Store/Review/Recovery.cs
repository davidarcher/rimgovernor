using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>A persisted pawn's area restriction: the area's load id, or empty when the pawn is unrestricted.</summary>
public sealed record ReviewRecoveryRestriction(PawnId Pawn, string Area);

/// <summary>The persisted recovery safety evidence.</summary>
public sealed record ReviewRecoverySafety
{
    public EquatableArray<ReviewRecoveryRestriction> Restrictions { get; init; }
}

/// <summary>
/// Retains the exact inputs for reproducible proposal readback. Nullable values are storage boundaries, not known
/// zero/false domain observations. It is keyed by the Need it serves and that Need's
/// world (#3304); a record an older journal keyed by its incident is dropped on load.
/// </summary>
public sealed record Recovery
{
    /// <summary>The Need the selection serves: <see cref="DisasterFamily.Services"/>.</summary>
    public NeedId Need { get; init; }

    /// <summary>The world whose Need the selection serves.</summary>
    public World World { get; init; }

    /// <summary>The Need's bound Methods whose plans are not retired, once each in id order: the MethodIds the selection skips.</summary>
    public EquatableArray<MethodId> Used { get; init; }

    public ReviewRecoverySafety Safety { get; init; } = new();

    public EquatableArray<MoodPawn> Workers { get; init; }

    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.BuildingState> Buildings { get; init; }

    public RecoverySelection Selection { get; init; } = new();

    /// <summary>The planning facts this record retains, lifted back into policy terms.</summary>
    internal RecoveryPlanning Planning()
    {
        return new RecoveryPlanning
        {
            Safety = new RecoverySafety { Restrictions = EquatableArray.From(Safety.Restrictions.Select(v => new RimGovernor.Brain.GameFacts.Observations.RecoveryRestriction
            {
                Pawn = new() { Id = v.Pawn.Value }, AreaId = v.Area.Length == 0 ? null : v.Area,
            })) },
            Buildings = Buildings,
            Workers = Workers,
        };
    }
}

public sealed partial class Store
{
    /// <summary>The retained planning inputs, with no Need, used methods or selection yet.</summary>
    internal static Recovery RecoveryRecord(RecoveryPlanning p)
    {
        return new Recovery
        {
            Safety = new ReviewRecoverySafety
            {
                Restrictions = EquatableArray.From(p.Safety.Restrictions.Select(v => new ReviewRecoveryRestriction(new PawnId(v.Pawn.Id), v.AreaId ?? ""))),
            },
            Buildings = p.Buildings,
            Workers = p.Workers,
        };
    }

    /// <summary>
    /// Re-derives the stored recovery selection from its retained inputs and refuses a review whose selection is
    /// unowned, from the future, invalid or not what those inputs select.
    /// </summary>
    internal static void ValidateReviewRecovery(ReviewState r)
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
    internal static Recovery? RecoveryFor(StoreTransaction tx, ReviewFacts f, DisasterHistory? h, ReviewState review, Tick tick)
    {
        if (h is null || review.VetoNeed(default).Length != 0)
        {
            return null;
        }
        var r = RecoveryRecord(new RecoveryPlanning { Safety = f.RecoverySafety, Workers = f.MoodPawns, Buildings = f.RecoveryBuildings });
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
