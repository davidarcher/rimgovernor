using System.Text;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// The shared checks and request builders of the placement, zone, building-target, supply and site reads (#3113
    /// group G4a): Go's <c>buildingContext</c>, <c>ValidateConstructionBuildings</c>, <c>validateZonePage</c>,
    /// <c>validCell</c> and <c>validRectangle</c>. A failed check throws what Go's <c>contract</c> returned.
    /// </summary>
    internal static partial class PlacementReads
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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

        /// <summary>Go's <c>buildingContext</c>: a valid context for the same identity (and, when exact, the same native generation).</summary>
        internal static void BuildingContext(PbCommon.ObservationContext? value, PbCommon.Identity? identity, ulong generation, bool exact)
        {
            if (!BridgeFunctions.SameIdentity(value!.Identity, identity) || (exact && (value.NativeGeneration is null || value.NativeGeneration.GetValueOrDefault() != generation)))
            {
                throw BridgeFunctions.Contract("building context mismatch");
            }
        }

        /// <summary>Go's <c>validCell</c>: a present cell with non-negative coordinates.</summary>
        internal static void ValidCell(PbCommon.Cell? cell)
        {
            if (cell is null || cell.X is null || cell.Z is null || cell.X.GetValueOrDefault() < 0 || cell.Z.GetValueOrDefault() < 0)
            {
                throw BridgeFunctions.Contract("cell presence/coordinates");
            }
        }

        /// <summary>Go's <c>validRectangle(rect, maxWidth) == nil</c>.</summary>
        internal static bool RectangleValid(PbObservations.Rectangle? rect, long maxWidth) =>
            rect is not null
            && Passes(() => ValidCell(rect.Minimum))
            && Passes(() => ValidCell(rect.Maximum))
            && rect.Maximum!.X.GetValueOrDefault() >= rect.Minimum!.X.GetValueOrDefault()
            && rect.Maximum.Z.GetValueOrDefault() >= rect.Minimum.Z.GetValueOrDefault()
            && (long)rect.Maximum.X.GetValueOrDefault() - rect.Minimum.X.GetValueOrDefault() < maxWidth
            && (long)rect.Maximum.Z.GetValueOrDefault() - rect.Minimum.Z.GetValueOrDefault() < maxWidth;

        /// <summary>Go's <c>adjacentCells</c>: the cells differ by one step along one axis.</summary>
        internal static bool AdjacentCells(PbCommon.Cell? a, PbCommon.Cell? b)
        {
            long dx = (long)(a?.X ?? 0) - (b?.X ?? 0);
            long dz = (long)(a?.Z ?? 0) - (b?.Z ?? 0);
            return (dx * dx) + (dz * dz) == 1;
        }

        /// <summary>Go's <c>presentationText</c>: an absent string, or valid text of at most <paramref name="limit"/> bytes.</summary>
        internal static bool PresentationText(string? value, int limit)
        {
            if (value is null)
            {
                return true;
            }
            try
            {
                return StrictUtf8.GetByteCount(value) <= limit;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>Go's <c>refSnapshot</c>: whether <paramref name="snapshot"/> is the CAS token of <paramref name="reference"/> read under <paramref name="context"/>.</summary>
        internal static bool RefSnapshot(PbObservations.SnapshotRef? snapshot, PbCommon.Ref? reference, PbCommon.ObservationContext? context) =>
            snapshot is not null
            && reference is not null
            && (snapshot.EntityId ?? "") == (reference.Id ?? "")
            && BridgeFunctions.IsValidId(snapshot.Token ?? "")
            && Equals(snapshot.Context, context);

        /// <summary>
        /// Go's <c>ValidateConstructionBuildings</c>: the building list was read under the expected identity.
        /// </summary>
        internal static void ValidateConstructionBuildings(PbObservations.BuildingsSnapshot? value, PbCommon.Identity? identity)
        {
            if (value is null || !Passes(() => BridgeFunctions.ValidateContext(value.Context)) || !BridgeFunctions.SameIdentity(value.Context!.Identity, identity))
            {
                throw BridgeFunctions.Contract("invalid building query context");
            }
        }

        /// <summary>Go's <c>validFarmMeasures</c>: counts within the planted cells, growth a fraction, factors non-negative, every number finite.</summary>
        internal static bool ValidFarmMeasures(PbObservations.FarmFacts farm)
        {
            if (farm.PlantedCells is not null)
            {
                foreach (var count in new[] { farm.FertilePlantedCells, farm.BlightedPlants })
                {
                    if (count is { } n && n > farm.PlantedCells.GetValueOrDefault())
                    {
                        return false;
                    }
                }
            }
            foreach (var fraction in new[] { farm.GrowthMin, farm.GrowthMean })
            {
                if (fraction is { } f && (!BridgeFunctions.CombatNumber(f, true) || f > 1))
                {
                    return false;
                }
            }
            foreach (var value in new[] { farm.FertilityFactorMin, farm.FertilityFactorMean, farm.LightFactorMean })
            {
                if (!BridgeFunctions.CombatNumber(value, true))
                {
                    return false;
                }
            }
            return BridgeFunctions.CombatNumber(farm.Temperature, false);
        }

        /// <summary>Go's <c>validateZonePage</c>: a zone census page's context, rows and farm facts.</summary>
        internal static void ValidateZonePage(PbObservations.ZonesSnapshot? value, PbCommon.Identity? identity)
        {
            if (value is null)
            {
                throw BridgeFunctions.Contract("missing zone snapshot");
            }
            if (!BridgeFunctions.SameIdentity(value.Context!.Identity, identity))
            {
                throw BridgeFunctions.Contract("zone identity mismatch");
            }
            foreach (var row in value.Zones)
            {
                if (row is null || !BridgeFunctions.IsValidId(row.Id ?? "") || row.FoodStorage is null)
                {
                    throw BridgeFunctions.Contract("invalid zone row");
                }
                if (row.Farm is { } farm)
                {
                    if ((farm.Zone?.Id ?? "") != (row.Id ?? "")
                        || !BridgeFunctions.IsValidId(farm.Crop ?? "")
                        || farm.UsableCells is null
                        || farm.PlantedCells is null
                        || farm.EdibleCrop is null
                        || !ValidFarmMeasures(farm))
                    {
                        throw BridgeFunctions.Contract("invalid zone farm facts");
                    }
                }
                else if ((row.Type ?? "") == "growing")
                {
                    throw BridgeFunctions.Contract("missing zone farm facts");
                }
            }
        }

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
