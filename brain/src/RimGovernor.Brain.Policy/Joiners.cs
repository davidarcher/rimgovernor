using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the joiners family reads (#3302): the review's facts, whose quest census (offers and sites), joiner letters,
/// royalty and capacity facts its predicates and choices read. Each is a complete native read; an unknown one
/// raises <see cref="RequiredFactException"/> for its Need.
/// </summary>
public sealed record JoinersFacts
{
    public RoundsFacts Rounds { get; init; } = new();

    /// <summary>The facts a review already holds.</summary>
    public static JoinersFacts From(RoundsFacts f) => new() { Rounds = f };
}

/// <summary>
/// The joiners family (#3302): the joiner and quest half of the old population Concern. Every Need is
/// Development; an unread census raises <see cref="RequiredFactException"/>.
/// <list type="bullet">
/// <item><c>joiners/offers</c>: no answerable joiner offer waits while the colony has room for one more colonist
/// (<see cref="PopulationRules.JoinerDeficit"/>). Its Method accepts the lowest-id offer, <c>joiner-accept-&lt;quest&gt;</c>.</item>
/// <item><c>joiners/letters</c>: no acceptable joiner letter waits while the colony has room
/// (<see cref="PopulationRules.JoinerLetterDeficit"/>). Its Method answers the lowest-id letter, <c>joiner-letter-&lt;id&gt;</c>.</item>
/// <item><c>joiners/ceremony</c>: no accepted bestowing ceremony waits to start (<see cref="CeremonyRules.CeremonyStart"/>).
/// Starting one is not yet a supported forecast outcome, so it is held as <c>unsupported_forecast_bestowing</c>.</item>
/// <item><c>joiners/quests</c>: no quest is worth accepting, and no accepted quest owes hosting, expedition, site or
/// ideology work or a refugee (<see cref="QuestSelection.QuestDeficit"/>, <see cref="QuestRefugees.RefugeeDeficit"/>).
/// Its Method accepts the best quest, <c>quest-accept-&lt;quest&gt;</c>; the runtime's work step does the site,
/// hack, expedition and hospitality work.</item>
/// <item><c>joiners/decrees</c>, <c>joiners/monuments</c>, <c>joiners/departures</c> and <c>joiners/grav-inspection</c>:
/// no accepted royal decree, quest monument, pawn-lend or camp departure, or grav-engine inspection is owed. Their
/// work is the runtime's.</item>
/// </list>
/// The Needs are independent; none waits on another. Methods on different quests run side by side.
/// </summary>
public sealed class JoinersFamily : IFamily<JoinersFacts>
{
    public const string FamilyId = "joiners";

    public static readonly NeedId Offers = NeedId.Of(FamilyId, "offers");

    public static readonly NeedId Letters = NeedId.Of(FamilyId, "letters");

    public static readonly NeedId Ceremony = NeedId.Of(FamilyId, "ceremony");

    public static readonly NeedId Quests = NeedId.Of(FamilyId, "quests");

    public static readonly NeedId Decrees = NeedId.Of(FamilyId, "decrees");

    public static readonly NeedId Monuments = NeedId.Of(FamilyId, "monuments");

    public static readonly NeedId Departures = NeedId.Of(FamilyId, "departures");

    public static readonly NeedId GravInspection = NeedId.Of(FamilyId, "grav-inspection");

    /// <summary>The stable refusal subject of a bestowing ceremony to start.</summary>
    public const string CeremonyHeld = "unsupported_forecast_bestowing";

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Pawns, FactFamily.Colony, FactFamily.World, FactFamily.Rooms];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(JoinersFacts facts)
    {
        var f = facts.Rounds;
        var capacity = PopulationRules.JoinerCapacity(f.JoinerCapacity());
        var known = f.QuestOffers.TryGetValue(out _);
        return
        [
            FromFact(Offers, PopulationRules.JoinerDeficit(f.QuestOffers, capacity, f), "joiner offers"),
            FromFact(Letters, PopulationRules.JoinerLetterDeficit(f.JoinerLetters, capacity), "joiner letters"),
            Ceremonies(f),
            Quest(Quests, known, () => QuestSelection.QuestDeficit(f).ValueOrDefault || QuestRefugees.RefugeeDeficit(f)),
            Quest(Decrees, known, () => QuestDecrees.DecreeDeficit(f)),
            Quest(Monuments, known, () => QuestMonumentPolicy.MonumentDeficit(f)),
            Quest(Departures, known, () => QuestDeparture.DepartureDeficit(f)),
            Quest(GravInspection, known, () => QuestGravInspection.GravInspectionDeficit(f)),
        ];
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(JoinersFacts facts) => [];

    /// <summary>The offer, letter and quest acceptances and the ceremony hold; the rest is the runtime's work.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, JoinersFacts facts)
    {
        var f = facts.Rounds;
        var capacity = PopulationRules.JoinerCapacity(f.JoinerCapacity());
        var output = new List<NeedMethod>();
        var joiner = PopulationRules.SelectJoinerMethod(f.QuestOffers, capacity, f);
        foreach (var need in ready)
        {
            if (need.Id == Offers && joiner.Reason == JoinerPlanReason.None)
            {
                output.Add(Accept(Offers, AcceptMethod("joiner", joiner.Quest), joiner));
            }
            else if (need.Id == Letters && PopulationRules.SelectJoinerLetter(f.JoinerLetters, capacity) is (var letter, true))
            {
                var answer = DialogAnswer.NewJoinerLetterAnswer(letter.Id, letter.Label, letter.Token);
                output.Add(new NeedMethod(Letters, LetterMethod(letter.Id), "",
                    id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewDialogAnswerAction(new ActionId(id.Value + "-0"), answer)])));
            }
            else if (need.Id == Ceremony)
            {
                output.Add(NeedMethod.Held(Ceremony, CeremonyHeld, "Ceremony participation requires shared forecast occupancy and outcome admission."));
            }
            else if (need.Id == Quests && QuestSelection.SelectQuestMethod(f) is { Reason: JoinerPlanReason.None } quest
                && !(joiner.Reason == JoinerPlanReason.None && joiner.Quest == quest.Quest))
            {
                output.Add(Accept(Quests, AcceptMethod("quest", quest.Quest), quest));
            }
        }
        return output;
    }

    /// <summary>The MethodId of accepting <paramref name="quest"/>, <c>&lt;step&gt;-accept-&lt;quest&gt;</c>: the same offer continues its open Method.</summary>
    public static MethodId AcceptMethod(string step, QuestId quest) => new(step + "-accept-" + quest.Value);

    /// <summary>The MethodId of answering joiner letter <paramref name="id"/>.</summary>
    public static MethodId LetterMethod(int id) => new("joiner-letter-" + id.ToString(CultureInfo.InvariantCulture));

    private static NeedMethod Accept(NeedId need, MethodId method, JoinerChoice choice)
    {
        var accept = new QuestAccept(choice.Quest, choice.Accepter, choice.RewardChoice);
        return new NeedMethod(need, method, "", id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewQuestAcceptAction(new ActionId(id.Value + "-0"), accept)]));
    }

    private static Need FromFact(NeedId id, Fact<bool> deficit, string fact) => Owed(id, deficit.TryGetValue(out var owed) ? owed : throw new RequiredFactException(fact));

    private static Need Owed(NeedId id, bool owed) => FoodRules.WholeNeed(id, NeedTier.Development, !owed);

    /// <summary>
    /// The ceremony Need: unmet while an accepted ceremony waits to start. With Royalty inactive none can, so it is met.
    /// </summary>
    private static Need Ceremonies(RoundsFacts f) =>
        Owed(Ceremony, f.Royalty is RoyaltyState.Active { Facts: var royalty } && CeremonyRules.CeremonyStart(royalty) is not null);

    /// <summary>A quest-census Need: <paramref name="owed"/>; an unread census raises <see cref="RequiredFactException"/>.</summary>
    private static Need Quest(NeedId id, bool known, Func<bool> owed) =>
        Owed(id, known ? owed() : throw new RequiredFactException("quest offers"));
}
