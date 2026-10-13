namespace RimGovernor.Brain.Policy;

// Realized consumption. Native counts what the colony's stock was spent on, per (definition, reason), into a
// saved hourly ring; the brain holds the hours it was sent and reads a rate over any window as a subtraction.

/// <summary>One (resource, reason) spend; Count is signed (an ejected refuel or a removed shell subtracts).</summary>
public sealed record ConsumptionRow
{
    public Resource Resource { get; init; }

    public string Reason { get; init; } = "";

    public long Count { get; init; }
}

/// <summary>One completed ring hour, sparse.</summary>
public sealed record ConsumptionHour
{
    public int Hour { get; init; }

    public EquatableArray<ConsumptionRow> Rows { get; init; }
}

/// <summary>
/// One native read: the hours after the asked-for hour. CurrentHour is still filling and never listed;
/// FirstHour is the first hour the ring covers.
/// </summary>
public sealed record ConsumptionPage
{
    public int CurrentHour { get; init; }

    public int FirstHour { get; init; }

    public EquatableArray<ConsumptionHour> Hours { get; init; }
}

/// <summary>The recurring spend over the span the ledger covers inside a rate window.</summary>
public sealed record ResourceConsumption
{
    public double WindowDays { get; init; }

    public IReadOnlyDictionary<Resource, long> Recurring { get; init; } = new Dictionary<Resource, long>();
}

/// <summary>The in-memory copy of the ring. A new ledger holds nothing and asks for the whole window.</summary>
public sealed class ConsumptionLedger
{
    private bool _have;
    private int _through;
    private int _first;
    private Dictionary<int, EquatableArray<ConsumptionRow>>? _hours;

    /// <summary>The hour to pass native: the last completed hour held, or -1 for the whole window.</summary>
    public int Since() => _have ? _through : -1;

    /// <summary>Folds one page in. Every hour up to CurrentHour-1 is now held, listed or not.</summary>
    public void Merge(ConsumptionPage p)
    {
        _hours ??= [];
        foreach (var h in p.Hours)
        {
            _hours[h.Hour] = h.Rows;
        }
        _have = true;
        _through = p.CurrentHour - 1;
        _first = p.FirstHour;
        foreach (var hour in _hours.Keys.ToList())
        {
            if (hour <= _through - ConsumptionWindowHours)
            {
                _hours.Remove(hour);
            }
        }
    }

    /// <summary>The recurring spend over the last days days the ring covers.</summary>
    public Fact<ResourceConsumption> Window(int days)
    {
        if (!_have)
        {
            return Unknown<ResourceConsumption>();
        }
        var start = Math.Max(_first, _through + 1 - (days * 24));
        var recurring = new Dictionary<Resource, long>();
        var windowDays = 0.0;
        if (_through + 1 > start)
        {
            windowDays = (double)(_through + 1 - start) / 24;
        }
        for (var hour = start; hour <= _through; hour++)
        {
            if (_hours is null || !_hours.TryGetValue(hour, out var rows))
            {
                continue;
            }
            foreach (var row in rows)
            {
                if (PolicyFunctions.RecurringReasons.Contains(row.Reason))
                {
                    recurring[row.Resource] = recurring.GetValueOrDefault(row.Resource) + row.Count;
                }
            }
        }
        foreach (var (resource, n) in recurring.ToList())
        {
            recurring[resource] = Math.Max(0, n);
        }
        return Known(new ResourceConsumption { WindowDays = windowDays, Recurring = recurring });
    }
}

public static partial class PolicyFunctions
{
    /// <summary>One ring hour.</summary>
    public const int ConsumptionHourTicks = 2500;

    /// <summary>The ring's depth: 60 days.</summary>
    public const int ConsumptionWindowHours = 60 * 24;

    /// <summary>The window a maintenance rate is read over.</summary>
    public const int ResourceRateWindowDays = 15;

    /// <summary>
    /// The reasons whose spend repeats without a decision: only these feed a resource runway. Construction is
    /// a project (it stays on ConstructionDemand); the rest are losses.
    /// </summary>
    internal static readonly HashSet<string> RecurringReasons = new(StringComparer.Ordinal)
    {
        "bill_ingredient", "medicine_tend", "food_eaten", "drug_dose", "animal_feed",
        "nutrient_paste", "fuel_loaded", "shell_loaded", "apparel_wear",
    };

    /// <summary>Whether native's reason is one this build names.</summary>
    public static bool KnownConsumptionReason(string reason)
    {
        if (RecurringReasons.Contains(reason))
        {
            return true;
        }
        return reason is "construction" or "rot" or "deterioration" or "fire" or "sold" or "stolen" or "destroyed_other";
    }
}
