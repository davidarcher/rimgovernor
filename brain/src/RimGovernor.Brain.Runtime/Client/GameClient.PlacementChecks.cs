using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Runtime;

public sealed partial class GameClient
{
    /// <summary>
    /// The shared checks and request builders of the placement, zone, building-target, supply and site reads (#3113
    /// group G4a). A failed check throws a contract
    /// exception.
    /// </summary>
    internal static class PlacementChecks
    {
        /// <summary>Whether <paramref name="check"/> passes (does not throw a contract error).</summary>
        internal static bool Passes(Action check)
        {
            try
            {
                check();
                return true;
            }
            catch (BridgeContractException)
            {
                return false;
            }
        }

        /// <summary>A present cell with non-negative coordinates.</summary>
        internal static void ValidCell(RimGovernor.Brain.Domain.Cell cell)
        {
            if (cell.X < 0 || cell.Z < 0)
            {
                throw BridgeContractException.Contract("cell presence/coordinates");
            }
        }

        internal static bool RectangleValid(RimGovernor.Brain.GameFacts.Observations.Rectangle? rect, long maxWidth) =>
            rect is not null
            && Passes(() => ValidCell(rect.Minimum))
            && Passes(() => ValidCell(rect.Maximum))
            && rect.Maximum!.X >= rect.Minimum!.X
            && rect.Maximum.Z >= rect.Minimum.Z
            && (long)rect.Maximum.X - rect.Minimum.X < maxWidth
            && (long)rect.Maximum.Z - rect.Minimum.Z < maxWidth;

        /// <summary>The exact-or-census building list request (built, player).</summary>
        internal static RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest ConstructionBuildingsRequest(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> ids) =>
            new()
            {
                Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
                Ids = [.. ids],
                Statuses = [RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built],
            };

        /// <summary>The zone census read.</summary>
        internal static RimGovernor.Brain.GameOrders.Observations.ListZonesRequest ZoneSectionRequest(RimGovernor.Brain.GameFacts.Common.Identity identity) =>
            new() { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity } };
    }
}
