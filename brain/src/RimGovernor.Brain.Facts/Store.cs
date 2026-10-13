using RimGovernor.Brain.Bridge;
using ClockPb = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Facts;

// The controller-side state store: the decoded colony state a scheduler step planned against, held per
// section with the tick each section describes, so a later step can ask what is held and how old it is
// instead of reconstituting everything from a fresh bundle, and the keyed tables of the colony mirror
// (Tables.cs). It follows FactFamily for invalidation.

/// <summary>
/// Names one decoded state section. Today each maps to a bundle or routine census family; planning_cells is a
/// section of the colony facts with its own as-of so its source can change without touching consumers.
/// </summary>
public enum Section
{
    Colony,
    PlanningCells,
    Population,
    Research,
    Pawns,
    Emergency,
    Rooms,
    Zones,
    Buildings,
    Bills,
    Ideology,
}

public static class SectionExtensions
{
    private static readonly Section[] All =
    [
        Section.Colony, Section.PlanningCells, Section.Population, Section.Research, Section.Pawns, Section.Emergency,
        Section.Rooms, Section.Zones, Section.Buildings, Section.Bills, Section.Ideology,
    ];

    /// <summary>Every section in report order (Go <c>Sections()</c>).</summary>
    public static IReadOnlyList<Section> Sections() => All;

    /// <summary>The fact family whose invalidation the section follows.</summary>
    public static FactFamily Family(this Section section) => section switch
    {
        Section.Colony or Section.PlanningCells or Section.Zones or Section.Buildings or Section.Bills or Section.Ideology => FactFamily.Colony,
        Section.Population or Section.Pawns => FactFamily.Pawns,
        Section.Research => FactFamily.Research,
        Section.Emergency => FactFamily.Emergency,
        Section.Rooms => FactFamily.Rooms,
        _ => throw new InvalidOperationException("unknown section"),
    };

    /// <summary>
    /// Whether the section's rows are cells, so an invalidation narrowed to a rectangle leaves it held when the
    /// rectangle lies outside the region it holds.
    /// </summary>
    public static bool Cells(this Section section) => section == Section.PlanningCells;

    /// <summary>
    /// Reports a section of keyed rows (planning cells, zones, buildings, bills): the sections an invalidation
    /// narrowed to entity ids or cells names (<see cref="Invalidation.Sections"/>).
    /// </summary>
    public static bool Entities(this Section section) =>
        section is Section.PlanningCells or Section.Zones or Section.Buildings or Section.Bills;
}

/// <summary>Inclusive cell bounds. The default Rect is "unknown", which intersects everything.</summary>
public readonly record struct Rect(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public bool Unknown => this == default;

    /// <summary>Whether the two rectangles share a cell; an unknown rectangle on either side counts as intersecting.</summary>
    public bool Intersects(Rect other)
    {
        if (Unknown || other.Unknown)
        {
            return true;
        }
        return MinX <= other.MaxX && other.MinX <= MaxX && MinZ <= other.MaxZ && other.MinZ <= MaxZ;
    }
}

/// <summary>
/// One section's decoded value with its provenance: AsOf is the tick the reply described, Complete whether the
/// value covers the whole section (a partial census is held but marked), Source the native method that
/// produced it and Region the cells a cell section covers (unknown for the rest).
/// </summary>
public sealed record Held<T>
{
    public required T Value { get; init; }

    public long AsOf { get; init; }

    public bool Complete { get; init; }

    public string Source { get; init; } = "";

    public Rect Region { get; init; }
}

/// <summary>The (load, map, native generation) a held section or table belongs to: a change empties the store.</summary>
public readonly record struct Scope(string Load, int Map, ulong Generation);

/// <summary>One held section as the HTTP API reports it.</summary>
public sealed record SectionStatus(Section Section, FactFamily Family, long AsOf, bool Complete, string Source, DateTimeOffset StoredAt);

/// <summary>
/// Holds the sections under one scope. One writer (the scheduler step) puts; the step task and the HTTP API
/// read. A null FactStore holds nothing and accepts every call: the static helpers below take <c>FactStore?</c> for
/// that.
/// </summary>
public sealed class FactStore(TimeProvider? time = null)
{
    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private Scope _scope;
    private Dictionary<Section, Row> _rows = [];

    // Keyed tables by section name (Tables.cs), numbered by _tableVersion and handed to _recorder as published.
    private Dictionary<string, object> _tables = new(StringComparer.Ordinal);
    private ulong _tableVersion;
    private Recorder? _recorder;

    private sealed record Row(object? Value, long AsOf, bool Complete, string Source, DateTimeOffset StoredAt, Rect Region);

    /// <summary>The scope the held rows belong to.</summary>
    public Scope Scope
    {
        get
        {
            lock (_lock)
            {
                return _scope;
            }
        }
    }

    /// <summary>The number of sections held.</summary>
    public int Len
    {
        get
        {
            lock (_lock)
            {
                return _rows.Count;
            }
        }
    }

    /// <summary>Holds a section under scope; a scope other than the rows held empties the store first.</summary>
    public static void Put<T>(FactStore? store, Scope scope, Section section, Held<T> held)
    {
        if (store is null)
        {
            return;
        }
        lock (store._lock)
        {
            store.Rescope(scope);
            store._rows[section] = new Row(held.Value, held.AsOf, held.Complete, held.Source, store._time.GetUtcNow(), held.Region);
        }
    }

    /// <summary>The held section, false when none is held or it was put as another type.</summary>
    public static bool TryGet<T>(FactStore? store, Section section, out Held<T> held)
    {
        held = default!;
        if (store is null)
        {
            return false;
        }
        lock (store._lock)
        {
            if (!store._rows.TryGetValue(section, out var row) || row.Value is not T value)
            {
                return false;
            }
            held = new Held<T> { Value = value, AsOf = row.AsOf, Complete = row.Complete, Source = row.Source, Region = row.Region };
            return true;
        }
    }

    /// <summary>
    /// TryGet for a planner's read: it notes the section's family on the ambient read note whether or not a
    /// section is held, so a read served from the store counts the same as one served by a native call.
    /// </summary>
    public static bool Read<T>(FactStore? store, Section section, out Held<T> held)
    {
        BridgeFunctions.NoteRead(section.Family(), "held:" + Wire.Name(section));
        return TryGet(store, section, out held);
    }

    /// <summary>Drops the named sections.</summary>
    public void Invalidate(params Section[] sections)
    {
        lock (_lock)
        {
            foreach (var section in sections)
            {
                _rows.Remove(section);
            }
        }
    }

    /// <summary>Drops every section of the named families.</summary>
    public void InvalidateFamily(params FactFamily[] families)
    {
        if (families.Length == 0)
        {
            return;
        }
        lock (_lock)
        {
            foreach (var family in families)
            {
                foreach (var section in FamilySections(family))
                {
                    _rows.Remove(section);
                }
            }
        }
    }

    /// <summary>Lists the sections of one family in report order; a family without a section (world, definitions, identity) has none.</summary>
    public static IReadOnlyList<Section> FamilySections(FactFamily family) =>
        [.. SectionExtensions.Sections().Where(section => section.Family() == family)];

    /// <summary>
    /// Takes one invalidation: it drops every section of its families, except a cell section whose region a
    /// narrowing rectangle misses, which is unchanged, value and version both, so proposals planned from it hold.
    /// </summary>
    public void Apply(Invalidation invalidation)
    {
        lock (_lock)
        {
            foreach (var family in invalidation.Families)
            {
                foreach (var section in FamilySections(family))
                {
                    if (_rows.TryGetValue(section, out var row) && section.Cells() && invalidation.Rect is { } rect && !rect.Intersects(row.Region))
                    {
                        continue;
                    }
                    _rows.Remove(section);
                }
            }
        }
    }

    /// <summary>
    /// Drops every section (an event gap or an unattributed mutation says nothing is known to have stayed the
    /// same); the scope is kept.
    /// </summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _rows.Clear();
        }
    }

    /// <summary>Lists the held sections in Sections order.</summary>
    public IReadOnlyList<SectionStatus> Status()
    {
        lock (_lock)
        {
            var output = new List<SectionStatus>(_rows.Count);
            foreach (var section in SectionExtensions.Sections())
            {
                if (_rows.TryGetValue(section, out var row))
                {
                    output.Add(new SectionStatus(section, section.Family(), row.AsOf, row.Complete, row.Source, row.StoredAt));
                }
            }
            return output;
        }
    }

    /// <summary>Makes <paramref name="recorder"/> see every table published from now on (null stops).</summary>
    public void SetRecorder(Recorder? recorder)
    {
        lock (_lock)
        {
            _recorder = recorder;
        }
    }

    /// <summary>Empties the store and moves every version when scope differs from the one its sections belong to. Caller holds <see cref="Gate"/>.</summary>
    internal void Rescope(Scope scope)
    {
        if (scope == _scope)
        {
            return;
        }
        _rows = [];
        _tables = new Dictionary<string, object>(StringComparer.Ordinal);
        _scope = scope;
    }

    // Tables.cs reaches the guarded state through these; callers hold Gate.
    internal object Gate => _lock;

    internal Dictionary<string, object> Tables => _tables;

    internal ulong NextTableVersion() => ++_tableVersion;

    internal Recorder? CurrentRecorder => _recorder;

    internal Scope CurrentScope => _scope;
}

/// <summary>
/// One decoded ObservationInvalidated: the families that changed and, when native could attribute the change,
/// the entity ids and the one rectangle of cells it touched.
/// </summary>
public sealed record Invalidation
{
    public EquatableArray<FactFamily> Families { get; init; }

    public EquatableArray<string> Ids { get; init; }

    public Rect? Rect { get; init; }

    /// <summary>Whether the invalidation names rows rather than whole families.</summary>
    public bool Narrowed => Ids.Count > 0 || Rect is not null;

    /// <summary>
    /// Names the sections the invalidation makes stale, the rows a scheduler routes to the planners declaring
    /// them: every section of each family when nothing narrows it; the entity sections when entity ids do; the
    /// cell section when a rectangle does. A family without a section of the narrowed shape (ids against pawns)
    /// is dirty whole.
    /// </summary>
    public IReadOnlyList<Section> Sections()
    {
        var output = new List<Section>();
        void Add(Section section)
        {
            if (!output.Contains(section))
            {
                output.Add(section);
            }
        }

        foreach (var family in Families)
        {
            if (!Narrowed)
            {
                foreach (var section in FactStore.FamilySections(family))
                {
                    Add(section);
                }
                continue;
            }
            var fitted = false;
            foreach (var section in FactStore.FamilySections(family))
            {
                if ((section.Cells() && Rect is not null) || (!section.Cells() && section.Entities() && Ids.Count > 0))
                {
                    Add(section);
                    fitted = true;
                }
            }
            if (!fitted)
            {
                foreach (var section in FactStore.FamilySections(family))
                {
                    Add(section);
                }
            }
        }
        return output;
    }

    /// <summary>
    /// Decodes an ObservationInvalidated; an unknown family reports false. A rectangle is taken as the decoder
    /// validated it (bridge accepts only present, ordered bounds).
    /// </summary>
    public static bool TryFromWire(ClockPb.ObservationInvalidated wire, out Invalidation invalidation)
    {
        var families = new List<FactFamily>();
        foreach (var family in wire.Families)
        {
            if (!BridgeFunctions.FactFamilyFromWire(family, out var mapped))
            {
                invalidation = new Invalidation();
                return false;
            }
            families.Add(mapped);
        }
        Rect? rect = null;
        if (wire.Cells is { } cells)
        {
            rect = new Rect(cells.Minimum?.X ?? 0, cells.Minimum?.Z ?? 0, cells.Maximum?.X ?? 0, cells.Maximum?.Z ?? 0);
        }
        invalidation = new Invalidation { Families = EquatableArray.From(families), Ids = EquatableArray.From(wire.EntityIds), Rect = rect };
        return true;
    }
}
