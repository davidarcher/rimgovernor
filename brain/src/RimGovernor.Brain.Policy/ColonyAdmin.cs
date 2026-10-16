using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The creep joiner work one family read found (#3264): weapon drops, surgical inspections, isolation moves and
/// arrested ones to disarm, the stored inspection record they were read against, and whether any of it is owed.
/// <see cref="Owed"/> is unknown while the colonist census or a creepjoiner's facts cannot be read.
/// </summary>
public sealed record CreepJoinerWork
{
    public Fact<bool> Owed { get; init; }

    public IReadOnlyList<WeaponDrop> Drops { get; init; } = [];

    public Inspections Inspections { get; init; } = new();

    public IReadOnlyList<IsolationMove> Isolation { get; init; } = [];

    public DisarmWork Disarm { get; init; } = new();

    /// <summary>The colony's inspection record as stored (the <c>family/admin/creep_joiners</c> record).</summary>
    public CreepJoinerRecord Record { get; init; } = new();

    /// <summary>How many separate pieces of work are owed: drops, inspection orders, isolation moves and disarm orders.</summary>
    public int Count => Drops.Count + Inspections.Orders.Count + Isolation.Count + Disarm.Orders.Count;

    /// <summary>Whether the review leaves a different inspection record than the stored one (an ended bill, a pawn gone).</summary>
    public bool RecordChanged => Inspections.Record.Encode() != Record.Encode();
}

/// <summary>What the colony admin family reads; a section is null when its Need is not composed.</summary>
public sealed record AdminFacts
{
    public CreepJoinerWork? CreepJoiners { get; init; }
}

/// <summary>
/// The colony admin family (#3264). Today it carries one Need:
/// <list type="bullet">
/// <item><c>admin/creep-joiners</c> (Wellbeing): no creepjoiner whose downside has not shown is owed a weapon drop,
/// a surgical inspection or an isolation move, no arrested creepjoiner is owed a disarming surgery, and the
/// inspection record is current. It replaced the ManageCreepJoiners Concern. A weapon drop is not yet a supported
/// forecast outcome, so a Need with drops owed is held as <c>unsupported_forecast_precautionary_disarm</c>; the other
/// work is the runtime step's (the record is written with the inspection orders it admits).</item>
/// </list>
/// </summary>
public sealed class AdminFamily(bool creepJoiners = true) : IFamily<AdminFacts>
{
    public const string FamilyId = "admin";

    public static readonly NeedId CreepJoiners = NeedId.Of(FamilyId, "creep-joiners");

    /// <summary>The stable refusal subject of a creepjoiner weapon drop.</summary>
    public const string UnsupportedDisarm = "unsupported_forecast_precautionary_disarm";

    public const string UnsupportedDisarmDetail =
        "Precautionary disarming requires shared participant admission; immediate combat protection remains separately owned.";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(AdminFacts facts)
    {
        var needs = new List<Need>(1);
        if (creepJoiners)
        {
            needs.Add(CreepJoinersNeed(facts.CreepJoiners ?? new CreepJoinerWork()));
        }
        return needs;
    }

    /// <summary>Only the weapon drop hold: the creepjoiner orders are the runtime step's, which claims each pawn and writes the record.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, AdminFacts facts)
    {
        var output = new List<NeedMethod>();
        if (ready.Any(n => n.Id == CreepJoiners) && facts.CreepJoiners is { Drops.Count: > 0 })
        {
            output.Add(NeedMethod.Held(CreepJoiners, UnsupportedDisarm, UnsupportedDisarmDetail));
        }
        return output;
    }

    /// <summary>Unmet while any creepjoiner work is owed (including a record change), unknown while it cannot be read, met otherwise.</summary>
    public static Need CreepJoinersNeed(CreepJoinerWork work)
    {
        if (!work.Owed.TryGetValue(out var owed))
        {
            return new Need { Id = CreepJoiners, Tier = NeedTier.Wellbeing, Evidence = [new("creepjoiner_work", default)] };
        }
        return new Need
        {
            Id = CreepJoiners,
            Tier = NeedTier.Wellbeing,
            Satisfaction = owed ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)work.Count),
            Evidence =
            [
                new("weapon_drops", Count(work.Drops.Count)),
                new("inspections", Count(work.Inspections.Orders.Count)),
                new("isolation_moves", Count(work.Isolation.Count)),
                new("disarm_orders", Count(work.Disarm.Orders.Count)),
            ],
        };
    }

    private static Fact<string> Count(int n) => Fact.Known(n.ToString(CultureInfo.InvariantCulture));
}
