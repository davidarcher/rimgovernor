namespace RimGovernor.Brain.Policy;


/// <summary>
/// The foothold half of the comfort need: every colonist can reach a seat at an indoor eating surface and a
/// recreation source, wherever they stand. "Ate without table" and "no recreation" are the cheapest mood
/// debuffs to remove, so they are provided with the starter hut rather than after the whole startup ladder;
/// the hosting room's native role and proof of use stay the rooms/hosting Need.
/// </summary>
public sealed record BasicComfortReview
{
    public ComfortNeed Dining { get; init; }

    public ComfortNeed Recreation { get; init; }

    public int MissingVariety { get; init; }

    public Fact<bool> Recovered()
    {
        if (Dining == ComfortNeed.Unknown || Recreation == ComfortNeed.Unknown)
        {
            return Unknown<bool>();
        }

        return Known(Dining == ComfortNeed.Recovered && Recreation == ComfortNeed.Recovered && MissingVariety == 0);
    }

    public Fact<double> Deficit()
    {
        if (Dining == ComfortNeed.Unknown || Recreation == ComfortNeed.Unknown)
        {
            return Unknown<double>();
        }

        var value = 0.0;
        if (Dining != ComfortNeed.Recovered)
        {
            value += .5;
        }

        if (Recreation != ComfortNeed.Recovered || MissingVariety > 0)
        {
            value += .5;
        }

        return Known(value);
    }

    /// <summary>The dining and recreation capacity stand; only variety is left.</summary>
    public bool CoreRecovered() => Dining == ComfortNeed.Recovered && Recreation == ComfortNeed.Recovered;
}

public static class BasicComfortRules
{
    /// <summary>
    /// Measures the unfiltered comfort census (every indoor seat and every recreation source, whatever room
    /// hosts it). Capacity alone counts: a facility everyone can reach is provided whether or not anyone has
    /// been observed using it yet.
    /// </summary>
    public static BasicComfortReview ReviewBasicComfort(Fact<ComfortObservation> observed)
    {
        var full = ComfortRules.ReviewComfort(observed, new ComfortHistory(), new Tick(0));
        static ComfortNeed Provided(ComfortNeed need) => need == ComfortNeed.UseNeeded ? ComfortNeed.Recovered : need;

        var r = new BasicComfortReview
        {
            Dining = Provided(full.Dining),
            Recreation = Provided(full.Recreation),
        };
        if (observed.TryGetValue(out var v))
        {
            r = r with { MissingVariety = RecreationRules.RecreationVarietyMissing(v) };
        }

        return r;
    }

    /// <summary>
    /// Names the one facility the foothold goal builds next: a table where no indoor eating surface stands,
    /// then a seat beside it, then a recreation source. Existing furniture some colonist cannot reach is
    /// preserved as a blocker, as the rooms/comfort Need does, rather than duplicated.
    /// </summary>
    public static ComfortMethod SelectBasicComfortMethod(ComfortObservation v, BasicComfortReview r)
    {
        DomainException.Require(r.Dining != ComfortNeed.Unknown && r.Recreation != ComfortNeed.Unknown, "comfort evidence unavailable");
        if (r.Dining == ComfortNeed.Capacity)
        {
            if (v.Census.Dining.Count > 0)
            {
                return ComfortMethod.AccessBlocked;
            }

            return v.Census.Surfaces.Count == 0 ? v.Furniture.TableMethod() : v.Furniture.ChairMethod();
        }

        if (r.Recreation == ComfortNeed.Capacity)
        {
            return v.Census.Recreation.Count > 0 ? ComfortMethod.AccessBlocked : v.FootholdMethod();
        }

        return ComfortMethod.NoMethod;
    }
}
