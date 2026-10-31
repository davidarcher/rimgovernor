using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Bridge;

/// <summary>The activity site definitions (habitat 3.3), read from the game's own Def types, never retyped.</summary>
public static class ActivitySiteReads
{
    /// <summary>The spot each site placement builds: the definitions the rooms read names so its builder gate is read.</summary>
    public static IReadOnlyList<string> Placed { get; } = [nameof(RimWorld.ThingDefOf.PartySpot), nameof(RimWorld.ThingDefOf.MarriageSpot), nameof(RimWorld.ThingDefOf.RitualSpot)];

    /// <summary>The spot a placement of <paramref name="kind"/>'s site builds.</summary>
    public static string PlaceDefinition(ActivityKind kind) =>
        kind == ActivityKind.Party ? nameof(RimWorld.ThingDefOf.PartySpot)
            : kind == ActivityKind.Marriage ? nameof(RimWorld.ThingDefOf.MarriageSpot) : nameof(RimWorld.ThingDefOf.RitualSpot);

    /// <summary>
    /// The site definitions: the Party GatheringDef's gatherSpotDefs (what RCellFinder.TryFindGatheringSpot picks a
    /// building from), unread naming the def when the catalog has no Party row, and the marriage and ritual spots.
    /// </summary>
    internal static ActivitySiteDefs SiteDefs(DefinitionCatalog catalog)
    {
        SiteDefsRead party = catalog.DefRow<RimWorld.GatheringDef>(nameof(RimWorld.GatheringDefOf.Party)) is { } row
            ? new SiteDefsRead.Read(EquatableArray.From((row.gatherSpotDefs ?? []).Select(d => d.defName).Order(StringComparer.Ordinal).ToList()))
            : new SiteDefsRead.Unread("gathering_def:" + nameof(RimWorld.GatheringDefOf.Party));
        return new ActivitySiteDefs
        {
            PartySpots = party,
            PartySpot = PlaceDefinition(ActivityKind.Party),
            MarriageSpot = PlaceDefinition(ActivityKind.Marriage),
            RitualSpot = PlaceDefinition(ActivityKind.Ritual),
            Party = Attended(catalog, nameof(RimWorld.ThoughtDefOf.AttendedParty)),
            Wedding = Attended(catalog, nameof(RimWorld.ThoughtDefOf.AttendedWedding)),
        };
    }

    /// <summary>An attendance memory's first-stage mood and duration (the ledger's site credit, ForecastActivitySites); unread naming the def without a row or stage.</summary>
    private static AttendedThought Attended(DefinitionCatalog catalog, string def) =>
        catalog.DefRow<RimWorld.ThoughtDef>(def) is { } row && row.stages.Seq().FirstOrDefault() is { } stage
            ? new AttendedThought.Read(DefinitionCatalog.Decimal32(stage.baseMoodEffect), DefinitionCatalog.Decimal32(row.durationDays))
            : new AttendedThought.Unread("thought_def:" + def);
}
