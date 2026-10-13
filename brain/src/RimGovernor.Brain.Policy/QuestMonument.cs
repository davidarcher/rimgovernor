using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

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

public static partial class PolicyFunctions
{
    public static bool MonumentDeficit(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var offer in rows)
        {
            if (offer.State != "Ongoing")
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Monument.TryGetValue(out _))
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
        where TOffers : IReadOnlyList<JoinerOffer>
    {
        if (!offers.TryGetValue(out var all))
        {
            return new QuestMonumentMethod { Reason = "quest_census_unknown" };
        }
        foreach (var offer in all.OrderBy(o => o.Quest))
        {
            if (offer.State != "Ongoing")
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (!objective.Monument.TryGetValue(out var marker) || marker.Map != home)
                {
                    continue;
                }
                var step = new QuestMonumentMethod { Quest = offer.Quest };
                if (marker.DisallowedBuilding.Length != 0)
                {
                    return step with { Reason = "disallowed_building" };
                }
                if (marker.AllDone.TryGetValue(out var done) && done)
                {
                    return step with { Kind = "protect" };
                }
                if (marker.Packed.TryGetValue(out var packed) && packed)
                {
                    foreach (var cell in marker.InstallCells)
                    {
                        var blocked = false;
                        foreach (var rect in protectedRects)
                        {
                            foreach (var piece in marker.Pieces)
                            {
                                foreach (var occupied in piece.Footprint)
                                {
                                    if (RectContains(rect, new Cell(cell.X + occupied.X, cell.Z + occupied.Z)))
                                    {
                                        blocked = true;
                                    }
                                }
                            }
                        }
                        if (blocked)
                        {
                            continue;
                        }
                        try
                        {
                            return step with { Kind = "install", Move = new MoveBuilding(marker.Marker, marker.Def, cell, Rotation.North) };
                        }
                        catch (DomainException)
                        {
                        }
                    }
                    return step with { Reason = "install_site_unavailable" };
                }
                if (!marker.Installed.TryGetValue(out var installed) || !installed)
                {
                    return step with { Reason = "marker_unavailable" };
                }
                foreach (var resource in marker.Resources)
                {
                    if (!resource.InStorage.TryGetValue(out var stored) || stored || resource.Haulers.Count == 0)
                    {
                        continue;
                    }
                    var hauler = resource.Haulers.Min();
                    try
                    {
                        return step with { Kind = "haul", Haul = new Haul((Domain.PawnId)hauler, resource.Id, resource.Def, resource.Cell) };
                    }
                    catch (DomainException)
                    {
                    }
                }
                foreach (var piece in marker.Pieces)
                {
                    var bk = piece.Built.TryGetValue(out var built);
                    var qk = piece.Queued.TryGetValue(out var queued);
                    var ak = piece.Allowed.TryGetValue(out var allowed);
                    if (!bk || !qk || !ak)
                    {
                        return step with { Reason = "sketch_unknown" };
                    }
                    if (built || queued)
                    {
                        continue;
                    }
                    if (!allowed)
                    {
                        return step with { Reason = "sketch_placement_refused" };
                    }
                    var stuff = piece.Stuff;
                    if (stuff.Length == 0 && piece.AllowedStuffs.Count > 0)
                    {
                        foreach (var candidate in piece.AllowedStuffs.OrderBy(s => s, StringComparer.Ordinal))
                        {
                            var have = stock.GetValueOrDefault(new Resource(candidate));
                            if (have > 0 && (stuff.Length == 0 || have > stock.GetValueOrDefault(new Resource(stuff))))
                            {
                                stuff = candidate;
                            }
                        }
                        if (stuff.Length == 0)
                        {
                            return step with { Reason = "monument_materials" };
                        }
                    }
                    try
                    {
                        var build = new Building(piece.Def, new Cell(marker.Cell.X + piece.Offset.X, marker.Cell.Z + piece.Offset.Z), piece.Rotation, stuff);
                        return step with { Kind = "build", Build = build };
                    }
                    catch (DomainException)
                    {
                        return step with { Reason = "sketch_invalid" };
                    }
                }
                return step with { Kind = "wait" };
            }
        }
        return new QuestMonumentMethod();
    }

    public static QuestSkipReason MonumentAdmission(JoinerOffer offer, RoundsFacts f)
    {
        if (!offer.Profile.TryGetValue(out var profile))
        {
            return "class_unknown";
        }
        if (profile.Family is not (QuestFamily.BuildMonument or QuestFamily.DecreeMonument or QuestFamily.RoyalHeir))
        {
            return "";
        }
        QuestMonument? marker = null;
        var timed = false;
        foreach (var objective in offer.Objectives)
        {
            if (objective.Monument.TryGetValue(out var m))
            {
                if (marker is not null)
                {
                    return "monument_unknown";
                }
                marker = m;
                var d = objective.DeadlineTicks.TryGetValue(out _);
                var t = objective.DurationTicks.TryGetValue(out _);
                timed = d || t;
            }
        }
        if (marker is null)
        {
            return "monument_unknown";
        }
        if (!marker.ClearSite.TryGetValue(out var clear))
        {
            return "monument_site_unknown";
        }
        if (!clear || marker.InstallCells.Count == 0)
        {
            return "monument_site";
        }
        if (!timed)
        {
            return "deadline_unknown";
        }
        if (!f.ConstructionDeficit.TryGetValue(out var deficits))
        {
            return "materials_unknown";
        }
        var available = new Dictionary<Resource, long>();
        foreach (var a in marker.AvailableResources)
        {
            available[a.Resource] = available.GetValueOrDefault(a.Resource) + a.Count;
        }
        foreach (var a in marker.SuppliedResources)
        {
            available[a.Resource] = available.GetValueOrDefault(a.Resource) + a.Count;
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
            if (q.State != "Ongoing" || q.Quest == offer.Quest)
            {
                continue;
            }
            if (!q.Profile.TryGetValue(out var p))
            {
                return "open_demands_unknown";
            }
            if (p.Cost == QuestCost.Free || p.NeverAct || p.Disposition == QuestDisposition.Observe)
            {
                continue;
            }
            foreach (var o in q.Objectives)
            {
                if (o.Monument.TryGetValue(out var m) && !FundMonument(m, available))
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
            var tk = offer.ThreatPoints.TryGetValue(out var threat);
            var dk = f.DefenseCapacity.TryGetValue(out var defense);
            if (!tk || !dk)
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

    private static bool FundMonument(QuestMonument marker, Dictionary<Resource, long> available)
    {
        foreach (var piece in marker.Pieces)
        {
            if (piece.Built.TryGetValue(out var built) && built)
            {
                continue;
            }
            var funded = false;
            foreach (var option in piece.BuildOptions.OrderBy(o => o.Stuff, StringComparer.Ordinal))
            {
                var costs = new Dictionary<Resource, long>();
                foreach (var c in option.Costs)
                {
                    costs[c.Resource] = costs.GetValueOrDefault(c.Resource) + c.Count;
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
