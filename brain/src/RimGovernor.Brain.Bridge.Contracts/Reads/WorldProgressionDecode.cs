using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

partial record WorldProgressionRead
{

    /// <summary>Projects the world census a frame carries.</summary>
    public static WorldProgressionRead DecodeWorldProgressionSnapshot(WorldProgressionSnapshot v)
    {
        return new WorldProgressionRead
        {
            Context = v.Context,
            Maps = [.. v.Maps],
            Caravans = [.. v.Caravans],
            Quests = [.. v.Quests],
            Sites = [.. v.Sites],
            Assemblies = [.. v.Assemblies],
        };
    }
}
