using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>One settlement trade mission the economy/trade-mission Need drives: its minted id and its intent.</summary>
public sealed record TradeMissionEntry(string Id, TradeMission Mission);

/// <summary>
/// The colony's settlement trade missions under way (#3316), kept in the save as the
/// <c>family/economy/trade_missions</c> blob: what the world cannot show (the trip's crew, budget, demand, phase and
/// the purchase it committed to). A mission leaves the record once it is delivered or ended. It replaced the
/// trade-mission Standard, whose old blobs are dropped on load (decision 12).
/// </summary>
public sealed record TradeMissionsRecord
{
    /// <summary>The most missions the record holds; the Need plans one at a time.</summary>
    public const int MaxMissions = 8;

    public World World { get; init; }

    public IReadOnlyList<TradeMissionEntry> Missions { get; init; } = [];

    public void Validate(IGameDefinitionSemantics? definitions = null)
    {
        World.Validate();
        DomainException.Require(Missions.Count <= MaxMissions, "too many trade missions");
        for (var i = 0; i < Missions.Count; i++)
        {
            var entry = Missions[i];
            DomainException.Require(Families.ValidIdentity(entry.Id) && (i == 0 || string.CompareOrdinal(Missions[i - 1].Id, entry.Id) < 0), "invalid trade mission id");
            DomainException.Require(entry.Mission is not null && entry.Mission.HomeColony == World.Colony && entry.Mission.HomeMap == World.Map, "trade mission scope mismatch");
            entry.Mission.Validate(definitions);
            DomainException.Require(!entry.Mission.Over, "a trade mission that is over leaves the record");
        }
    }

    /// <summary>The record with <paramref name="id"/>'s mission replaced by <paramref name="mission"/>, or removed once it is over.</summary>
    public TradeMissionsRecord With(string id, TradeMission mission)
    {
        var missions = Missions.Where(m => m.Id != id).ToList();
        if (!mission.Over)
        {
            missions.Add(new TradeMissionEntry(id, mission));
        }
        return this with { Missions = [.. missions.OrderBy(m => m.Id, StringComparer.Ordinal)] };
    }
}

public sealed partial class Store
{
    public const string GovernorTradeMissionsKey = "family/economy/trade_missions";

    private const int MaxTradeMissionsBytes = 64 * 1024;

    internal static TradeMissionsRecord? LoadTradeMissionsIn(StoreTransaction tx)
    {
        var r = LoadSingleton<TradeMissionsRecord>(tx, "economy_trade_missions", MaxTradeMissionsBytes);
        return r is null ? null : Validated(r, () => r.Validate(tx.Definitions), "trade missions");
    }

    private static void PutTradeMissionsIn(StoreTransaction tx, TradeMissionsRecord r)
    {
        r.Validate(tx.Definitions);
        if (r.Missions.Count == 0)
        {
            _ = tx.Execute("DELETE FROM economy_trade_missions");
            return;
        }
        PutSingleton(tx, "economy_trade_missions", r, MaxTradeMissionsBytes);
    }

    /// <summary>
    /// The trade missions of the world's colony and map; empty when none or when the record belongs to another colony
    /// or map. A reload of the same colony keeps them: the crew and settlement ids outlive the load.
    /// </summary>
    public TradeMissionsRecord LoadTradeMissions(World w, CancellationToken cancellationToken) =>
        Run(tx => LoadTradeMissionsIn(tx) is { } r && r.World.Colony == w.Colony && r.World.Map == w.Map ? r with { World = w } : new TradeMissionsRecord { World = w }, cancellationToken);

    public Task<TradeMissionsRecord> LoadTradeMissionsAsync(World w, CancellationToken cancellationToken) => Task.Run(() => LoadTradeMissions(w, cancellationToken), cancellationToken);

    /// <summary>Replaces the stored trade missions with <paramref name="r"/>; an empty record clears them.</summary>
    public void SaveTradeMissions(TradeMissionsRecord r, CancellationToken cancellationToken) => Run(tx => PutTradeMissionsIn(tx, r), cancellationToken);

    /// <summary>
    /// The economy/trade-mission Need's claimed commit of one mission phase: the Method and the mission intent it
    /// carries (a committed purchase's return goods) are written in one transaction, and the record only when the
    /// Method is admitted.
    /// </summary>
    public NeedCommit CommitTradeMissionMethod(NeedId need, GenerationSnapshot current, MethodId method, string reason, PlanSpec plan, TradeMissionsRecord record, CancellationToken cancellationToken)
    {
        need.Validate();
        if (!method.IsValid) throw new DomainException("invalid need method id");
        if (plan.Actions.Count == 0) throw new DomainException("empty need method");
        record.Validate(_definitions);
        return Run(tx =>
        {
            var commit = CommitClaimedNeedMethodIn(tx, need, current, method, reason, plan);
            if (commit.Outcome == ClaimOutcome.Admitted)
            {
                PutTradeMissionsIn(tx, record);
            }
            return commit;
        }, cancellationToken);
    }
}
