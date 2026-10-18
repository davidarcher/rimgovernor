using TechLevel = RimGovernor.Contracts.Wire.Defs.TechLevel;

namespace RimGovernor.Brain.Policy;

/// <summary>Where one goal stands.</summary>
public enum GoalState
{
    /// <summary>The goal's predicate holds.</summary>
    Met,

    /// <summary>An owning Need is working toward it.</summary>
    Pursuing,

    /// <summary>It cannot advance now: its facts are unread or its owner is blocked; <see cref="GoalStatus.Reason"/> says which.</summary>
    Blocked,

    /// <summary>No Need pursues the goal; <see cref="GoalStatus.Reason"/> says why.</summary>
    NoMethod,

    /// <summary>Every route the forecast found to the goal breaks a safety floor the colony holds today (#3503); <see cref="GoalStatus.Reason"/> names the floor.</summary>
    Infeasible,
}

/// <summary>One goal's standing: its state with the reason, the Need that pursues it and how far it is from met.</summary>
public sealed record GoalStatus(GoalRecord Goal, Satisfaction Satisfaction, GoalState State, string Reason, NeedId Owner, Fact<double> Deficit)
{
    /// <summary>
    /// The game day tier 2 estimates the goal completes (#3505): derived each review from the colony model and never stored. Unknown for a
    /// goal the model has no path for, one it cannot complete inside its horizon and one already met.
    /// </summary>
    public Fact<int> EtaDay { get; init; }
}

/// <summary>A theme's progress: how many of its goals stand met.</summary>
public sealed record ThemeStatus(int Id, int Met, int Total);

/// <summary>What an owning Need's progress row says about a goal's owner: a method it works, and why it is blocked when it is.</summary>
public sealed record OwnerProgress(bool Method, string Blocked);

/// <summary>
/// What a goal asks the existing channels for (#3494): research projects for the research-wanted set, stock floors
/// for the resource Need, a colonist head count for the joiner and lance population target and a head count per race
/// for the herd plan. The goals Family proposes no plans of its own.
/// </summary>
public sealed record GoalDemand
{
    public static readonly GoalDemand None = new();

    public IReadOnlyList<string> Research { get; init; } = [];

    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    public int Colonists { get; init; }

    public IReadOnlyDictionary<Resource, long> Animals { get; init; } = new Dictionary<Resource, long>();

    /// <summary>A Believers goal lacks believers: a prisoner of another ideoligion is converted first (<see cref="PopulationRules.PrisonerIntent"/>).</summary>
    public bool Believers { get; init; }

    /// <summary>The prisoners the goals name (#3499): the prisoner step recruits them whatever their worth.</summary>
    public IReadOnlyList<PawnId> Prisoners { get; init; } = [];

    /// <summary>The quests the goals name (#3499): the quest step considers them first.</summary>
    public IReadOnlyList<QuestId> Quests { get; init; } = [];

    /// <summary>The garments the goals want worn (#3496): the outfit policy requires each for its first <see cref="WornGoal.Count"/> eligible colonists and the clothing runway prices the missing ones.</summary>
    public IReadOnlyList<WornGoal> Worn { get; init; } = [];

    /// <summary>What the goals ask the build channels for (#3496): the rooms and the hospital bed floor.</summary>
    public GoalBuildDemand Build { get; init; } = GoalBuildDemand.None;

    /// <summary>The unmet goals as the forecast's ledger values them (#3503).</summary>
    public EquatableArray<ForecastGoalValue> Values { get; init; }
}

/// <summary>
/// What the goals ask the planned-room, hospital and station channels to build (#3496). It rides
/// <see cref="RoundsFacts.GoalBuild"/>, so a room's goal is one more demand to the planner that owns the room.
/// </summary>
public sealed record GoalBuildDemand
{
    public static readonly GoalBuildDemand None = new();

    /// <summary>The roles whose child room a goal names: the nursery, playroom, classroom and deathrest chamber (<see cref="ChildRoomsRules.GoalRoomNeeds"/>).</summary>
    public EquatableArray<RoomRole> Rooms { get; init; }

    /// <summary>The hosted medical beds a Hospital goal asks for; 0 for none. It is the minimum bed count of <see cref="HospitalRules.SelectHospitalBed"/> and the station graph's injured floor.</summary>
    public int HospitalBeds { get; init; }

    /// <summary>The turret count a built-count goal for a turret asks for; 0 for none. It is a floor under <see cref="DefenseBudget.TurretBudget"/>.</summary>
    public int Turrets { get; init; }
}

/// <summary>The planner that places a built-count goal's building (#3496).</summary>
public enum BuiltPlacer
{
    /// <summary>Nothing places the definition: the goal has no method.</summary>
    None,

    /// <summary>The defense layout's turret tier: the goal raises the turret budget and the <c>layout/defense</c> Need builds.</summary>
    Turret,

    /// <summary>The goals family's trade beacon step.</summary>
    Beacon,

    /// <summary>The goals family's standalone geothermal generator step.</summary>
    Geothermal,
}

/// <summary>A worn-count goal's demand: <see cref="Count"/> colonists wear <see cref="Garment"/>, of <see cref="Stuff"/> when it names one.</summary>
public sealed record WornGoal(Resource Garment, Resource Stuff, int Count);

/// <summary>What the goals Family reads (#3494): the player's goals and the review facts they are judged by.</summary>
public sealed record GoalsFacts
{
    public IReadOnlyList<GoalRecord> Goals { get; init; } = [];

    public IReadOnlyList<ThemeRecord> Themes { get; init; } = [];

    public Fact<ResearchFacts> Research { get; init; }

    public Fact<TechTier> Tier { get; init; }

    public Fact<EquatableArray<Amount>> Stock { get; init; }

    public Fact<long> Colonists { get; init; }

    public Fact<EquatableArray<UpkeepAnimal>> Animals { get; init; }

    public Fact<Ideoligion> Ideology { get; init; }

    public Fact<bool> IdeologyInstalled { get; init; }

    /// <summary>The colonists' pawn rows: the believer census a Believers goal is judged by.</summary>
    public Fact<EquatableArray<WorkPawn>> Pawns { get; init; }

    /// <summary>The reform the ideoligion goals plan (#3500); unknown while the ideoligion or catalog is unread.</summary>
    public Fact<DesignChoice> Reform { get; init; }

    /// <summary>The goal the reform in flight serves: the pending record's, else the chosen reform's.</summary>
    public int? ReformGoal { get; init; }

    /// <summary>The room census a room goal is judged by.</summary>
    public Fact<RoomObservation> Rooms { get; init; }

    /// <summary>The review's facts: the prisoner, custody and quest censuses the named-prisoner and named-quest goals are judged by.</summary>
    public RoundsFacts Rounds { get; init; } = new();

    /// <summary>The research each harvested stuff waits on (a plant's sowing prerequisites, keyed by its harvested thing def), the way a worn-count goal reaches the research-wanted set (#3496).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> StuffResearch { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The safety floor every forecast route to a goal breaks, by goal need id (#3503): the latest coupled selection's <c>goal_infeasible</c> findings.</summary>
    public IReadOnlyDictionary<string, string> Infeasible { get; init; } = new Dictionary<string, string>();

    /// <summary>The game day tier 2 estimates each goal completes, by goal need id (#3505): the latest review's colony model.</summary>
    public IReadOnlyDictionary<string, int> Etas { get; init; } = new Dictionary<string, int>();

    /// <summary>The progress row of each owning Need that has one.</summary>
    public IReadOnlyDictionary<NeedId, OwnerProgress> Owners { get; init; } = new Dictionary<NeedId, OwnerProgress>();

    /// <summary>The facts a review already holds, with the goals and the owners' rows.</summary>
    public static GoalsFacts From(RoundsFacts f, Fact<TechTier> tier, Fact<EquatableArray<WorkPawn>> pawns, IReadOnlyList<GoalRecord> goals, IReadOnlyList<ThemeRecord> themes, IReadOnlyDictionary<NeedId, OwnerProgress> owners, IReadOnlyDictionary<string, IReadOnlyList<string>>? stuffResearch = null) => new()
    {
        Pawns = pawns,
        Goals = goals,
        Themes = themes,
        Research = f.Research,
        Tier = tier,
        Stock = f.Resources,
        Colonists = f.Colonists,
        Animals = f.AnimalUpkeep.Animals,
        Ideology = f.Ideology,
        IdeologyInstalled = f.IdeologyInstalled,
        Rounds = f,
        Owners = owners,
        StuffResearch = stuffResearch ?? new Dictionary<string, IReadOnlyList<string>>(),
    };
}

/// <summary>
/// The goals Family (#3494): the player's goals as Development Needs <c>goals/&lt;kind&gt;/&lt;id&gt;</c>. It owns status
/// only. A goal becomes demand on the channels that already exist (<see cref="GoalRules.DemandOf"/>), and an unmet
/// goal's Method is a hold naming what pursues it (<c>pursuing:&lt;owner need&gt;</c>), what blocks it
/// (<c>blocked:&lt;reason&gt;</c>) or why nothing does (<c>no_method:&lt;reason&gt;</c>). A kind the brain does not pursue is
/// accepted and reported.
/// </summary>
public sealed class GoalsFamily : IFamily<GoalsFacts>
{
    public const string FamilyId = "goals";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Research, FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(GoalsFacts facts) =>
        [.. facts.Goals.Select(goal => GoalRules.Evaluate(goal, facts)).Select(s => new Need
        {
            Id = GoalRules.NeedOf(s.Goal),
            Tier = NeedTier.Development,
            Satisfaction = s.Satisfaction,
            Deficit = s.Deficit,
        })];

    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, GoalsFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            var goal = facts.Goals.FirstOrDefault(g => GoalRules.NeedOf(g) == need.Id);
            if (goal is null || GoalRules.OwnStep(goal))
            {
                // A goal the family places itself reports through its work step.
                continue;
            }
            var status = GoalRules.Evaluate(goal, facts);
            var (subject, detail) = status.State switch
            {
                GoalState.Pursuing => ("pursuing:" + status.Owner.Value, "Pursued by " + status.Owner.Value + "."),
                GoalState.Blocked => ("blocked:" + status.Reason, "The goal cannot advance: " + status.Reason + "."),
                GoalState.Infeasible => ("infeasible:" + status.Reason, "Every route to the goal breaks the " + status.Reason + " floor."),
                _ => ("no_method:" + status.Reason, "Nothing pursues this goal: " + status.Reason + "."),
            };
            output.Add(NeedMethod.Held(need.Id, subject, detail));
        }
        return output;
    }
}

public static partial class GoalRules
{
    /// <summary>The NeedId of <paramref name="goal"/>: <c>goals/&lt;kind&gt;/&lt;id&gt;</c>.</summary>
    public static NeedId NeedOf(GoalRecord goal) => NeedId.Of(GoalsFamily.FamilyId, KindSlug(goal.Kind), goal.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The lowercase hyphenated name of a kind.</summary>
    public static string KindSlug(GoalKind kind)
    {
        var name = kind.ToString();
        var text = new System.Text.StringBuilder(name.Length + 2);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                text.Append('-');
            }
            text.Append(char.ToLowerInvariant(name[i]));
        }
        return text.ToString();
    }

    /// <summary>Evaluates one goal against the facts: the predicate, then who pursues it, then whether the forecast found a route that holds every floor (#3503).</summary>
    public static GoalStatus Evaluate(GoalRecord goal, GoalsFacts f)
    {
        var status = EvaluateOwned(goal, f);
        if (status.State != GoalState.Met && f.Etas.TryGetValue(NeedOf(goal).Value, out var eta))
        {
            status = status with { EtaDay = Fact.Known(eta) };
        }
        return status.State is GoalState.Pursuing or GoalState.Blocked && f.Infeasible.TryGetValue(NeedOf(goal).Value, out var floor)
            ? status with { State = GoalState.Infeasible, Reason = floor }
            : status;
    }

    private static GoalStatus EvaluateOwned(GoalRecord goal, GoalsFacts f)
    {
        Fact<bool> met;
        Fact<double> deficit;
        NeedId owner;
        switch (goal.Kind)
        {
            case GoalKind.ResearchProject:
                (met, deficit) = ProjectMet(goal.Target, f.Research);
                owner = EconomyFamily.Research;
                break;
            case GoalKind.ResearchTier:
                (met, deficit) = Enum.TryParse<TechTier>(goal.Target, true, out _) ? TierMet(goal.Target, f.Tier) : LevelMet(goal.Target, f.Research);
                owner = EconomyFamily.Research;
                break;
            case GoalKind.StockCount:
                (met, deficit) = Shortfall(goal.Count, f.Stock.TryGetValue(out var stock) ? Fact.Known(stock.Where(a => a.Resource == new Resource(goal.Target)).Sum(a => a.Count)) : Fact.Unknown<long>());
                owner = EconomyFamily.Resource;
                break;
            case GoalKind.Colonists:
                (met, deficit) = Shortfall(goal.Count, f.Colonists);
                owner = JoinersFamily.Offers;
                break;
            case GoalKind.Animals:
                (met, deficit) = Shortfall(goal.Count, f.Animals.TryGetValue(out var animals) ? Fact.Known((long)animals.Count(a => a.Definition == new Resource(goal.Target))) : Fact.Unknown<long>());
                owner = AnimalsFamily.Herd;
                break;
            case GoalKind.WornCount:
                return Worn(goal, f);
            case GoalKind.Precept:
            case GoalKind.Meme:
            case GoalKind.VeneratedAnimal:
                return Adoption(goal, f);
            case GoalKind.Believers:
                return Believing(goal, f);
            case GoalKind.NamedPrisoner:
                return NamedPrisoner(goal, f);
            case GoalKind.NamedQuest:
                return NamedQuest(goal, f);
            case GoalKind.RoomRole:
                return Room(goal, f);
            case GoalKind.BuiltCount:
                return Built(goal, f);
            default:
                return new GoalStatus(goal, Satisfaction.Unknown, GoalState.NoMethod, NoPursuit, default, default);
        }
        if (!met.TryGetValue(out var isMet))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, deficit);
        }
        return isMet ? new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, deficit) : Pursued(goal, f, owner, deficit);
    }

    /// <summary>An unmet goal an owning Need can work: blocked when that Need's progress row says why it has no method, else pursuing.</summary>
    private static GoalStatus Pursued(GoalRecord goal, GoalsFacts f, NeedId owner, Fact<double> deficit) =>
        f.Owners.TryGetValue(owner, out var row) && !row.Method && row.Blocked != ""
            ? new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, row.Blocked, owner, deficit)
            : new GoalStatus(goal, Satisfaction.Unmet, GoalState.Pursuing, "", owner, deficit);

    /// <summary>
    /// A worn-count goal (#3496): met once <c>Count</c> colonists wear the garment (of the stuff, when named). Otherwise the
    /// outfit policy requires it for the first eligible colonists and the clothing runway asks for the material, both
    /// owned by the resource Need; with no colonist who can wear it nothing pursues the goal.
    /// </summary>
    private static GoalStatus Worn(GoalRecord goal, GoalsFacts f)
    {
        var owner = EconomyFamily.Resource;
        if (!f.Rounds.Gear.TryGetValue(out var gear))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
        }
        var want = new WornGoal(new Resource(goal.Target), new Resource(goal.Qualifier), goal.Count);
        var wearing = WearingCount(gear, want);
        var deficit = Fact.Known((double)Math.Max(0, goal.Count - wearing));
        if (wearing >= goal.Count)
        {
            return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, deficit);
        }
        return EligibleWearers(gear, want.Garment).Count == 0
            ? new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "no_wearer", default, deficit)
            : Pursued(goal, f, owner, deficit);
    }

    /// <summary>The trade beacon's definition; policy is game-free, so the name is a constant as <see cref="PowerRules.GeothermalDefinition"/> is.</summary>
    public const string TradeBeaconDefinition = "OrbitalTradeBeacon";

    /// <summary>Who places <paramref name="definition"/>: the turret ladder through the defense layout, the beacon and the geothermal generator through the goals family's own steps; nothing places any other definition.</summary>
    public static BuiltPlacer PlacerOf(string definition) =>
        DefenseTurretsPolicy.TurretLadder.Contains(definition) ? BuiltPlacer.Turret
        : definition == TradeBeaconDefinition ? BuiltPlacer.Beacon
        : definition == PowerRules.GeothermalDefinition ? BuiltPlacer.Geothermal
        : BuiltPlacer.None;

    /// <summary>Whether the goals family itself places the goal's building (its Need has a work step instead of a held Method).</summary>
    public static bool OwnStep(GoalRecord goal) => goal.Kind == GoalKind.BuiltCount && PlacerOf(goal.Target) is BuiltPlacer.Beacon or BuiltPlacer.Geothermal;

    /// <summary>How many finished buildings of <paramref name="definition"/> stand; unknown while the building table is unread.</summary>
    public static Fact<long> BuiltOf(RoundsFacts f, string definition) =>
        f.BuiltBuildings.TryGetValue(out var built) ? Fact.Known(built.Where(a => a.Resource == new Resource(definition)).Sum(a => a.Count)) : Fact.Unknown<long>();

    /// <summary>
    /// A built-count goal (#3496): met once <c>Count</c> finished buildings of the definition stand. A turret is built by the
    /// defense layout (<c>layout/defense</c>); the trade beacon and the geothermal generator by the goal's own Need; any other
    /// definition has no placer and reports it.
    /// </summary>
    private static GoalStatus Built(GoalRecord goal, GoalsFacts f)
    {
        var placer = PlacerOf(goal.Target);
        var owner = placer switch { BuiltPlacer.Turret => LayoutFamily.Defense, BuiltPlacer.None => default, _ => NeedOf(goal) };
        var (met, deficit) = Shortfall(goal.Count, BuiltOf(f.Rounds, goal.Target));
        if (met.TryGetValue(out var isMet) && isMet)
        {
            return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, deficit);
        }
        if (placer == BuiltPlacer.None)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "no placer for " + goal.Target, default, deficit);
        }
        return met.IsKnown ? Pursued(goal, f, owner, deficit) : new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, deficit);
    }

    /// <summary>The colonists wearing the goal's garment, of its stuff when it names one.</summary>
    private static int WearingCount(GearObservation gear, WornGoal want) =>
        gear.Pawns.Count(p => p.LoadoutModel.TryGetValue(out var model)
            && model.Worn.Items.Any(o => o.Definition == want.Garment && (want.Stuff.Value == "" || o.Stuff == want.Stuff)));

    /// <summary>The colonists who can wear <paramref name="garment"/> and are not soldiers, children or prisoners, in pawn id order: the outfit rule's candidates.</summary>
    private static List<GearPawn> EligibleWearers(GearObservation gear, Resource garment) =>
        [.. gear.Pawns
            .Where(p => !p.Blocked && p.Policy.TryGetValue(out var state)
                && GearLoadoutRules.DeriveGearRole(state.Role) is not (GearRole.Soldier or GearRole.Child or GearRole.Prisoner)
                && state.Definitions.Items.Any(d => d.Name == garment.Value && d.Adult))
            .OrderBy(p => p.Pawn.Value, StringComparer.Ordinal)];

    /// <summary>A named prisoner: met once a colonist, pursued by the prisoners/recruit Need while held and recruitable.</summary>
    private static GoalStatus NamedPrisoner(GoalRecord goal, GoalsFacts f)
    {
        var owner = PrisonersFamily.Recruit;
        var one = Fact.Known(1.0);
        var pawn = new PawnId(goal.Target);
        if (!f.Rounds.Prisoners.TryGetValue(out var prisoners))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
        }
        var row = prisoners.FirstOrDefault(p => p.Pawn == pawn);
        if (row is null)
        {
            if (!f.Rounds.Custody.TryGetValue(out var custody))
            {
                return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
            }
            var colonist = custody.Any(c => c.Pawn == pawn && c.Admitted.TryGetValue(out var admitted) && admitted && c.Dead.TryGetValue(out var dead) && !dead);
            return colonist
                ? new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, Fact.Known(0.0))
                : new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, NotAPrisoner, default, one);
        }
        if (!row.Dead.TryGetValue(out var gone) || !row.Recruitable.TryGetValue(out var recruitable))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
        }
        if (gone)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, NotAPrisoner, default, one);
        }
        return recruitable ? Pursued(goal, f, owner, one) : new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "not_recruitable", default, one);
    }

    /// <summary>
    /// A room of a role (#3496). Met once a standing room has the role (a Hospital goal: once the hosted medical beds
    /// reach the goal's bed count, 1 when it names none). The child rooms are pursued by <c>shelter/sleeping</c>, which
    /// builds the room the goal adds to its planned-room needs; the hospital by <c>medical/care</c>. A role no planner
    /// stages has no method.
    /// </summary>
    private static GoalStatus Room(GoalRecord goal, GoalsFacts f)
    {
        if (!Wire.TryParse<RoomRole>(goal.Target, out var role) || role == RoomRole.Unset)
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.NoMethod, "unknown_role", default, default);
        }
        var staged = StagedRole(role);
        var owner = role == RoomRole.Hospital ? MedicalRules.MedicalCareNeed : staged ? ShelterFamily.Sleeping : default;
        if (!f.Rooms.TryGetValue(out var rooms))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
        }
        double deficit;
        if (role == RoomRole.Hospital)
        {
            var (hosted, known) = HospitalRules.HostedMedicalBeds(rooms, f.Rounds.Sleeping, false);
            if (!known)
            {
                return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
            }
            deficit = Math.Max(0, HospitalBedsOf(goal) - hosted);
        }
        else
        {
            deficit = rooms.Rooms.Any(r => r.Role.TryGetValue(out var have) && have == role) ? 0 : 1;
        }
        if (deficit == 0)
        {
            return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, Fact.Known(0.0));
        }
        var unmet = Fact.Known(deficit);
        if (!staged)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, NoPursuit, default, unmet);
        }
        if (role == RoomRole.WorshipRoom)
        {
            if (f.IdeologyInstalled.TryGetValue(out var installed) && !installed)
            {
                return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "missing_dlc", default, unmet);
            }
            if (f.Ideology.TryGetValue(out var ideo) && ideo.RequiredBuildings().Count == 0)
            {
                return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "no_required_buildings", default, unmet);
            }
        }
        return Pursued(goal, f, owner, unmet);
    }

    /// <summary>Whether a planner stages a room of the role: the hospital and the child rooms.</summary>
    private static bool StagedRole(RoomRole role) => role == RoomRole.Hospital || ChildRoomsRules.GoalRoles.Contains(role) || role == RoomRole.WorshipRoom;

    /// <summary>The hosted medical beds a Hospital goal asks for: its count, 1 when it names none.</summary>
    private static int HospitalBedsOf(GoalRecord goal) => Math.Max(1, goal.Count);

    /// <summary>A named quest: met when it completes, pursued by the quest step (the offers step for a joiner) while an admission check passes, blocked on the check that fails, no method once it expires or fails.</summary>
    private static GoalStatus NamedQuest(GoalRecord goal, GoalsFacts f)
    {
        var one = Fact.Known(1.0);
        if (!f.Rounds.QuestOffers.TryGetValue(out var offers))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", JoinersFamily.Quests, default);
        }
        var quest = new QuestId(goal.Target);
        var offer = offers.FirstOrDefault(o => o.Quest == quest);
        if (offer is null)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "unknown_quest", default, one);
        }
        var owner = PopulationRules.IsJoinerOffer(offer.ScriptDef) ? JoinersFamily.Offers : JoinersFamily.Quests;
        switch (offer.State)
        {
            case "EndedSuccess":
                return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", owner, Fact.Known(0.0));
            case "EndedOfferExpired":
                return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, Expired, default, one);
            case "Ongoing":
                return Pursued(goal, f, owner, one);
            case "NotYetAccepted":
                var reason = owner == JoinersFamily.Offers ? QuestSelection.JoinerThreatReason(offer, f.Rounds) : QuestSelection.NamedQuestReason(offer, f.Rounds);
                if (reason == Expired)
                {
                    return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, Expired, default, one);
                }
                return reason.IsNone ? Pursued(goal, f, owner, one) : new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, reason.ToString(), owner, one);
            default:
                return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "ended", default, one);
        }
    }

    /// <summary>Every theme's progress: its member goals that stand met.</summary>
    public static IReadOnlyList<ThemeStatus> Themes(GoalsFacts f)
    {
        var met = f.Goals.Where(g => Evaluate(g, f).State == GoalState.Met).Select(g => g.Id).ToHashSet();
        return [.. f.Themes.Select(t => new ThemeStatus(t.Id, t.Members.Count(met.Contains), t.Members.Count))];
    }

    /// <summary>The unfinished projects a research goal waits on, in project id order: its project, or the first project of each tier it still has to reach and the unfinished projects of a tech level; none once it is met or while the census is unread.</summary>
    internal static IReadOnlyList<string> ResearchOf(GoalRecord goal, GoalsFacts f)
    {
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        if (goal.Kind == GoalKind.ResearchProject)
        {
            if (goal.Target != "" && Evaluate(goal, f).State != GoalState.Met && f.Research.TryGetValue(out _))
            {
                projects.Add(goal.Target);
            }
        }
        else if (goal.Kind == GoalKind.ResearchTier)
        {
            if (Enum.TryParse<TechTier>(goal.Target, true, out var target) && f.Tier.TryGetValue(out var tier))
            {
                for (var t = tier + 1; t <= target; t++)
                {
                    projects.Add(TechTierRules.Unlocks[t][0]);
                }
            }
            if (Evaluate(goal, f).State != GoalState.Met)
            {
                projects.UnionWith(LevelProjects(goal.Target, f.Research));
            }
        }
        return [.. projects];
    }

    /// <summary>What the goals ask the existing channels for.</summary>
    public static GoalDemand DemandOf(GoalsFacts f)
    {
        var research = new SortedSet<string>(StringComparer.Ordinal);
        var stock = new Dictionary<Resource, long>();
        var animals = new Dictionary<Resource, long>();
        var colonists = 0;
        var prisoners = new List<PawnId>();
        var quests = new List<QuestId>();
        var rooms = new SortedSet<RoomRole>();
        var hospitalBeds = 0;
        var turrets = 0;
        var worn = new List<WornGoal>();
        foreach (var goal in f.Goals)
        {
            switch (goal.Kind)
            {
                case GoalKind.WornCount when goal.Target != "" && goal.Count > 0:
                    worn.Add(new WornGoal(new Resource(goal.Target), new Resource(goal.Qualifier), goal.Count));
                    // The stuff's own research (devilstrand: the plant's sowing project) joins the research-wanted set while the goal is unmet.
                    if (goal.Qualifier != "" && f.Research.TryGetValue(out var census) && Evaluate(goal, f).State is not GoalState.Met && f.StuffResearch.TryGetValue(goal.Qualifier, out var projects))
                    {
                        research.UnionWith(projects.Where(p => !census.Finished.Contains(new ResearchProjectId(p))));
                    }
                    break;
                case GoalKind.NamedPrisoner when goal.Target != "":
                    prisoners.Add(new PawnId(goal.Target));
                    break;
                case GoalKind.NamedQuest when goal.Target != "":
                    quests.Add(new QuestId(goal.Target));
                    break;
                case GoalKind.ResearchProject:
                case GoalKind.ResearchTier:
                    research.UnionWith(ResearchOf(goal, f));
                    break;
                case GoalKind.StockCount when goal.Target != "" && goal.Count > 0:
                    stock[new Resource(goal.Target)] = Math.Max(stock.GetValueOrDefault(new Resource(goal.Target)), goal.Count);
                    break;
                case GoalKind.Animals when goal.Target != "" && goal.Count > 0:
                    animals[new Resource(goal.Target)] = Math.Max(animals.GetValueOrDefault(new Resource(goal.Target)), goal.Count);
                    break;
                case GoalKind.Colonists:
                    colonists = Math.Max(colonists, goal.Count);
                    break;
                case GoalKind.BuiltCount when goal.Count > 0 && PlacerOf(goal.Target) == BuiltPlacer.Turret:
                    // The turret goal keeps asking once met: a lower budget would plan the standing turrets away.
                    turrets = Math.Max(turrets, goal.Count);
                    break;
                case GoalKind.RoomRole when Wire.TryParse<RoomRole>(goal.Target, out var room):
                    // A room goal keeps asking once the room stands: dropping the need would end the room's role in the plan.
                    if (room == RoomRole.Hospital)
                    {
                        hospitalBeds = Math.Max(hospitalBeds, HospitalBedsOf(goal));
                    }
                    else if (ChildRoomsRules.GoalRoles.Contains(room))
                    {
                        rooms.Add(room);
                    }
                    break;
            }
        }
        var believers = ConversionRules.Shortfall(f.Goals, f.Ideology, f.Pawns).TryGetValue(out var lacking) && lacking > 0;
        return new GoalDemand { Research = [.. research], Stock = stock, Colonists = colonists, Animals = animals, Prisoners = prisoners, Quests = quests, Believers = believers, Worn = worn, Build = new GoalBuildDemand { Rooms = EquatableArray.From(rooms), HospitalBeds = hospitalBeds, Turrets = turrets }, Values = ValuesOf(f) };
    }

    /// <summary>The review facts with the goals' stock floors in them, and everything <see cref="Stamp"/> carries.</summary>
    public static RoundsFacts Apply(RoundsFacts f, GoalDemand d) => Stamp(f with { ResourceNeeds = ResourceRules.ResourceConcernTargets(f.ResourceNeeds, d.Stock) }, d);

    /// <summary>The facts with the goals' colonist head count, herd targets, named quests, named prisoners and believer demand: what every read of the world carries.</summary>
    public static RoundsFacts Stamp(RoundsFacts f, GoalDemand d)
    {
        f = StampWorn(f with { ColonistGoal = d.Colonists, AnimalGoals = d.Animals, NamedQuests = d.Quests, GoalBuild = d.Build, GoalValues = d.Values }, d.Worn);
        return f.PrisonerColony.TryGetValue(out var colony) ? f with { PrisonerColony = Fact.Known(colony with { NamedPrisoners = d.Prisoners, BelieversWanted = d.Believers }) } : f;
    }

    /// <summary>
    /// The worn-count goals on the facts (#3496): each garment joins <c>Required</c> of the first <c>Count</c> eligible
    /// colonists (so their outfit allows it whatever their role), and the missing ones (Count less those already worn)
    /// become bills in <see cref="RoundsFacts.WornBills"/> for the clothing runway. Idempotent, so every read can stamp.
    /// </summary>
    private static RoundsFacts StampWorn(RoundsFacts f, IReadOnlyList<WornGoal> worn)
    {
        if (!f.Gear.TryGetValue(out var gear))
        {
            return f with { WornBills = [] };
        }
        var required = new Dictionary<PawnId, List<string>>();
        var bills = new List<OpenBill>();
        foreach (var w in worn)
        {
            foreach (var p in EligibleWearers(gear, w.Garment).Take(w.Count))
            {
                if (!required.TryGetValue(p.Pawn, out var names))
                {
                    required[p.Pawn] = names = [];
                }
                names.Add(w.Garment.Value);
            }
            var missing = w.Count - WearingCount(gear, w);
            if (missing > 0
                && f.Garments.Items.FirstOrDefault(g => g.Definition == w.Garment) is { } garment
                && GearRules.GearFilter(garment.Slots, w.Stuff, f.Items) is { } filter)
            {
                bills.Add(new OpenBill { Count = missing, Slots = Fact.Known(garment.Slots), Filter = EquatableArray.From(filter.Select(r => r.Value)) });
            }
        }
        if (required.Count > 0)
        {
            GearPawn Add(GearPawn p) => required.TryGetValue(p.Pawn, out var names) && p.Policy.TryGetValue(out var state)
                ? p with { Policy = Fact.Known(state with { Required = EquatableArray.From(state.Required.Items.Union(names, StringComparer.Ordinal)) }) }
                : p;
            f = f with { Gear = Fact.Known(gear with { Pawns = [.. gear.Pawns.Select(Add)] }) };
        }
        return f with { WornBills = bills };
    }

    private const string NoPursuit = "no_pursuit";

    private const string NotAPrisoner = "not a prisoner";

    private const string Expired = "expired";

    /// <summary>
    /// A believer goal: met once enough free colonists hold the ideoligion. Otherwise the ideology family converts
    /// the lowest-certainty colonist of another ideoligion (<c>ideology/convert/&lt;pawn&gt;</c>), which waits on the
    /// roles Need for a moral guide; an ideoligion with no such role has no method.
    /// </summary>
    private static GoalStatus Believing(GoalRecord goal, GoalsFacts f)
    {
        if (f.IdeologyInstalled.TryGetValue(out var installed) && !installed)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "missing_dlc", default, Fact.Known(1.0));
        }
        if (!ConversionRules.Shortfall([goal], f.Ideology, f.Pawns).TryGetValue(out var lacking) || !ConversionRules.Owed([goal], f.Ideology, f.Pawns).TryGetValue(out var owed))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", default, default);
        }
        if (lacking == 0)
        {
            return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", default, Fact.Known(0.0));
        }
        var deficit = Fact.Known((double)lacking);
        if (!owed.IsOwed)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, "no_convertible_colonist", default, deficit);
        }
        var need = ConversionRules.NeedOf(owed.Target);
        return owed.Block switch
        {
            ConversionBlock.NoGuideRole => new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, IdeologyFamily.NoGuideRole, default, deficit),
            ConversionBlock.Unassigned => new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, "no_moral_guide", IdeologyFamily.Roles, deficit),
            _ => new GoalStatus(goal, Satisfaction.Unmet, GoalState.Pursuing, "", need, deficit),
        };
    }

    private static GoalStatus Adoption(GoalRecord goal, GoalsFacts f)
    {
        if (f.IdeologyInstalled.TryGetValue(out var installed) && !installed)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "missing_dlc", default, Fact.Known(1.0));
        }
        if (!f.Ideology.TryGetValue(out var ideo))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", default, default);
        }
        var held = goal.Kind switch
        {
            GoalKind.Precept => ideo.Facts.Precepts.Any(p => p.Def == goal.Target),
            GoalKind.Meme => ideo.Facts.Memes.Contains(goal.Target),
            _ => ideo.Facts.VeneratedAnimals.Contains(goal.Target),
        };
        if (held)
        {
            return new GoalStatus(goal, Satisfaction.Met, GoalState.Met, "", default, Fact.Known(0.0));
        }
        var unmet = Fact.Known(1.0);
        var defined = goal.Kind switch
        {
            GoalKind.Precept => ideo.Defs.Precepts.ContainsKey(goal.Target),
            GoalKind.Meme => ideo.Defs.Memes.ContainsKey(goal.Target),
            _ => goal.Target != "",
        };
        if (!defined)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "unavailable", default, unmet);
        }
        if (!ideo.Facts.Development.TryGetValue(out var development))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", default, default);
        }
        if (!development.Fluid)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.NoMethod, "not fluid", default, unmet);
        }
        var owner = IdeologyFamily.Reform;
        if (!f.Reform.TryGetValue(out var reform))
        {
            return new GoalStatus(goal, Satisfaction.Unknown, GoalState.Blocked, "facts_unread", owner, default);
        }
        if (reform.Blocked.TryGetValue(goal.Id, out var missing))
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, missing, owner, unmet);
        }
        if (reform.Comparison?.Decision == DesignDecision.Unsafe)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, "not_calm", owner, unmet);
        }
        if (!development.CanReform)
        {
            return new GoalStatus(goal, Satisfaction.Unmet, GoalState.Blocked, FormattableString.Invariant($"development points {development.DevelopmentPoints}/{development.NextReformPoints}"), owner, unmet);
        }
        return new GoalStatus(goal, Satisfaction.Unmet, GoalState.Pursuing, f.ReformGoal == goal.Id ? "" : "queued", owner, unmet);
    }

    /// <summary>The meme, precept and venerated-animal goals the ideoligion's reform plans toward.</summary>
    public static IReadOnlyList<IdeoGoalTarget> IdeoTargets(IEnumerable<GoalRecord> goals) =>
        [
            .. goals
                .Where(g => g.Kind is GoalKind.Precept or GoalKind.Meme or GoalKind.VeneratedAnimal && g.Target != "")
                .Select(g => new IdeoGoalTarget(g.Id, g.Kind switch
                {
                    GoalKind.Meme => IdeoGoalKind.Meme,
                    GoalKind.Precept => IdeoGoalKind.Precept,
                    _ => IdeoGoalKind.Animal,
                }, g.Target)),
        ];

    private static (Fact<bool> Met, Fact<double> Deficit) ProjectMet(string project, Fact<ResearchFacts> research)
    {
        if (!research.TryGetValue(out var facts) || project == "" || !facts.Projects.Any(p => p.Value == project))
        {
            return (default, default);
        }
        var finished = facts.Finished.Any(p => p.Value == project);
        return (Fact.Known(finished), Fact.Known(finished ? 0.0 : 1.0));
    }

    private static (Fact<bool> Met, Fact<double> Deficit) TierMet(string target, Fact<TechTier> tier)
    {
        if (!tier.TryGetValue(out var reached) || !Enum.TryParse<TechTier>(target, true, out var wanted))
        {
            return (default, default);
        }
        return (Fact.Known(reached >= wanted), Fact.Known((double)Math.Max(0, wanted - reached)));
    }

    /// <summary>The unfinished projects at the tech level <paramref name="target"/> names; none when it names no level or the census is unread.</summary>
    private static IReadOnlyList<string> LevelProjects(string target, Fact<ResearchFacts> research)
    {
        if (!Enum.TryParse<TechLevel>(target, true, out var level) || !Enum.IsDefined(level) || !research.TryGetValue(out var facts))
        {
            return [];
        }
        var finished = facts.Finished.Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        return [.. facts.Levels.Where(l => l.Value == level && !finished.Contains(l.Key.Value)).Select(l => l.Key.Value)];
    }

    /// <summary>A tech level that is not a construction tier: met once no listed project at that level is unfinished.</summary>
    private static (Fact<bool> Met, Fact<double> Deficit) LevelMet(string target, Fact<ResearchFacts> research)
    {
        if (!Enum.TryParse<TechLevel>(target, true, out var level) || !Enum.IsDefined(level) || !research.TryGetValue(out _))
        {
            return (default, default);
        }
        var open = LevelProjects(target, research).Count;
        return (Fact.Known(open == 0), Fact.Known((double)open));
    }

    private static (Fact<bool> Met, Fact<double> Deficit) Shortfall(long wanted, Fact<long> have)
    {
        if (!have.TryGetValue(out var count))
        {
            return (default, default);
        }
        return (Fact.Known(count >= wanted), Fact.Known((double)Math.Max(0, wanted - count)));
    }
}
