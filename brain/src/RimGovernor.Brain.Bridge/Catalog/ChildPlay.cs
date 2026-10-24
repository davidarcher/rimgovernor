using RimWorld;
using Verse;

namespace RimGovernor.Brain.Bridge;

// The buildings the babies' play uses (#3837). A baby's play need (NeedDef Play, developmentalStageFilter Baby)
// rises by the BabyPlayDef activities a carer gives it. Each def names its giver class (workerClass); the building
// the giver plays at is the game's code, not a def field (ilspycmd): BabyPlayGiver_PlayToys looks for a
// ThingDefOf.ToyBox in the baby's room, then within 15.9 cells (IsValidToybox), while BabyPlayGiver_PlayStatic and
// BabyPlayGiver_PlayWalking use no building. A load without Biotech has no BabyPlayDef, so it has no play building:
// a named state, not an unknown. A giver class this read does not know cannot be read and throws.

public sealed partial class DefinitionCatalog
{
    private readonly Lazy<IReadOnlyList<string>> _playBuildings;

    /// <summary>The building defs the load's BabyPlayDef givers play at, sorted and distinct; empty without Biotech.</summary>
    public IReadOnlyList<string> PlayBuildings() => _playBuildings.Value;

    private IReadOnlyList<string> BuildPlayBuildings()
    {
        var output = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var play in Rows<BabyPlayDef>().Values)
        {
            if (PlayBuilding(play) is { } building)
            {
                output.Add(building);
            }
        }

        return [.. output];
    }

    // The building one play def's giver class uses; null for a giver that plays without one.
    private string? PlayBuilding(BabyPlayDef play)
    {
        var giver = play.workerClass ?? throw new CatalogException($"baby play {play.defName} has no worker class");
        if (typeof(BabyPlayGiver_PlayToys).IsAssignableFrom(giver))
        {
            var toyBox = nameof(ThingDefOf.ToyBox);
            return _thingDefs.ContainsKey(toyBox) ? toyBox : throw new CatalogException($"baby play {play.defName} plays at {toyBox}, which is not a def of the catalog");
        }

        if (typeof(BabyPlayGiver_PlayStatic).IsAssignableFrom(giver) || typeof(BabyPlayGiver_PlayWalking).IsAssignableFrom(giver))
        {
            return null;
        }

        throw new CatalogException($"baby play {play.defName}: giver class {giver.FullName} names no known building");
    }
}
