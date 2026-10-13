namespace RimGovernor.Brain.Policy;

/// <summary>A ledger kill: the corpse's thing id (the food census's stock id), the meat the corpse would yield and the tick of the kill.</summary>
public sealed record HuntKill(string CorpseID, double Potential, long Tick);

/// <summary>A ledger butcher record: the meat a corpse's butchering made.</summary>
public sealed record HuntButcher(string CorpseID, double MeatNutrition);

public static class DeliveryCreditHuntExtensions
{
    /// <summary>
    /// The hunt group's credited nutrition, in the ledger's cumulative form, from the kills and butchers of one
    /// load token and the corpse census. A corpse is credited once, by its id, in two stages. Kill: while it lies
    /// unbutchered in the census (field or larder; hauling never counts) it is worth its potential yield times its
    /// rot clock, the rot ticks left over the most it was seen with. A corpse with no rot deadline (frozen, or a
    /// non-perishable) does not decay. A corpse the census no longer lists is worth nothing; with an incomplete
    /// census it is held at its yield rather than read as gone. Butcher: the meat its butchering made replaces the
    /// corpse's credit. Humanlike corpses are the human-butchery channel's, never a kill's. The ledger holds the
    /// newest records only, so a kill that fell off it stops counting. A null credit credits nothing.
    /// </summary>
    public static double HuntDelivered(
        this DeliveryCredit? d,
        IReadOnlyList<HuntKill> kills,
        IReadOnlyList<HuntButcher> butchers,
        IReadOnlyList<FoodStock> stocks,
        bool complete
    )
    {
        if (d is null)
        {
            return 0;
        }
        var made = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var b in butchers)
        {
            made[b.CorpseID] = made.GetValueOrDefault(b.CorpseID) + b.MeatNutrition;
        }
        var corpses = new Dictionary<string, FoodStock>(StringComparer.Ordinal);
        foreach (var s in stocks)
        {
            if (s.Corpse)
            {
                corpses[s.Id] = s;
            }
        }
        var peak = new Dictionary<string, long>(StringComparer.Ordinal);
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var total = 0.0;
        foreach (var k in kills)
        {
            if (counted.Contains(k.CorpseID) || !PolicyFunctions.FoodNumber(k.Potential))
            {
                continue;
            }
            counted.Add(k.CorpseID);
            var listed = corpses.TryGetValue(k.CorpseID, out var corpse);
            if (listed && corpse!.IsHumanlike)
            {
                continue;
            }
            var credit = k.Potential;
            if (made.TryGetValue(k.CorpseID, out var meat))
            {
                credit = meat;
            }
            else if (listed)
            {
                credit *= RotFraction(d, corpse!, peak);
            }
            else if (complete)
            {
                credit = 0;
            }
            total += Math.Max(0, credit);
        }
        d.RotPeak = peak;
        return total;
    }

    /// <summary>The share of a corpse's rot clock left: its rot ticks over the most seen for it, carried in peak into the next call.</summary>
    private static double RotFraction(DeliveryCredit d, FoodStock s, Dictionary<string, long> peak)
    {
        var pk = s.Perishable.TryGetValue(out var perishable);
        var tk = s.RotTicks.TryGetValue(out var ticks);
        if (!pk || !perishable || !tk)
        {
            return 1;
        }
        var most = Math.Max(ticks, d.RotPeak.GetValueOrDefault(s.Id));
        peak[s.Id] = most;
        if (ticks <= 0 || most <= 0)
        {
            return 0;
        }
        return (double)ticks / most;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The one counter group of hunting: every hunt channel shares it, and the ledger's kills and butchers are its deliveries.</summary>
    public const string HuntSource = "hunt";

    private static readonly double TicksPerDayValue = GameTime.TicksPerDay;

    /// <summary>The trailing window hunting is judged over, long enough that a kill every few days averages out.</summary>
    public const double HuntCreditWindowDays = 6.0;

    /// <summary>The planning kill rate of one hunter until the ledger shows a cadence: a hunt's walk, kill and haul take most of a day.</summary>
    public const double HuntKillsPerHunterDay = 1.0;

    /// <summary>Bounds the trailing kills the cadence reads.</summary>
    public const double HuntCadenceDays = 14.0;

    /// <summary>The history the cadence needs before it replaces the default.</summary>
    public const int HuntCadenceMinKills = 3;

    /// <summary>
    /// The kills per hunter-day the ledger shows: the kills of the trailing HuntCadenceDays over the hunters and
    /// the days that history spans. Without HuntCadenceMinKills of them it is HuntKillsPerHunterDay.
    /// </summary>
    public static double HuntCadence(IReadOnlyList<HuntKill> kills, int hunters, long now)
    {
        if (hunters <= 0)
        {
            return HuntKillsPerHunterDay;
        }
        var cutoff = now - (long)(HuntCadenceDays * TicksPerDayValue);
        var n = 0;
        var oldest = now;
        foreach (var k in kills)
        {
            if (k.Tick >= cutoff)
            {
                n++;
                oldest = Math.Min(oldest, k.Tick);
            }
        }
        if (n < HuntCadenceMinKills)
        {
            return HuntKillsPerHunterDay;
        }
        var span = Math.Min(HuntCadenceDays, Math.Max(1, (now - oldest) / TicksPerDayValue));
        return n / (hunters * span);
    }

    /// <summary>
    /// Caps the rate of the hunt channels at what the hunters kill: hunters x kills per hunter-day x the meat of
    /// an average prey. Each channel's nutrition, work and products per day scale together by the share the cap
    /// leaves, so a channel the plan opens keeps its labor per nutrition. The channels' StockCap (the animals in
    /// reach, huntChannel) bounds them again in the ranker as a finite source. Without hunters, or for a channel
    /// that waits on a hunter's weapon, nothing scales.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> HuntThroughput(IReadOnlyList<SupplyCandidate> channels, int hunters, double killsPerHunterDay)
    {
        if (hunters <= 0 || killsPerHunterDay <= 0)
        {
            return channels;
        }
        var rate = 0.0;
        var prey = 0.0;
        foreach (var c in channels)
        {
            if (HuntScales(c))
            {
                c.Nutrition().PerDay.TryGetValue(out var n);
                rate += n;
                prey += HuntPreyCount(c);
            }
        }
        if (rate <= 0 || prey <= 0)
        {
            return channels;
        }
        var limit = hunters * killsPerHunterDay * rate * FoodHuntCycleDays / prey;
        if (limit >= rate)
        {
            return channels;
        }
        var share = limit / rate;
        var @out = new List<SupplyCandidate>(channels.Count);
        foreach (var c in channels)
        {
            if (!HuntScales(c))
            {
                @out.Add(c);
                continue;
            }
            var w = c.LaborPerDay.ValueOrDefault;
            var yields = c.Yields.Select(y => y.PerDay.TryGetValue(out var pd) ? y with { PerDay = Known(pd * share) } : y);
            @out.Add(
                c with
                {
                    Yields = EquatableArray.From(yields),
                    LaborPerDay = Known(w * share),
                    Terms = EquatableArray.From(c.Terms.Append(new CandidateTerm("hunt_throughput_share", share))),
                }
            );
        }
        return @out;
    }

    /// <summary>Whether a hunt channel counts toward the hunters' throughput.</summary>
    private static bool HuntScales(SupplyCandidate c)
    {
        if (c.Kind != CandidateKind.Hunt)
        {
            return false;
        }
        if (!c.Nutrition().PerDay.TryGetValue(out var n) || n <= 0)
        {
            return false;
        }
        foreach (var t in c.Terms)
        {
            if (t.Name == TermNeedsWeapon)
            {
                return false;
            }
        }
        return true;
    }

    private static int HuntPreyCount(SupplyCandidate c)
    {
        foreach (var t in c.Terms)
        {
            if (t.Name == "prey")
            {
                return (int)t.Value;
            }
        }
        return 1;
    }
}
