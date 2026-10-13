using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_joy.go: the joy buildings and the joy model.

public sealed partial class DefinitionCatalog
{
    private sealed record RankedJoyBuilding(P.JoyBuildingMethod Method, ThingDef Row, double Joy, double Cost);

    /// <summary>
    /// The joy buildings the colony can build for recreation, chosen by a rule over the catalog rows and never by name:
    /// a buildable definition whose ThingDef gives a joy kind (<c>BuildingProperties.joyKind</c>), with the power its
    /// power comp draws. The preference order is the joy one session gives, most first: the JobDef joyGainRate times
    /// joyDuration of the JoyGiverDef that offers the building (the best giver when several do); ties go to the cheaper
    /// building (the market value of its adjusted costs at its cheapest stuff), then to the name. A joy building no
    /// JoyGiverDef offers and a giver without its job row are errors (a giver with no jobDef offers no buildings and is
    /// skipped). Research and builders are the planner's to check.
    /// </summary>
    public IReadOnlyList<P.JoyBuildingMethod> JoyBuildings() => [.. RankedJoyBuildings().Select(r => r.Method)];

    private List<RankedJoyBuilding> RankedJoyBuildings()
    {
        var sessions = JoyPerSession();
        var found = new List<RankedJoyBuilding>();
        foreach (var (name, row) in _thingDefs)
        {
            if (row.building?.joyKind is not { } kind || !Buildable(row))
            {
                continue;
            }

            if (!sessions.TryGetValue(name, out var joy))
            {
                throw new CatalogException($"joy building {name} is offered by no joy giver");
            }

            var cost = CheapestCostValue(name);
            var watts = PowerDraw(row) ?? 0;
            found.Add(new RankedJoyBuilding(new P.JoyBuildingMethod { Definition = name, Kind = kind.defName, PowerW = Math.Max(0, watts) }, row, joy, cost));
        }

        found.Sort((a, b) =>
        {
            var byJoy = b.Joy.CompareTo(a.Joy);
            if (byJoy != 0)
            {
                return byJoy;
            }

            var byCost = a.Cost.CompareTo(b.Cost);
            return byCost != 0 ? byCost : string.CompareOrdinal(a.Method.Definition, b.Method.Definition);
        });
        return found;
    }

    /// <summary>
    /// The joy building the colony's first recreation facility is: the cheapest joy building that draws no power and
    /// needs no research (the one a colony can build before anything else), ties to the joy one session gives, then the
    /// name. A catalog with none is an error.
    /// </summary>
    public string RecreationFoothold()
    {
        RankedJoyBuilding? best = null;
        foreach (var r in RankedJoyBuildings())
        {
            if (r.Method.PowerW > 0 || r.Row.researchPrerequisites.Seq().Any())
            {
                continue;
            }

            if (best is null || r.Cost < best.Cost)
            {
                best = r;
            }
        }

        return best?.Method.Definition ?? throw new CatalogException("catalog has no joy building that draws no power and needs no research");
    }

    /// <summary>The building defs a JoyGiverDef of the watch-building class offers (<c>WatchBuildingUtility</c>): the ones whose placement preview reports whether every colonist can reach a watch cell. Sorted by name.</summary>
    public IReadOnlyList<string> WatchBuildings() =>
        [.. Rows<JoyGiverDef>().Values
            .Where(giver => giver.giverClass is { } kind && typeof(JoyGiver_WatchBuilding).IsAssignableFrom(kind))
            .SelectMany(giver => giver.thingDefs.Seq().Select(d => d.defName))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    // The joy one session of each building gives, by building def: the best of its JoyGiverDefs' jobs, joyGainRate times
    // joyDuration.
    private Dictionary<string, double> JoyPerSession()
    {
        var sessions = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var giver in Rows<JoyGiverDef>().Values.OrderBy(g => g.defName, StringComparer.Ordinal))
        {
            // A giver with no jobDef hardcodes its job in its class (the ingest givers offer food items, not buildings); a
            // joy building such a giver offers surfaces as "offered by no joy giver".
            if (!giver.thingDefs.Seq().Any() || giver.jobDef is not { } job)
            {
                continue;
            }

            double joy = job.joyGainRate * (double)job.joyDuration;
            foreach (var building in giver.thingDefs)
            {
                if (!_thingDefs.ContainsKey(building.defName))
                {
                    throw new CatalogException($"joy giver {giver.defName} offers {building.defName}, which the catalog has no def row for");
                }

                if (!sessions.TryGetValue(building.defName, out var best) || joy > best)
                {
                    sessions[building.defName] = joy;
                }
            }
        }

        return sessions;
    }

    // The joy givers a colonist may use with no building: the givers with a job and no thingDefs, each with its chance
    // (baseChance times the share of pawns that ever do it), the joy a session of its job gives before the game's
    // per-tick scale, and whether it is outdoors (the def's own flags, or the table for a class that is). A catalog with
    // no NeedDef for joy leaves the model unknown.
    internal P.JoyModel JoyModel()
    {
        var game = P.PolicyFunctions.JoyGame();
        if (DefRow<NeedDef>(game.Need) is null)
        {
            return new P.JoyModel();
        }

        var sources = new List<P.JoySource>();
        foreach (var giver in Rows<JoyGiverDef>().Values.OrderBy(g => g.defName, StringComparer.Ordinal))
        {
            if (giver.jobDef is not { } job || giver.thingDefs.Seq().Any())
            {
                continue;
            }

            sources.Add(new P.JoySource
            {
                Giver = giver.defName,
                Weight = giver.baseChance * (double)giver.pctPawnsEverDo,
                Session = job.joyGainRate * (double)job.joyDuration,
                Outdoor = giver.unroofedOnly || giver.requiresEnjoyOutdoors || game.OutdoorGivers.Contains(giver.defName),
            });
        }

        return new P.JoyModel { Sources = sources };
    }
}
