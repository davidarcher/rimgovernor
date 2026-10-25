using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the rooms family reads (#3255), each measured fresh with no latch: the lighting and flooring reviews of the
/// room census, the artists and the bedrooms owed a sculpture, the basic and hosted comfort of the shared rooms, the
/// PartySpot step and the party the mood census owes.
/// </summary>
public sealed record RoomsFacts
{
    /// <summary>The work-cell lighting review with the plan's unlit rooms folded in; unknown while the lighting census or the sky is unread.</summary>
    public Fact<LightingReview> Lighting { get; init; }

    /// <summary>The room terrain review; unknown while the terrain census is unread.</summary>
    public Fact<FlooringReview> Flooring { get; init; }

    /// <summary>The colonists' work profiles: who can sculpt and who is inspired to.</summary>
    public Fact<EquatableArray<PawnProfile>> Profiles { get; init; }

    /// <summary>A bedroom below target, weakest in beauty, has a free cell for a sculpture.</summary>
    public Fact<bool> SculptureRoomsOwed { get; init; }

    /// <summary>The unfiltered comfort census: every indoor seat and recreation source, whatever room hosts it.</summary>
    public ComfortObservation? BasicComfort { get; init; }

    /// <summary>The hosted comfort review: hosting rooms and proof of use, against the review's comfort history.</summary>
    public ComfortReview? Hosting { get; init; }

    /// <summary>The PartySpot is missing, duplicated or outranked by a better shared room, and the one-shot is not spent.</summary>
    public Fact<bool> PartySpotOwed { get; init; }

    /// <summary>A standing bench of a planned room has the work chair the plan chose for it missing from its interaction cell (WorkChairRules.OwedSeats).</summary>
    public Fact<bool> WorkChairsOwed { get; init; }

    /// <summary>The party the mood census owes now (an organizer named), or none.</summary>
    public Fact<GatheringPlan> Gathering { get; init; }
}

/// <summary>
/// The rooms family (#3255): the shared rooms the colony lives and works in.
/// <list type="bullet">
/// <item><c>rooms/lighting</c> is met while no roofed work cell measures dark and the plan's lit rooms have their lamps.
/// Its lamps are building work the runtime commits under the Need.</item>
/// <item><c>rooms/flooring</c> is met while every tiered room and the paying traffic and firebreak cells have their
/// floor. Its floors are building work.</item>
/// <item><c>rooms/art</c> is met while no artist is inspired and either no bedroom is owed a sculpture or no colonist
/// can sculpt. Its pinned sculpture bills are the work ledger's.</item>
/// <item><c>rooms/comfort</c> is met while a reachable table, seat and recreation source stand for everyone, with the
/// recreation variety. Its furniture is building work.</item>
/// <item><c>rooms/hosting</c>, after comfort, is met while the shared rooms host dining and recreation with proof of
/// use. Its furniture is building work.</item>
/// <item><c>rooms/party-spot</c>, after hosting, is met while one PartySpot stands in the best shared room. Its
/// placement or retirement is one building Method.</item>
/// <item><c>rooms/work-chairs</c>, after comfort, is met while every standing bench whose planned room chose a work chair
/// has it on its interaction cell. Each chair is one building Method.</item>
/// <item><c>rooms/gatherings</c>, after the party spot, is met while no party is owed. A party is held: gathering
/// participation needs shared forecast occupancy and need effects.</item>
/// </list>
/// </summary>
public sealed class RoomsFamily(bool lighting, bool flooring, bool art, bool comfort, bool gatherings) : IFamily<RoomsFacts>
{
    public const string FamilyId = "rooms";

    public static readonly NeedId Lighting = NeedId.Of(FamilyId, "lighting");

    public static readonly NeedId Flooring = NeedId.Of(FamilyId, "flooring");

    public static readonly NeedId Art = NeedId.Of(FamilyId, "art");

    public static readonly NeedId Comfort = NeedId.Of(FamilyId, "comfort");

    public static readonly NeedId Hosting = NeedId.Of(FamilyId, "hosting");

    public static readonly NeedId PartySpot = NeedId.Of(FamilyId, "party-spot");

    public static readonly NeedId WorkChairs = NeedId.Of(FamilyId, "work-chairs");

    public static readonly NeedId Gatherings = NeedId.Of(FamilyId, "gatherings");

    /// <summary>The stable hold subject of an owed party.</summary>
    public const string UnsupportedGathering = "unsupported_forecast_gathering";

    public string Id => FamilyId;

    /// <summary>The room census and its lighting, terrain and comfort, the colony's buildings, the pawns' work and mood, the sky and the emergency census a party waits out.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Rooms, FactFamily.Colony, FactFamily.Pawns, FactFamily.World, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    /// <summary>Whether the Need is one of the four comfort steps, which build at the Comfort tier.</summary>
    public static bool ComfortStep(ConcernId concern) =>
        concern == Comfort.FilingKey || concern == Hosting.FilingKey || concern == PartySpot.FilingKey || concern == WorkChairs.FilingKey;

    public IReadOnlyList<Need> Derive(RoomsFacts facts)
    {
        var needs = new List<Need>(8);
        if (lighting)
        {
            needs.Add(LightingNeed(facts));
        }
        if (flooring)
        {
            needs.Add(FlooringNeed(facts));
        }
        if (art)
        {
            needs.Add(ArtNeed(facts));
        }
        if (comfort)
        {
            needs.Add(ComfortNeed(facts));
            needs.Add(HostingNeed(facts));
            needs.Add(PartySpotNeed(facts));
            needs.Add(WorkChairsNeed(facts));
        }
        if (gatherings)
        {
            needs.Add(GatheringsNeed(facts, comfort));
        }
        return needs;
    }

    public IReadOnlyList<Need> DeriveSeriousHarm(RoomsFacts facts) => [];

    /// <summary>A ready gatherings Need is held: nothing starts a party until participation is forecast. Every other Method is the runtime's building work or the ledger's bills.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, RoomsFacts facts) =>
        ready.Any(n => n.Id == Gatherings)
            ? [NeedMethod.Held(Gatherings, UnsupportedGathering, "Gathering participation requires shared forecast occupancy and need effects.")]
            : [];

    /// <summary>A plain threshold over the lighting review: unmet while a work cell is dark or a planned lamp unbuilt; an unread census raises <see cref="RequiredFactException"/>.</summary>
    public static Need LightingNeed(RoomsFacts f) =>
        FoodRules.WholeNeed(Lighting, NeedTier.Development, f.Lighting.TryGetValue(out var review) ? !review.Active : throw new RequiredFactException("lighting census"));

    /// <summary>A plain threshold over the flooring review: unmet while any room or cell is owed its floor; an unread census raises <see cref="RequiredFactException"/>.</summary>
    public static Need FlooringNeed(RoomsFacts f) =>
        FoodRules.WholeNeed(Flooring, NeedTier.Development, f.Flooring.TryGetValue(out var review) ? !review.Active : throw new RequiredFactException("flooring census"));

    /// <summary>
    /// Unmet while an artist is inspired, or while a bedroom is owed a sculpture and a colonist can sculpt; met while
    /// no bedroom is owed one or no colonist can sculpt. An unread fact that leaves it undecided raises
    /// <see cref="RequiredFactException"/>.
    /// </summary>
    public static Need ArtNeed(RoomsFacts f)
    {
        var pk = f.Profiles.TryGetValue(out var profiles);
        var ok = f.SculptureRoomsOwed.TryGetValue(out var owed);
        bool met;
        if (pk && ArtRules.InspiredArtists(profiles!).Count > 0)
        {
            met = false;
        }
        else if (ok && !owed)
        {
            met = true;
        }
        else if (ok && pk)
        {
            met = ArtRules.Artists(profiles!).Count == 0;
        }
        else
        {
            throw new RequiredFactException(ok ? "colonist profiles" : "sculpture rooms owed");
        }
        return FoodRules.WholeNeed(Art, NeedTier.Development, met);
    }

    /// <summary>The basic comfort review: unmet while dining, recreation or its variety is short; an unread census or variety catalog raises <see cref="RequiredFactException"/>. The deficit is the review's half per short kind.</summary>
    public static Need ComfortNeed(RoomsFacts f)
    {
        var review = BasicComfortRules.ReviewBasicComfort(f.BasicComfort ?? throw new RequiredFactException("basic comfort census"));
        var recovered = review.Recovered();
        return new Need
        {
            Id = Comfort,
            Tier = NeedTier.Wellbeing,
            Satisfaction = recovered ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = review.Deficit(),
        };
    }

    /// <summary>After comfort: the hosted comfort review, unmet while a hosting room or its proof of use is missing; an unread review raises <see cref="RequiredFactException"/>.</summary>
    public static Need HostingNeed(RoomsFacts f)
    {
        var review = f.Hosting ?? throw new RequiredFactException("hosted comfort");
        return new()
        {
            Id = Hosting,
            Tier = NeedTier.Development,
            Satisfaction = review.Recovered() ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = review.Deficit(),
            Preconditions = [Comfort],
        };
    }

    /// <summary>After hosting: unmet while the PartySpot step is owed; unread rooms, qualities or census raise <see cref="RequiredFactException"/>.</summary>
    public static Need PartySpotNeed(RoomsFacts f) =>
        FoodRules.WholeNeed(PartySpot, NeedTier.Development, !Owed(f.PartySpotOwed, "party spot")) with { Preconditions = [Hosting] };

    /// <summary>After comfort: unmet while a standing bench's chosen work chair is missing; an unread census or plan raises <see cref="RequiredFactException"/>.</summary>
    public static Need WorkChairsNeed(RoomsFacts f) =>
        FoodRules.WholeNeed(WorkChairs, NeedTier.Development, !Owed(f.WorkChairsOwed, "work chairs")) with { Preconditions = [Comfort] };

    /// <summary>
    /// Unmet while the mood census owes a party; an unread census raises <see cref="RequiredFactException"/>. The PartySpot Need is its precondition
    /// when the comfort Needs are composed. The cooldown is the AttendedParty memory the plan reads.
    /// </summary>
    public static Need GatheringsNeed(RoomsFacts f, bool partySpot)
    {
        return FoodRules.WholeNeed(Gatherings, NeedTier.Wellbeing, !Owed(GatheringRules.GatheringOwed(f.Gathering), "gathering mood census")) with
        {
            Preconditions = partySpot ? [PartySpot] : [],
        };
    }

    private static bool Owed(Fact<bool> owed, string fact) => owed.TryGetValue(out var v) ? v : throw new RequiredFactException(fact);

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}
