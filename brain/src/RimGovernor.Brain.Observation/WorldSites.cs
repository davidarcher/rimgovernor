namespace RimGovernor.Brain.Observation;

public static class WorldSitesReads
{
    /// <summary>The world sites of the frame's world-progression read; the read itself is the RoundsFacts aggregate (#3784).</summary>
    internal static Fact<EquatableArray<WorldSite>> FrameWorldSites(WorldProgressionRead? read) =>
        read is null ? Unknown<EquatableArray<WorldSite>>() : Known(EquatableArray.From(read.Sites));
}
