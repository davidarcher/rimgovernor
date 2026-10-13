using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Observation;

// The construction census is derived from the building table: a frame-to-frame state that applies only the rows
// that changed between two table versions (Table.Changed), instead of projecting every building each step. Its
// outputs are rebuilt only when a projection changed (a hit-point change alters none), else the last ones stand.

/// <summary>The census derived from a building table.</summary>
internal sealed class ConstructionState
{
    private Buildings _rows;
    private readonly Dictionary<string, BuiltEntry> _built = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SiteEntry> _sites = new(StringComparer.Ordinal);

    // The standing sites' needs summed; BadNeeds are the sites whose need is malformed (the deficit is then unknown).
    private readonly Dictionary<Resource, long> _deficit = [];
    private readonly HashSet<string> _badNeeds = new(StringComparer.Ordinal);

    // Dirty marks a changed built or site projection since the outputs.
    private bool _dirty = true;

    // Outputs.
    public Fact<CurrentConstruction> Census { get; private set; }

    public DomainException? Error { get; private set; }

    public Fact<IReadOnlyDictionary<Resource, long>> DeficitFact { get; private set; }

    /// <summary>
    /// Applies <paramref name="rows"/> to the state derived from the previous version (null derives from scratch)
    /// and returns the state at rows. The outputs are fresh values: a state is only ever mutated inside the
    /// memo's lock.
    /// </summary>
    public static ConstructionState Next(ConstructionState? state, Buildings rows)
    {
        if (state is null)
        {
            state = new ConstructionState();
            foreach (var (id, row) in rows.Table.All())
            {
                state.Set(id, row);
            }
        }
        else
        {
            var prev = state._rows;
            rows.Table.Changed(
                prev.Table,
                (id, row) =>
                {
                    if (prev.Table.TryGetValue(id, out var old))
                    {
                        state.DropNeeds(id, old);
                    }
                    state.Set(id, row);
                },
                id =>
                {
                    if (prev.Table.TryGetValue(id, out var old))
                    {
                        state.DropNeeds(id, old);
                    }
                    state.Unset(id);
                });
        }
        state._rows = rows;
        if (state._dirty)
        {
            state.Rebuild();
        }
        state.RebuildDeficit();
        return state;
    }

    // Projects row into the state, marking the lists dirty only when the row's projection differs from the one held.
    private void Set(string id, O.BuildingState row)
    {
        switch (row.Status.GetValueOrDefault())
        {
            case O.BuildingStatus.Built:
            {
                var next = BuiltEntryOf(row);
                if (!_built.TryGetValue(id, out var old) || old != next)
                {
                    _dirty = true;
                }
                _built[id] = next;
                _sites.Remove(id);
                break;
            }
            case O.BuildingStatus.Blueprint:
            case O.BuildingStatus.Frame:
            {
                if (_built.Remove(id))
                {
                    _dirty = true;
                }
                var (site, ok, error) = ObservationPackage.ConstructionSiteOf(row);
                if (ok || error is not null)
                {
                    var next = new SiteEntry(site, error);
                    if (!_sites.TryGetValue(id, out var old) || old != next)
                    {
                        _dirty = true;
                    }
                    _sites[id] = next;
                }
                else
                {
                    UnsetSite(id);
                }
                var (needs, valid) = ObservationPackage.SiteNeeds(row);
                if (!valid)
                {
                    _badNeeds.Add(id);
                }
                foreach (var n in needs)
                {
                    _deficit[n.Resource] = _deficit.GetValueOrDefault(n.Resource) + n.Count;
                }
                break;
            }
            default:
                Unset(id);
                break;
        }
    }

    private static BuiltEntry BuiltEntryOf(O.BuildingState row)
    {
        try
        {
            var (b, known) = ObservationPackage.CurrentBuildingOf(row);
            return new BuiltEntry(b, known, null);
        }
        catch (DomainException e)
        {
            return new BuiltEntry(new CurrentBuilding(), false, e);
        }
    }

    // Forgets id's projections.
    private void Unset(string id)
    {
        if (_built.Remove(id))
        {
            _dirty = true;
        }
        UnsetSite(id);
    }

    private void UnsetSite(string id)
    {
        if (_sites.Remove(id))
        {
            _dirty = true;
        }
    }

    // Removes a replaced or removed row's share of the deficit.
    private void DropNeeds(string id, O.BuildingState row)
    {
        _badNeeds.Remove(id);
        var (needs, _) = ObservationPackage.SiteNeeds(row);
        foreach (var n in needs)
        {
            var left = _deficit.GetValueOrDefault(n.Resource) - n.Count;
            if (left == 0)
            {
                _deficit.Remove(n.Resource);
            }
            else
            {
                _deficit[n.Resource] = left;
            }
        }
    }

    // Recomputes the outputs from the projections, in id order.
    private void Rebuild()
    {
        _dirty = false;
        Error = null;
        Census = Unknown<CurrentConstruction>();
        var buildings = new List<CurrentBuilding>();
        var unknown = false;
        foreach (var id in _built.Keys.Order(StringComparer.Ordinal))
        {
            var e = _built[id];
            if (e.Error is not null)
            {
                Error = e.Error;
                return;
            }
            if (!e.Known)
            {
                unknown = true;
            }
            else
            {
                buildings.Add(e.Building);
            }
        }
        if (unknown)
        {
            return;
        }
        var sites = new List<ConstructionSite>();
        foreach (var id in _sites.Keys.Order(StringComparer.Ordinal))
        {
            var e = _sites[id];
            if (e.Error is not null)
            {
                Error = e.Error;
                Census = Unknown<CurrentConstruction>();
                return;
            }
            sites.Add(e.Site);
        }
        Census = Known(new CurrentConstruction
        {
            Colony = true,
            Requested = EquatableArray.From(new List<string>()),
            Buildings = EquatableArray.From(buildings),
            Sites = EquatableArray.From(sites),
        });
    }

    private void RebuildDeficit() =>
        DeficitFact = _badNeeds.Count == 0
            ? Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>(_deficit))
            : Unknown<IReadOnlyDictionary<Resource, long>>();

    private sealed record BuiltEntry(CurrentBuilding Building, bool Known, DomainException? Error);

    private sealed record SiteEntry(ConstructionSite Site, DomainException? Error);
}

internal readonly record struct SiteNeed(Resource Resource, long Count);

public static partial class ObservationPackage
{
    /// <summary>Maps a native cardinal rotation onto the domain's; any other value is refused by the building constructor.</summary>
    internal static Rotation RotationOf(Placement.Rotation r) => r switch
    {
        Placement.Rotation.North => Rotation.North,
        Placement.Rotation.East => Rotation.East,
        Placement.Rotation.South => Rotation.South,
        Placement.Rotation.West => Rotation.West,
        _ => (Rotation)(-1),
    };

    private static string? SiteStage(O.BuildingStatus status) => status switch
    {
        O.BuildingStatus.Blueprint => "blueprint",
        O.BuildingStatus.Frame => "frame",
        _ => null,
    };

    /// <summary>Projects one built row for the construction census; known is false for a row whose stuff the native could not say.</summary>
    internal static (CurrentBuilding Building, bool Known) CurrentBuildingOf(O.BuildingState row)
    {
        var stuff = (row.Stuff ?? "");
        if (row.Stuff is null)
        {
            var notApplicable = false;
            foreach (var issue in row.Issues)
            {
                notApplicable = notApplicable || ((issue.Field ?? "") == "stuff" && (issue.Unavailable?.Reason).GetValueOrDefault() == C.UnavailableReason.NotApplicable);
            }
            if (!notApplicable)
            {
                return (new CurrentBuilding(), false);
            }
        }
        var building = new Building(row.Building?.DefName ?? "", CellOf(row.Building?.Position), RotationOf(row.Rotation.GetValueOrDefault()), stuff);
        var cells = RectCells(row.Occupied);
        if (row.Occupied is not null && cells is null)
        {
            throw new ObservationContractException("invalid occupied rectangle");
        }
        return (new CurrentBuilding { Id = row.Building?.Id ?? "", Building = building, Cells = EquatableArray.From(cells ?? []) }, true);
    }

    /// <summary>Projects one blueprint or frame row; ok is false for any other row.</summary>
    internal static (ConstructionSite Site, bool Ok, DomainException? Error) ConstructionSiteOf(O.BuildingState row)
    {
        var site = new ConstructionSite();
        try
        {
            var stage = SiteStage(row.Status.GetValueOrDefault());
            if (stage is null || (row.BuildDefName ?? "") == "")
            {
                return (site, false, null);
            }
            var b = new Building((row.BuildDefName ?? ""), CellOf(row.Building?.Position), RotationOf(row.Rotation.GetValueOrDefault()), (row.Stuff ?? ""));
            var state = row.Construction;
            site = new ConstructionSite { Building = b, Stage = stage, Id = row.Building?.Id ?? "" };
            if (state is not null)
            {
                if (state.MinimumFinishingSkill.GetValueOrDefault() < 0 || state.MinimumFinishingSkill.GetValueOrDefault() > 1000 || state.NativeFinishingSkill.GetValueOrDefault() < 0 || state.EligibleFinishers.GetValueOrDefault() < 0)
                {
                    throw new ObservationContractException("construction state out of range");
                }
                site = site with
                {
                    ResourcesComplete = Optional(state.ResourcesComplete is not null, state.ResourcesComplete.GetValueOrDefault()),
                    QualitySensitive = Optional(state.QualitySensitive is not null, state.QualitySensitive.GetValueOrDefault()),
                };
                if (state.MinimumFinishingSkill is not null)
                {
                    site = site with { MinimumFinishingSkill = Known(state.MinimumFinishingSkill.GetValueOrDefault()) };
                }
                if (state.NativeFinishingSkill is not null)
                {
                    site = site with { NativeFinishingSkill = Known(state.NativeFinishingSkill.GetValueOrDefault()) };
                }
                if (state.EligibleFinishers is not null)
                {
                    site = site with { EligibleFinishers = Known(state.EligibleFinishers.GetValueOrDefault()) };
                }
                if (state.Tier is not null)
                {
                    var tier = (ConstructionTier)state.Tier.GetValueOrDefault();
                    if (!Enum.IsDefined(tier))
                    {
                        throw new ObservationContractException("invalid construction tier");
                    }
                    site = site with { Tier = Known(tier) };
                }
                site = site with { FinishingBlocker = state.FinishingBlocker ?? "" };
            }
            return (site, true, null);
        }
        catch (DomainException e)
        {
            return (site, false, e);
        }
    }

    /// <summary>What one blueprint or frame row is still owed, nothing for any other row; valid is false when a need is malformed.</summary>
    internal static (List<SiteNeed> Needs, bool Valid) SiteNeeds(O.BuildingState row)
    {
        var needs = new List<SiteNeed>();
        if (row.Status is not (O.BuildingStatus.Blueprint or O.BuildingStatus.Frame))
        {
            return (needs, true);
        }
        foreach (var need in row.Construction?.Resources ?? [])
        {
            if (need is null || (need.DefName ?? "") == "" || need.StillNeeded is null || need.StillNeeded.GetValueOrDefault() < 0)
            {
                return ([], false);
            }
            if (need.StillNeeded.GetValueOrDefault() > 0)
            {
                needs.Add(new SiteNeed(new Resource((need.DefName ?? "")), need.StillNeeded.GetValueOrDefault()));
            }
        }
        return (needs, true);
    }

    /// <summary>
    /// The colony construction census with its standing sites, and the construction deficit, of a frame's
    /// building table. With the stream's memo it applies only the rows changed since the last frame; without one
    /// (a recording) it projects the whole table. A built row the domain refuses fails the census with its error.
    /// </summary>
    public static (Fact<CurrentConstruction> Construction, Fact<IReadOnlyDictionary<Resource, long>> Deficit) ConstructionFromCensus(BuildingCensus census)
    {
        Fact<CurrentConstruction> construction = default;
        Fact<IReadOnlyDictionary<Resource, long>> deficit = default;
        DomainException? error = null;
        object? Update(object? last)
        {
            var state = ConstructionState.Next(last as ConstructionState, census.Rows);
            construction = state.Census;
            deficit = state.DeficitFact;
            error = state.Error;
            return state;
        }
        if (census.Memo is { } memo)
        {
            memo.Update(Update);
        }
        else
        {
            Update(null);
        }
        if (error is not null)
        {
            throw error;
        }
        return (construction, deficit);
    }

    /// <summary>Reads every bill row's live bill-job reservations. Unknown without the bill census or when any row is malformed.</summary>
    public static Fact<EquatableArray<IngredientReservation>> BillReservations(O.BillsSnapshot? v)
    {
        var unknown = Unknown<EquatableArray<IngredientReservation>>();
        if (v is null)
        {
            return unknown;
        }
        var output = new List<IngredientReservation>();
        foreach (var bench in v.Benches)
        {
            foreach (var bill in bench.Bills)
            {
                foreach (var r in bill.Reservations)
                {
                    if ((r.PawnId ?? "") == "")
                    {
                        return unknown;
                    }
                    var items = new List<Amount>();
                    foreach (var item in r.Items)
                    {
                        if ((item.DefName ?? "") == "" || item.Units is null || item.Units.GetValueOrDefault() < 0)
                        {
                            return unknown;
                        }
                        items.Add(new Amount(new Resource((item.DefName ?? "")), item.Units.GetValueOrDefault()));
                    }
                    output.Add(new IngredientReservation { Pawn = new PawnId((r.PawnId ?? "")), Items = items });
                }
            }
        }
        return Known(EquatableArray.From(output));
    }
}
