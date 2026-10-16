using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The colony's creepjoiner inspection record (#3264), kept in the save as the <c>family/admin/creep_joiners</c> blob:
/// the pawns it ordered a surgical inspection of and those whose inspection bill has ended. Both lists are sorted and
/// disjoint. It replaced the ManageCreepJoiners Standard's <c>Record</c>, which was unread after a reload.
/// </summary>
public sealed record CreepJoinersRecord
{
    public World World { get; init; }

    public IReadOnlyList<PawnId> Ordered { get; init; } = [];

    public IReadOnlyList<PawnId> Done { get; init; } = [];

    /// <summary>The record of <paramref name="world"/> holding <paramref name="record"/>'s inspections, canonically ordered.</summary>
    public static CreepJoinersRecord Of(World world, CreepJoinerRecord record) => new()
    {
        World = world,
        Ordered = [.. record.Inspections.Where(e => e.Value == InspectionStage.Ordered).Select(e => e.Key).Order()],
        Done = [.. record.Inspections.Where(e => e.Value == InspectionStage.Done).Select(e => e.Key).Order()],
    };

    /// <summary>The inspections as the creepjoiner planning reads them.</summary>
    public CreepJoinerRecord Inspections() => new()
    {
        Inspections = Ordered.Select(p => (p, InspectionStage.Ordered)).Concat(Done.Select(p => (p, InspectionStage.Done)))
            .ToDictionary(e => e.p, e => e.Item2),
    };

    public void Validate()
    {
        World.Validate();
        DomainException.Require(Ordered.Count + Done.Count <= 256, "invalid creepjoiner record");
        foreach (var list in new[] { Ordered, Done })
        {
            for (var i = 0; i < list.Count; i++)
            {
                DomainException.Require(Families.ValidIdentity(list[i].Value) && (i == 0 || list[i - 1].CompareTo(list[i]) < 0), "invalid creepjoiner record pawn");
            }
        }
        DomainException.Require(!Ordered.Intersect(Done).Any(), "creepjoiner record pawn both ordered and done");
    }
}

public sealed partial class Store
{
    public const string GovernorCreepJoinersKey = "family/admin/creep_joiners";

    private const int MaxCreepJoinersBytes = 64 * 1024;

    internal static CreepJoinersRecord? LoadCreepJoinersIn(StoreTransaction tx)
    {
        var r = LoadSingleton<CreepJoinersRecord>(tx, "admin_creep_joiners", MaxCreepJoinersBytes);
        return r is null ? null : Validated(r, r.Validate, "creepjoiner record");
    }

    private static void PutCreepJoinersIn(StoreTransaction tx, CreepJoinersRecord r)
    {
        r.Validate();
        PutSingleton(tx, "admin_creep_joiners", r, MaxCreepJoinersBytes);
    }

    /// <summary>
    /// The creepjoiner inspection record of the world's colony and map; empty when none or when it belongs to another
    /// colony or map. A reload of the same colony keeps it: pawn ids outlive the load.
    /// </summary>
    public CreepJoinerRecord LoadCreepJoiners(World w, CancellationToken cancellationToken) =>
        Run(tx => LoadCreepJoinersIn(tx) is { } r && r.World.Colony == w.Colony && r.World.Map == w.Map ? r.Inspections() : new CreepJoinerRecord(), cancellationToken);

    public Task<CreepJoinerRecord> LoadCreepJoinersAsync(World w, CancellationToken cancellationToken) => Task.Run(() => LoadCreepJoiners(w, cancellationToken), cancellationToken);

    /// <summary>Replaces the stored creepjoiner inspection record with <paramref name="record"/> for <paramref name="w"/>.</summary>
    public void SaveCreepJoiners(World w, CreepJoinerRecord record, CancellationToken cancellationToken) =>
        Run(tx => PutCreepJoinersIn(tx, CreepJoinersRecord.Of(w, record)), cancellationToken);

    /// <summary>
    /// The admin/creep-joiners Need's claimed commit of one inspection order: the Method and the record that marks the
    /// pawn ordered are written in one transaction, and the record only when the Method is admitted.
    /// </summary>
    public NeedCommit CommitCreepJoinerMethod(NeedId need, GenerationSnapshot current, MethodId method, string reason, PlanSpec plan, CreepJoinerRecord record, CancellationToken cancellationToken)
    {
        need.Validate();
        if (!method.IsValid) throw new DomainException("invalid need method id");
        if (plan.Actions.Count == 0) throw new DomainException("empty need method");
        var stored = CreepJoinersRecord.Of(new World(current.Colony, current.Load, current.Map), record);
        stored.Validate();
        return Run(tx =>
        {
            var commit = CommitClaimedNeedMethodIn(tx, need, current, method, reason, plan);
            if (commit.Outcome == ClaimOutcome.Admitted)
            {
                PutCreepJoinersIn(tx, stored);
            }
            return commit;
        }, cancellationToken);
    }
}
