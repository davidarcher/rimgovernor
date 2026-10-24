using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record QuestMonumentMethod
{
    public QuestId Quest { get; init; }

    public string Kind { get; init; } = "";

    public MoveBuilding? Move { get; init; }

    public Building? Build { get; init; }

    public Haul? Haul { get; init; }

    public string Reason { get; init; } = "";
}

public static class QuestMonumentPolicy
{
    public static bool MonumentDeficit(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var offer in rows)
        {
            if (offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Monument is not null)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Follows native sketch progress. Queued construction and an intact completed marker remain native
    /// work; receipts never complete it.
    /// </summary>
    public static QuestMonumentMethod SelectQuestMonument<TOffers>(Fact<TOffers> offers, MapId home, IReadOnlyDictionary<Resource, long> stock, IReadOnlyList<Rectangle> protectedRects)
        where TOffers : IReadOnlyList<QuestState>
    {
        if (!offers.TryGetValue(out var all))
        {
            return new QuestMonumentMethod { Reason = "quest_census_unknown" };
        }
        foreach (var offer in all.OrderBy(o => o.Id))
        {
            if (offer.State != QuestStatus.Ongoing)
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Monument is not { } marker || new MapId(marker.MapId) != home)
                {
                    continue;
                }
                var step = new QuestMonumentMethod { Quest = offer.Id };
                if (marker.State is Pb.QuestMonumentState.Packed packed)
                {
                    return Install(step, marker, packed.InstallCells, protectedRects);
                }
                if (marker.State is not Pb.QuestMonumentState.Installed installed)
                {
                    // An offered marker still waits in its drop pods.
                    return step with { Reason = "marker_unavailable" };
                }
                return Build(step, marker, installed, stock);
            }
        }
        return new QuestMonumentMethod();
    }

    /// <summary>The first legal install cell whose sketch footprint stays clear of the protected rectangles.</summary>
    private static QuestMonumentMethod Install(QuestMonumentMethod step, Pb.QuestMonument marker, EquatableArray<Cell> installCells, IReadOnlyList<Rectangle> protectedRects)
    {
        foreach (var cell in installCells)
        {
            var blocked = false;
            foreach (var rect in protectedRects)
            {
                foreach (var piece in marker.Pieces)
                {
                    foreach (var occupied in piece.Footprint)
                    {
                        if (InteriorWalkRules.RectContains(rect, new Cell(cell.X + occupied.X, cell.Z + occupied.Z)))
                        {
                            blocked = true;
                        }
                    }
                }
            }
            if (!blocked)
            {
                return step with { Kind = "install", Move = new MoveBuilding(marker.MarkerId, marker.DefName, cell, Rotation.North) };
            }
        }
        return step with { Reason = "install_site_unavailable" };
    }

    /// <summary>Hauls loose material to storage first, then places the first unbuilt, unqueued piece of the installed sketch.</summary>
    private static QuestMonumentMethod Build(QuestMonumentMethod step, Pb.QuestMonument marker, Pb.QuestMonumentState.Installed installed, IReadOnlyDictionary<Resource, long> stock)
    {
        if (installed.DisallowedBuildingId is not null)
        {
            return step with { Reason = "disallowed_building" };
        }
        if (installed.AllDone)
        {
            return step with { Kind = "protect" };
        }
        foreach (var resource in marker.Resources)
        {
            if (resource.EligibleHaulers.Count == 0)
            {
                continue;
            }
            return step with { Kind = "haul", Haul = new Haul(resource.EligibleHaulers.Min(), resource.Id, resource.DefName, resource.Cell) };
        }
        foreach (var piece in marker.Pieces)
        {
            if (piece.Site is not Pb.QuestMonumentPieceSite.Placed site)
            {
                throw new RequiredFactException($"quest monument {marker.MarkerId} piece {piece.DefName} progress");
            }
            if (site.Built || site.Queued)
            {
                continue;
            }
            if (!site.Allowed)
            {
                return step with { Reason = "sketch_placement_refused" };
            }
            if (PieceStuff(piece, stock) is not { } stuff)
            {
                return step with { Reason = "monument_materials" };
            }
            try
            {
                var build = new Building(piece.DefName, new Cell(installed.Cell.X + piece.Offset.X, installed.Cell.Z + piece.Offset.Z), piece.Rotation, stuff);
                return step with { Kind = "build", Build = build };
            }
            catch (DomainException)
            {
                return step with { Reason = "sketch_invalid" };
            }
        }
        return step with { Kind = "wait" };
    }

    /// <summary>
    /// The stuff to build a piece from: its single option's stuff ("" for a def not made from stuff), or, when the
    /// sketch leaves the stuff open, the allowed stuff the colony holds most of (ties by name); null when it holds none.
    /// </summary>
    private static string? PieceStuff(Pb.QuestMonumentPiece piece, IReadOnlyDictionary<Resource, long> stock)
    {
        if (piece.Options.Count == 1)
        {
            return piece.Options[0].Stuff ?? "";
        }
        string? best = null;
        foreach (var option in piece.Options.OrderBy(o => o.Stuff, StringComparer.Ordinal))
        {
            if (option.Stuff is not { } candidate)
            {
                continue;
            }
            var have = stock.GetValueOrDefault(new Resource(candidate));
            if (have > 0 && (best is null || have > stock.GetValueOrDefault(new Resource(best))))
            {
                best = candidate;
            }
        }
        return best;
    }

    public static QuestSkipReason MonumentAdmission(QuestState offer, RoundsFacts f)
    {
        if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile })
        {
            return "class_unknown";
        }
        if (profile.Family is not (QuestFamily.BuildMonument or QuestFamily.DecreeMonument or QuestFamily.RoyalHeir))
        {
            return "";
        }
        Pb.QuestMonument? marker = null;
        var timed = false;
        foreach (var objective in offer.Objectives)
        {
            if (objective.Monument is { } m)
            {
                if (marker is not null)
                {
                    return "monument_ambiguous";
                }
                marker = m;
                timed = objective.Timing is not Pb.QuestObjectiveTiming.Untimed;
            }
        }
        if (marker is null)
        {
            return "monument_absent";
        }
        // An installed marker already stands on its site; one not yet installed needs a legal install cell.
        if (marker.State is Pb.QuestMonumentState.Offered { InstallCells.Count: 0 } or Pb.QuestMonumentState.Packed { InstallCells.Count: 0 })
        {
            return "monument_site";
        }
        if (!timed)
        {
            return "deadline_unknown";
        }
        if (f.CurrentConstruction is not { Deficit: var deficits })
        {
            return "materials_unknown";
        }
        var available = new Dictionary<Resource, long>();
        foreach (var a in marker.AvailableResources)
        {
            available[new Resource(a.DefName)] = available.GetValueOrDefault(new Resource(a.DefName)) + a.Units;
        }
        if (marker.State is Pb.QuestMonumentState.Offered offered)
        {
            foreach (var a in offered.Supplied)
            {
                available[new Resource(a.DefName)] = available.GetValueOrDefault(new Resource(a.DefName)) + a.Units;
            }
        }
        foreach (var (resource, count) in deficits)
        {
            available[resource] = available.GetValueOrDefault(resource) - count;
        }
        // Reserve unmet material for earlier nonautomatic quests before this offer.
        if (!f.QuestOffers.TryGetValue(out var open))
        {
            return "open_demands_unknown";
        }
        foreach (var q in open)
        {
            if (q.State != QuestStatus.Ongoing || q.Id == offer.Id)
            {
                continue;
            }
            if (f.RootOf(q) is not QuestRoot.Classified { Profile: var p })
            {
                return "open_demands_unknown";
            }
            if (p.Cost == QuestCost.Free || p.NeverAct || p.Disposition == QuestDisposition.Observe)
            {
                continue;
            }
            foreach (var o in q.Objectives)
            {
                if (o.Monument is { } m && !FundMonument(m, available))
                {
                    return "monument_materials";
                }
            }
        }
        if (!FundMonument(marker, available))
        {
            return "monument_materials";
        }
        if ((profile.Demands & QuestDemand.Security) != 0)
        {
            var threat = offer.Threat is Pb.QuestThreat.Points points ? points.Value : 0;
            if (!f.DefenseCapacity.TryGetValue(out var defense))
            {
                return "defense_unknown";
            }
            if (defense < threat)
            {
                return "defense_capacity";
            }
        }
        return "";
    }

    /// <summary>Whether a piece is built on the installed marker.</summary>
    public static bool PieceBuilt(Pb.QuestMonumentPiece piece) => piece.Site is Pb.QuestMonumentPieceSite.Placed { Built: true };

    private static bool FundMonument(Pb.QuestMonument marker, Dictionary<Resource, long> available)
    {
        foreach (var piece in marker.Pieces)
        {
            if (PieceBuilt(piece))
            {
                continue;
            }
            var funded = false;
            foreach (var option in piece.Options.OrderBy(o => o.Stuff, StringComparer.Ordinal))
            {
                var costs = new Dictionary<Resource, long>();
                foreach (var c in option.Costs)
                {
                    costs[new Resource(c.DefName)] = costs.GetValueOrDefault(new Resource(c.DefName)) + c.Units;
                }
                var enough = true;
                foreach (var (r, n) in costs)
                {
                    if (n < 0 || available.GetValueOrDefault(r) < n)
                    {
                        enough = false;
                        break;
                    }
                }
                if (!enough)
                {
                    continue;
                }
                foreach (var (r, n) in costs)
                {
                    available[r] = available.GetValueOrDefault(r) - n;
                }
                funded = true;
                break;
            }
            if (!funded)
            {
                return false;
            }
        }
        return true;
    }
}
