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

    /// <summary>
    /// The event loot whose forbid state the latest safety census found wrong (the review's cohort): unsafe stacks to
    /// forbid and safe forbidden stacks to allow. Unknown while the census cannot be read.
    /// </summary>
    public Fact<EquatableArray<StartingSupply>>? SupplySafety { get; init; }

    /// <summary>Whether a tomb, grave or morgue step is due; unknown while the rooms, plan or corpses cannot be read.</summary>
    public Fact<bool>? Burial { get; init; }

    /// <summary>Whether the work ledger's reconcile diff places or removes a bill (LedgerDiffOwed); unknown unless the ledger has declarers and read every bench's bills.</summary>
    public Fact<bool>? WorkLedger { get; init; }
}

/// <summary>Which of the colony admin family's Needs the runtime composes.</summary>
public sealed record AdminNeeds
{
    public bool CreepJoiners { get; init; }

    public bool SupplySafety { get; init; }

    public bool Burial { get; init; }

    public bool WorkLedger { get; init; }
}

/// <summary>
/// The colony admin family (#3264, #3304). Its Needs:
/// <list type="bullet">
/// <item><c>admin/creep-joiners</c> (Wellbeing): no creepjoiner whose downside has not shown is owed a weapon drop,
/// a surgical inspection or an isolation move, no arrested creepjoiner is owed a disarming surgery, and the
/// inspection record is current. It replaced the ManageCreepJoiners Concern. A weapon drop is not yet a supported
/// forecast outcome, so a Need with drops owed is held as <c>unsupported_forecast_precautionary_disarm</c>; the other
/// work is the runtime step's (the record is written with the inspection orders it admits).</item>
/// <item><c>admin/supply-safety</c> (Wellbeing): every event-loot stack's forbid state matches its safety, so no
/// unsafe stack is left allowed and no safe one forbidden. It replaced the ManageSupplySafety Concern; the runtime
/// step forbids the unsafe stacks first, then allows the safe ones, one Method per batch.</item>
/// <item><c>admin/burial</c> (Wellbeing): no tomb, grave or morgue step is due. It replaced the MaintainBurial
/// Concern; the runtime step builds the morgue, the tomb or the next plain grave.</item>
/// <item><c>admin/work-ledger</c> (Wellbeing): the benches' production bills equal the orders the declarers want, so
/// the ledger's reconcile places and removes nothing. It replaced the MaintainWorkLedger Concern; the runtime step
/// commits the reconcile's places and removals, one Method per diff, each bill claiming its bench and recipe.</item>
/// </list>
/// </summary>
public sealed class AdminFamily(AdminNeeds composed) : IFamily<AdminFacts>
{
    public const string FamilyId = "admin";

    public static readonly NeedId CreepJoiners = NeedId.Of(FamilyId, "creep-joiners");

    public static readonly NeedId SupplySafety = NeedId.Of(FamilyId, "supply-safety");

    public static readonly NeedId Burial = NeedId.Of(FamilyId, "burial");

    public static readonly NeedId WorkLedger = NeedId.Of(FamilyId, "work-ledger");

    /// <summary>The stable refusal subject of a creepjoiner weapon drop.</summary>
    public const string UnsupportedDisarm = "unsupported_forecast_precautionary_disarm";

    public const string UnsupportedDisarmDetail =
        "Precautionary disarming requires shared participant admission; immediate combat protection remains separately owned.";

    /// <summary>The family with only the creepjoiner Need, as the first slice composed it.</summary>
    public AdminFamily(bool creepJoiners = true) : this(new AdminNeeds { CreepJoiners = creepJoiners })
    {
    }

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(AdminFacts facts)
    {
        var needs = new List<Need>(4);
        if (composed.CreepJoiners)
        {
            needs.Add(CreepJoinersNeed(facts.CreepJoiners ?? new CreepJoinerWork()));
        }
        if (composed.SupplySafety)
        {
            needs.Add(SupplySafetyNeed(facts.SupplySafety ?? Fact.Unknown<EquatableArray<StartingSupply>>()));
        }
        if (composed.Burial)
        {
            needs.Add(BurialNeed(facts.Burial ?? Fact.Unknown<bool>()));
        }
        if (composed.WorkLedger)
        {
            needs.Add(WorkLedgerNeed(facts.WorkLedger ?? Fact.Unknown<bool>()));
        }
        return needs;
    }

    /// <summary>Only the weapon drop hold: the other Needs' work is the runtime step's, which reads the native targets and claims them.</summary>
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

    /// <summary>
    /// Unmet while the safety census leaves any event-loot stack to forbid or allow, unknown while it cannot be
    /// read, met otherwise. The deficit is the number of stacks.
    /// </summary>
    public static Need SupplySafetyNeed(Fact<EquatableArray<StartingSupply>> pending)
    {
        if (!pending.TryGetValue(out var rows))
        {
            return new Need { Id = SupplySafety, Tier = NeedTier.Wellbeing, Evidence = [new("event_loot", default)] };
        }
        var forbid = rows.Count(r => r.Forbid);
        return new Need
        {
            Id = SupplySafety,
            Tier = NeedTier.Wellbeing,
            Satisfaction = rows.Count > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known((double)rows.Count),
            Evidence = [new("forbid", Count(forbid)), new("allow", Count(rows.Count - forbid))],
        };
    }

    /// <summary>Unmet while a tomb, grave or morgue step is due, unknown while that cannot be read, met otherwise.</summary>
    public static Need BurialNeed(Fact<bool> owed)
    {
        if (!owed.TryGetValue(out var due))
        {
            return new Need { Id = Burial, Tier = NeedTier.Wellbeing, Evidence = [new("burial_owed", default)] };
        }
        return new Need
        {
            Id = Burial,
            Tier = NeedTier.Wellbeing,
            Satisfaction = due ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known(due ? 1.0 : 0.0),
            Evidence = [new("burial_owed", Fact.Known(due ? "true" : "false"))],
        };
    }

    /// <summary>Unmet while the ledger's reconcile places or removes a bill, unknown while the declarations or the bench readback are unread, met otherwise.</summary>
    public static Need WorkLedgerNeed(Fact<bool> owed)
    {
        if (!owed.TryGetValue(out var diff))
        {
            return new Need { Id = WorkLedger, Tier = NeedTier.Wellbeing, Evidence = [new("ledger_diff", default)] };
        }
        return new Need
        {
            Id = WorkLedger,
            Tier = NeedTier.Wellbeing,
            Satisfaction = diff ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = Fact.Known(diff ? 1.0 : 0.0),
            Evidence = [new("ledger_diff", Fact.Known(diff ? "true" : "false"))],
        };
    }

    private static Fact<string> Count(int n) => Fact.Known(n.ToString(CultureInfo.InvariantCulture));
}
