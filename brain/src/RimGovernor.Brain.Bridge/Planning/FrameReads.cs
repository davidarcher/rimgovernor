using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
namespace RimGovernor.Brain.Bridge;

public static class FrameReads
{
    /// <summary>Joins every routine frame's required room table to comfort; detailed room consumers are opt-in.</summary>
    internal static ColonyReading WithFrameRooms(ColonyReading p, ReviewFrame frame, DiningFurniture dining)
    {
        // The frame's rooms census is the one room table: room quality reads it on every reading, the room census
        // sections only when asked.
        var frameRooms = frame.Rooms ?? throw new ObservationContractException("a routine frame carries no room census");
        var bedded = p.Facts.Sleeping with { Rooms = SleepingUpkeepReads.UpkeepRooms(frameRooms, p.Facts.Sleeping.Beds) };
        var comfortRooms = new RoomObservation
        {
            Rooms = frameRooms.Rooms,
            Cells = frame.RoomCells,
            EligibleBeds = EquatableArray.From(p.Facts.Sleeping.Beds.Where(bed => bed.Humanlike && !bed.Medical && !bed.Prisoners).Select(bed => bed.Id)),
            Dining = dining,
            Shapes = p.Facts.Shapes,
        };
        p = p with { Facts = p.Facts with { Sleeping = bedded, Comfort = p.Facts.RequireBasicComfort() } };
        p = p with { Facts = p.Facts with { Upkeep = p.Facts.Upkeep with { Rooms = comfortRooms } } };
        return p;
    }

    /// <summary>Resolves definitions against the frame's catalog, research and crop rows.</summary>
    internal static DefinitionFacts FrameDefinitionFacts(ReviewFrame frame, DefinitionCatalog catalog) =>
        new()
        {
            Catalog = catalog,
            Crops = DefinitionsReads.CropRows((frame.Colony ?? throw new RequiredFactException("colony")).Crops),
            Finished = DefinitionsReads.FinishedSet((frame.Research ?? throw new RequiredFactException("finished research")).Finished),
        };

    /// <summary>
    /// The catalog's classification of every quest root the census holds, once per root: its profile and, for a
    /// ship-only Odyssey root, the space layer (QuestClass). A root the catalog cannot classify carries why the class
    /// failed ("" when only the profile did).
    /// </summary>
    internal static EquatableArray<QuestRoot> FrameQuestRoots(RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot read, DefinitionCatalog catalog)
    {
        var roots = new List<QuestRoot>();
        foreach (var def in read.Quests.Select(q => q.ScriptDef).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string layer = "", classError = "";
            try
            {
                layer = catalog.QuestClass(def).SpaceLayer;
            }
            catch (DomainException e)
            {
                classError = e.Message;
            }
            try
            {
                roots.Add(new QuestRoot.Classified { ScriptDef = def, Profile = catalog.QuestProfile(def), SpaceLayer = layer });
            }
            catch (DomainException)
            {
                roots.Add(new QuestRoot.Unclassified { ScriptDef = def, Error = classError });
            }
        }
        return EquatableArray.From(roots);
    }

    /// <summary>The finished buildings of the frame's building table counted by definition (what a built-count goal is judged by).</summary>
    internal static EquatableArray<Amount> BuiltBuildingsOf(BuildingCensus census)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in census.Rows.Table.Values())
        {
            if (row.Status == RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built)
            {
                counts[row.Building.DefName] = counts.GetValueOrDefault(row.Building.DefName) + 1;
            }
        }
        return EquatableArray.From(counts.Select(c => new Amount(new Resource(c.Key), c.Value)));
    }

    /// <summary>The research section of a frame read from the source; the empty carrier frame has none.</summary>
    internal static RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot ResearchSection(ReviewFrame frame) =>
        frame.Research ?? throw new BridgeUnavailableException("The frame holds no research section.");

    internal static ResearchFacts FrameResearch(RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot read, DefinitionCatalog catalog) =>
        EconomyRules.ResearchSummary(EconomyRules.ResearchCensusOf(read, catalog.Research, catalog.KnowledgeOverflow));

    /// <summary>The trader census; unknown when the frame carries none.</summary>
    internal static EquatableArray<RimGovernor.Brain.GameFacts.Observations.Trader> FrameTraders(RimGovernor.Brain.GameFacts.Observations.TradersSnapshot? read) =>
        read?.Traders ?? throw new BridgeUnavailableException("The frame holds no trader census.");

}
