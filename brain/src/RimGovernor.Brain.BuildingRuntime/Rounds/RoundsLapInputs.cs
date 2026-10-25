using System.Reflection;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What each lap of the rounds review entered on, compared with the previous review's (#3564). The reading a lap
/// enters on is the one the lap before it left, so a lap whose entering reading matches the previous review's
/// re-derives from the same inputs (apart from what it reads from the game or the journal itself), and is the lap
/// a continuation could skip. The comparison is by the projection's stored properties and the facts' that have
/// value equality (records, value types, strings, Facts of those; <see cref="Uncompared"/> names the rest, which a
/// review rebuilds and which would read as changed whether or not anything did), named on the rounds_review row as
/// <c>laps_changed</c>: lap to the properties that differ, in the order <see cref="Laps"/> gives; <c>laps_same</c>
/// names the laps that entered on an identical reading. The properties are reflected once: a hand-written digest
/// would list three hundred of them and could not name what drifted.
/// </summary>
internal sealed class RoundsLapInputs
{
    /// <summary>The review's laps in order; each enters on the reading the one before it left.</summary>
    internal static readonly string[] Laps = ["observe", "layout", "d_owed", "d_reads", "d_stock", "d_planned", "d_file", "d_work", "d_clear", "derive", "review"];

    /// <summary>At most this many property names per lap; the rest is a count.</summary>
    internal const int NamesCap = 24;

    private static readonly PropertyInfo[] ProjectionProperties = Stored(typeof(ColonyProjection), except: nameof(ColonyProjection.Facts));
    private static readonly PropertyInfo[] FactsProperties = Stored(typeof(RoundsFacts), except: null);

    private readonly Dictionary<string, ColonyProjection> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColonyProjection> _current = new(StringComparer.Ordinal);
    private readonly List<string> _same = [];
    private GenerationSnapshot _world;
    private bool _compared;

    /// <summary>Begins a review over <paramref name="world"/>: the last review's readings become the previous ones, or are dropped for another world.</summary>
    public void Begin(GenerationSnapshot world)
    {
        _previous.Clear();
        if (world == _world)
        {
            foreach (var (lap, projection) in _current)
            {
                _previous[lap] = projection;
            }
        }
        _current.Clear();
        _world = world;
        _compared = false;
    }

    /// <summary>Notes the reading <paramref name="lap"/> left (what the next lap enters on).</summary>
    public void Left(string lap, ColonyProjection projection) => _current[lap] = projection;

    /// <summary>Whether a previous review's readings exist to compare with.</summary>
    public bool HasPrevious => _previous.Count > 0;

    /// <summary>The wall time the last <see cref="Changed"/> took.</summary>
    public double CompareMs { get; private set; }

    /// <summary>
    /// Per lap after the first, the properties of the reading it entered on that differ from the previous review's,
    /// as "A,B,C" (capped at <see cref="NamesCap"/>, then "+n"); a lap that entered on an identical reading is absent.
    /// Null without a previous review or when no lap left a reading.
    /// </summary>
    public Dictionary<string, object?>? Changed()
    {
        if (!HasPrevious)
        {
            return null;
        }
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var changed = new Dictionary<string, object?>(StringComparer.Ordinal);
        _same.Clear();
        for (var i = 1; i < Laps.Length; i++)
        {
            var entering = Laps[i - 1];
            if (!_current.TryGetValue(entering, out var now) || !_previous.TryGetValue(entering, out var was))
            {
                continue;
            }
            var names = Differences(was, now);
            if (names.Count == 0)
            {
                _same.Add(Laps[i]);
                continue;
            }
            changed[Laps[i]] = names.Count <= NamesCap
                ? string.Join(",", names)
                : string.Join(",", names.Take(NamesCap)) + ",+" + (names.Count - NamesCap).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        _compared = true;
        CompareMs = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        return changed;
    }

    /// <summary>The laps that entered on an identical reading, after <see cref="Changed"/>; empty before it.</summary>
    public string[] Same => _compared ? [.. _same] : [];

    /// <summary>The properties of <paramref name="now"/> that differ from <paramref name="was"/>: the projection's by name, the facts' as "Facts.Name".</summary>
    internal static List<string> Differences(ColonyProjection was, ColonyProjection now)
    {
        var names = new List<string>();
        if (ReferenceEquals(was, now))
        {
            return names;
        }
        foreach (var property in ProjectionProperties)
        {
            if (!Equals(property.GetValue(was), property.GetValue(now)))
            {
                names.Add(property.Name);
            }
        }
        if (!ReferenceEquals(was.Facts, now.Facts))
        {
            foreach (var property in FactsProperties)
            {
                if (!Equals(property.GetValue(was.Facts), property.GetValue(now.Facts)))
                {
                    names.Add("Facts." + property.Name);
                }
            }
        }
        return names;
    }

    /// <summary>
    /// The stored properties the comparison leaves out, as the projection's names and "Facts.Name": their type (or
    /// the one inside their Fact) has no value equality, so a review that rebuilds them would read as changed whether
    /// or not anything did.
    /// </summary>
    internal static readonly string[] Uncompared =
        [.. Unequatable(typeof(ColonyProjection), nameof(ColonyProjection.Facts)), .. Unequatable(typeof(RoundsFacts), null).Select(n => "Facts." + n)];

    /// <summary>The type's public stored properties (an init or set accessor; computed getters are derived from the stored ones) with value equality, in declaration order.</summary>
    private static PropertyInfo[] Stored(Type type, string? except) =>
        [.. StoredAll(type, except).Where(p => Compared(type, p))];

    private static IEnumerable<string> Unequatable(Type type, string? except) =>
        StoredAll(type, except).Where(p => !Compared(type, p)).Select(p => p.Name);

    /// <summary>
    /// Whether the property compares by value: its type does (<see cref="HasValueEquality"/>) and two default
    /// instances of the record agree on it (a record whose members include a plain dictionary or list overrides
    /// Equals and still compares those by reference, which the probe shows).
    /// </summary>
    private static bool Compared(Type record, PropertyInfo property)
    {
        if (!HasValueEquality(property.PropertyType))
        {
            return false;
        }
        var a = ProbeRecord(record);
        var b = ProbeRecord(record);
        return Equals(property.GetValue(a), property.GetValue(b));
    }

    // A diagnostic equality probe must compose required getters before reading them. Each probe owns its
    // dictionary so construction's reference-comparing material table is correctly left out of the digest.
    private static object? ProbeRecord(Type record) => record == typeof(RoundsFacts)
        ? new RoundsFacts
        {
            CurrentConstruction = CurrentConstruction.Empty with { Deficit = new Dictionary<Resource, long>() },
            // These are equality probes, never planner inputs. Separate empty lists expose Gear's
            // reference-comparing collections just as separate dictionaries do for construction.
            Gear = new GearObservation
            {
                Pawns = new List<GearPawn>(),
                Climate = new O.GearClimate { Temperatures = [], DaylightHours = [], CurrentTwelfth = 0, TicksToNextTwelfth = 0 },
                Threat = new RaidThreat.Uncomposed(),
            },
        }
        : Activator.CreateInstance(record);

    private static IEnumerable<PropertyInfo> StoredAll(Type type, string? except) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.Name != except)
            .OrderBy(p => p.MetadataToken);

    /// <summary>Whether values of the type compare by value: a value type or string, or a class that overrides Equals (a record); a Fact's by what it holds.</summary>
    internal static bool HasValueEquality(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Fact<>))
        {
            type = type.GetGenericArguments()[0];
        }
        if (type.IsValueType || type == typeof(string))
        {
            return true;
        }
        var equals = type.GetMethod(nameof(Equals), BindingFlags.Public | BindingFlags.Instance, [typeof(object)]);
        return equals is not null && equals.DeclaringType != typeof(object);
    }
}
