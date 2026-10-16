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
    public Fact<ComfortObservation> BasicComfort { get; init; }

    /// <summary>The hosted comfort review: hosting rooms and proof of use, against the review's comfort history.</summary>
    public Fact<bool> HostingRecovered { get; init; }

    /// <summary>The share of hosting facility kinds not yet recovered.</summary>
    public Fact<double> HostingDeficit { get; init; }

    /// <summary>The PartySpot is missing, duplicated or outranked by a better shared room, and the one-shot is not spent.</summary>
    public Fact<bool> PartySpotOwed { get; init; }

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

    public static readonly NeedId Gatherings = NeedId.Of(FamilyId, "gatherings");

    /// <summary>The stable hold subject of an owed party.</summary>
    public const string UnsupportedGathering = "unsupported_forecast_gathering";

    public string Id => FamilyId;

    /// <summary>The room census and its lighting, terrain and comfort, the colony's buildings, the pawns' work and mood, the sky and the emergency census a party waits out.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Rooms, FactFamily.Colony, FactFamily.Pawns, FactFamily.World, FactFamily.Emergency];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    /// <summary>Whether the Need is one of the three comfort steps, which build at the Comfort tier.</summary>
    public static bool ComfortStep(ConcernId concern) =>
        concern == Comfort.FilingKey || concern == Hosting.FilingKey || concern == PartySpot.FilingKey;

    public IReadOnlyList<Need> Derive(RoomsFacts facts)
    {
        var needs = new List<Need>(7);
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
        }
        if (gatherings)
        {
            needs.Add(GatheringsNeed(facts, comfort));
        }
        return needs;
    }

    /// <summary>A ready gatherings Need is held: nothing starts a party until participation is forecast. Every other Method is the runtime's building work or the ledger's bills.</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, RoomsFacts facts) =>
        ready.Any(n => n.Id == Gatherings)
            ? [NeedMethod.Held(Gatherings, UnsupportedGathering, "Gathering participation requires shared forecast occupancy and need effects.")]
            : [];

    /// <summary>A plain threshold over the lighting review: unmet while a work cell is dark or a planned lamp unbuilt, unknown while the census is unread.</summary>
    public static Need LightingNeed(RoomsFacts f)
    {
        var known = f.Lighting.TryGetValue(out var review);
        var satisfaction = !known ? Satisfaction.Unknown : review!.Active ? Satisfaction.Unmet : Satisfaction.Met;
        return new Need
        {
            Id = Lighting,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence =
            [
                new("dark_benches", known ? Fact.Known(Count(review!.Dark.Count)) : Fact.Unknown<string>()),
                new("planned_rooms", known ? Fact.Known(Count(review!.Planned.Count)) : Fact.Unknown<string>()),
            ],
        };
    }

    /// <summary>A plain threshold over the flooring review: unmet while any room or cell is owed its floor, unknown while the census is unread.</summary>
    public static Need FlooringNeed(RoomsFacts f)
    {
        var known = f.Flooring.TryGetValue(out var review);
        var satisfaction = !known ? Satisfaction.Unknown : review!.Active ? Satisfaction.Unmet : Satisfaction.Met;
        return new Need
        {
            Id = Flooring,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence = [new("floor_deficits", known ? Fact.Known(Count(review!.Deficits.Count)) : Fact.Unknown<string>())],
        };
    }

    /// <summary>
    /// Unmet while an artist is inspired, or while a bedroom is owed a sculpture and a colonist can sculpt; met while
    /// no bedroom is owed one or no colonist can sculpt; unknown while either is unread.
    /// </summary>
    public static Need ArtNeed(RoomsFacts f)
    {
        var pk = f.Profiles.TryGetValue(out var profiles);
        var ok = f.SculptureRoomsOwed.TryGetValue(out var owed);
        var satisfaction = Satisfaction.Unknown;
        if (pk && PolicyFunctions.InspiredArtists(profiles!).Count > 0)
        {
            satisfaction = Satisfaction.Unmet;
        }
        else if (ok && !owed)
        {
            satisfaction = Satisfaction.Met;
        }
        else if (ok && pk)
        {
            satisfaction = PolicyFunctions.Artists(profiles!).Count == 0 ? Satisfaction.Met : Satisfaction.Unmet;
        }
        return new Need
        {
            Id = Art,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence =
            [
                new("artists", pk ? Fact.Known(Count(PolicyFunctions.Artists(profiles!).Count)) : Fact.Unknown<string>()),
                new("sculpture_rooms", ok ? Fact.Known(owed ? "owed" : "none") : Fact.Unknown<string>()),
            ],
        };
    }

    /// <summary>The basic comfort review: unmet while dining, recreation or its variety is short; unknown while the census or the variety catalog is unread. The deficit is the review's half per short kind.</summary>
    public static Need ComfortNeed(RoomsFacts f)
    {
        var review = PolicyFunctions.ReviewBasicComfort(f.BasicComfort);
        var recovered = review.Recovered();
        return new Need
        {
            Id = Comfort,
            Tier = NeedTier.Wellbeing,
            Satisfaction = Of(recovered),
            Deficit = review.Deficit(),
            Evidence =
            [
                new("dining", f.BasicComfort.IsKnown ? Fact.Known(review.Dining.ToString()) : Fact.Unknown<string>()),
                new("recreation", f.BasicComfort.IsKnown ? Fact.Known(review.Recreation.ToString()) : Fact.Unknown<string>()),
            ],
        };
    }

    /// <summary>After comfort: the hosted comfort review, unmet while a hosting room or its proof of use is missing, unknown while unread.</summary>
    public static Need HostingNeed(RoomsFacts f) => new()
    {
        Id = Hosting,
        Tier = NeedTier.Development,
        Satisfaction = Of(f.HostingRecovered),
        Deficit = f.HostingDeficit,
        Evidence = [new("hosting", f.HostingRecovered.TryGetValue(out var v) ? Fact.Known(v ? "recovered" : "short") : Fact.Unknown<string>())],
        Preconditions = [Comfort],
    };

    /// <summary>After hosting: unmet while the PartySpot step is owed, unknown while the rooms, qualities or census are unread.</summary>
    public static Need PartySpotNeed(RoomsFacts f)
    {
        var satisfaction = f.PartySpotOwed.TryGetValue(out var owed) ? owed ? Satisfaction.Unmet : Satisfaction.Met : Satisfaction.Unknown;
        return new Need
        {
            Id = PartySpot,
            Tier = NeedTier.Development,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence = [new("party_spot", f.PartySpotOwed.TryGetValue(out var v) ? Fact.Known(v ? "owed" : "standing") : Fact.Unknown<string>())],
            Preconditions = [Hosting],
        };
    }

    /// <summary>
    /// Unmet while the mood census owes a party, unknown while it is unread. The PartySpot Need is its precondition
    /// when the comfort Needs are composed. The cooldown is the AttendedParty memory the plan reads.
    /// </summary>
    public static Need GatheringsNeed(RoomsFacts f, bool partySpot)
    {
        var owed = PolicyFunctions.GatheringOwed(f.Gathering);
        var satisfaction = owed.TryGetValue(out var v) ? v ? Satisfaction.Unmet : Satisfaction.Met : Satisfaction.Unknown;
        return new Need
        {
            Id = Gatherings,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = Whole(satisfaction),
            Evidence = [new("organizer", f.Gathering.TryGetValue(out var plan) ? Fact.Known(plan.Organizer.IsEmpty ? "none" : plan.Organizer.ToString()) : Fact.Unknown<string>())],
            Preconditions = partySpot ? [PartySpot] : [],
        };
    }

    private static Satisfaction Of(Fact<bool> recovered) =>
        recovered.TryGetValue(out var v) ? v ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;

    private static Fact<double> Whole(Satisfaction satisfaction) => satisfaction switch
    {
        Satisfaction.Unknown => Fact.Unknown<double>(),
        Satisfaction.Unmet => Fact.Known(1.0),
        _ => Fact.Known(0.0),
    };

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}
