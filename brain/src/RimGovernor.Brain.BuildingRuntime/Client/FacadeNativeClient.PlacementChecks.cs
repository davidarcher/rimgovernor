using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// The shared checks and request builders of the placement, zone, building-target, supply and site reads (#3113
    /// group G4a): Go's <c>buildingContext</c>, <c>validCell</c> and <c>validRectangle</c>. A failed check throws what
    /// Go's <c>contract</c> returned.
    /// </summary>
    internal static partial class PlacementReads
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

        /// <summary>Go's <c>validCell</c>: a present cell with non-negative coordinates.</summary>
        internal static void ValidCell(PbCommon.Cell? cell)
        {
            if (cell is null || cell.X < 0 || cell.Z < 0)
            {
                throw BridgeFunctions.Contract("cell presence/coordinates");
            }
        }

        /// <summary>Go's <c>validRectangle(rect, maxWidth) == nil</c>.</summary>
        internal static bool RectangleValid(PbObservations.Rectangle? rect, long maxWidth) =>
            rect is not null
            && Passes(() => ValidCell(rect.Minimum))
            && Passes(() => ValidCell(rect.Maximum))
            && rect.Maximum!.X >= rect.Minimum!.X
            && rect.Maximum.Z >= rect.Minimum.Z
            && (long)rect.Maximum.X - rect.Minimum.X < maxWidth
            && (long)rect.Maximum.Z - rect.Minimum.Z < maxWidth;

        /// <summary>The exact-or-census building list request of Go's <c>constructionBuildingsRequest</c> (built, player).</summary>
        internal static PbObservations.ListBuildingsRequest ConstructionBuildingsRequest(PbCommon.Identity identity, IReadOnlyList<string> ids) =>
            new()
            {
                Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
                Ids = [.. ids],
                Statuses = [PbObservations.BuildingStatus.Built],
            };

        /// <summary>Go's <c>zoneSectionRequest</c>: the zone census read.</summary>
        internal static PbObservations.ListZonesRequest ZoneSectionRequest(PbCommon.Identity identity) =>
            new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
    }
}
