using System.Text;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// The shared checks and request builders of the placement, zone, building-target, supply and site reads (#3113
    /// group G4a): Go's <c>buildingContext</c>, <c>pawnsEntity</c>, <c>validateConstructionRow</c>,
    /// <c>ValidateConstructionBuildings</c>, <c>validateZonePage</c>, <c>validCell</c>, <c>validRectangle</c> and the
    /// anomaly and odyssey row bounds they read. A failed check throws what Go's <c>contract</c> returned.
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
            BridgeFunctions.ValidateContext(value);
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

        /// <summary>Go's <c>pawnsEntity</c>: the head of a row against the context it was read under.</summary>
        internal static void Entity(PbObservations.EntityRef? value, PbCommon.ObservationContext? context)
        {
            if (value is null)
            {
                throw BridgeFunctions.Contract("pawn entity missing");
            }
            BridgeFunctions.ValidId(value.Id ?? "");
            if (value.DefName is not null)
            {
                BridgeFunctions.ValidId(value.DefName);
            }
            if (!PresentationText(value.Label, 16384))
            {
                throw BridgeFunctions.Contract("invalid pawn label");
            }
            if (value.MapId is not null && value.MapId.GetValueOrDefault() != (context?.Identity?.MapId ?? 0))
            {
                throw BridgeFunctions.Contract("pawn entity map mismatch");
            }
            if (value.Position is not null && (value.Position.X is null || value.Position.Z is null || value.Position.X.GetValueOrDefault() < 0 || value.Position.Z.GetValueOrDefault() < 0))
            {
                throw BridgeFunctions.Contract("invalid pawn position");
            }
        }

        // Go catalogNumbers: every present number is finite.
        private static void CatalogNumbers(string kind, params double?[] values)
        {
            foreach (var value in values)
            {
                if (value is { } v && !double.IsFinite(v))
                {
                    throw BridgeFunctions.Contract($"nonfinite {kind} number");
                }
            }
        }

        // Go validateStudyState: bounds a CompStudiable block of a pawn or building row.
        private static void ValidateStudyState(PbObservations.StudyState? s)
        {
            if (s is null)
            {
                return;
            }
            CatalogNumbers("anomaly study", s.ProgressPercent, s.StudyPoints, s.AnomalyKnowledgeGained, s.AnomalyKnowledge);
            if (s.ProgressPercent is not null && (s.ProgressPercent.GetValueOrDefault() < 0 || s.ProgressPercent.GetValueOrDefault() > 1))
            {
                throw BridgeFunctions.Contract("anomaly study progress outside 0..1");
            }
            if ((s.StudyPoints is not null && s.StudyPoints.GetValueOrDefault() < 0)
                || (s.AnomalyKnowledgeGained is not null && s.AnomalyKnowledgeGained.GetValueOrDefault() < 0)
                || (s.AnomalyKnowledge is not null && s.AnomalyKnowledge.GetValueOrDefault() < 0)
                || (s.StudyInteractions is not null && s.StudyInteractions.GetValueOrDefault() < 0))
            {
                throw BridgeFunctions.Contract("negative anomaly study number");
            }
            if (s.KnowledgeCategory is not null && !BridgeFunctions.IsValidId(s.KnowledgeCategory))
            {
                throw BridgeFunctions.Contract("invalid anomaly study knowledge category");
            }
        }

        // Go validateBuildingAnomaly: bounds a building row's Anomaly block.
        internal static void ValidateBuildingAnomaly(PbObservations.AnomalyBuilding? b)
        {
            if (b is null)
            {
                return;
            }
            if (b.Holder is { } h)
            {
                if (h.ContainmentStrength is { } strength && (double.IsNaN(strength) || double.IsInfinity(strength) || strength < 0))
                {
                    throw BridgeFunctions.Contract("invalid anomaly holder containment strength");
                }
                if (h.HeldPawn is not null && !BridgeFunctions.ValidRef(h.HeldPawn))
                {
                    throw BridgeFunctions.Contract("invalid anomaly held pawn");
                }
                foreach (var door in h.Doors)
                {
                    try
                    {
                        ValidCell(door.Cell);
                    }
                    catch (BridgeContractException error)
                    {
                        throw BridgeFunctions.Contract($"anomaly holder door: {error.Message}");
                    }
                }
            }
            ValidateStudyState(b.Study);
            BridgeFunctions.PawnsIssues(b.Issues, b);
        }

        // Go validateBuildingOdyssey: bounds a building row's Odyssey block.
        internal static void ValidateBuildingOdyssey(PbObservations.OdysseyBuilding? b)
        {
            if (b is null)
            {
                return;
            }
            if (b.Hackable is { } h)
            {
                foreach (var value in new[] { h.ProgressPercent, h.Defence })
                {
                    if (value is { } v && !double.IsFinite(v))
                    {
                        throw BridgeFunctions.Contract("nonfinite odyssey hackable number");
                    }
                }
                if ((h.ProgressPercent is { } progress && (progress < 0 || progress > 1)) || (h.Defence is { } defence && defence < 0))
                {
                    throw BridgeFunctions.Contract("odyssey hack number out of range");
                }
            }
            if (b.Portal is { } p)
            {
                foreach (var id in new[] { p.StockpileType, p.Layout })
                {
                    if (id is not null && !BridgeFunctions.IsValidId(id))
                    {
                        throw BridgeFunctions.Contract("invalid odyssey portal name");
                    }
                }
                if (p.PocketMapId is not null && (p.PocketMapExists is null || !p.PocketMapExists.GetValueOrDefault() || p.PocketMapId.GetValueOrDefault() < 0))
                {
                    throw BridgeFunctions.Contract("odyssey pocket map id without a pocket map");
                }
            }
            BridgeFunctions.PawnsIssues(b.Issues, b);
        }

        /// <summary>Go's <c>validateConstructionRow</c>: one built player building's identity, geometry and issue checks.</summary>
        internal static void ValidateConstructionRow(PbObservations.BuildingState row, PbCommon.ObservationContext? context)
        {
            var e = row.Building;
            if (e is null
                || e.DefName is null
                || e.MapId is null
                || e.Position is null
                || !Passes(() => Entity(e, context))
                || row.Status != PbObservations.BuildingStatus.Built
                || row.Rotation is null
                || (row.Stuff is not null && !BridgeFunctions.IsValidId(row.Stuff)))
            {
                throw BridgeFunctions.Contract("invalid building identity or geometry");
            }
            switch (row.Rotation.GetValueOrDefault())
            {
                case PbPlacement.Rotation.North:
                case PbPlacement.Rotation.East:
                case PbPlacement.Rotation.South:
                case PbPlacement.Rotation.West:
                    break;
                default:
                    throw BridgeFunctions.Contract("invalid building rotation");
            }
            ValidateBuildingOdyssey(row.Odyssey);
            ValidateBuildingAnomaly(row.Anomaly);
            BridgeFunctions.PawnsIssues(row.Issues, row);
        }

        /// <summary>
        /// Go's <c>ValidateConstructionBuildings</c>: only exact identity, status and geometry fields are consumed for
        /// ownership; other typed building details do not supply upkeep authority.
        /// </summary>
        internal static void ValidateConstructionBuildings(PbObservations.BuildingsSnapshot? value, PbCommon.Identity? identity, IReadOnlyList<string> ids)
        {
            if (value is null || !Passes(() => BridgeFunctions.ValidateContext(value.Context)) || !BridgeFunctions.SameIdentity(value.Context!.Identity, identity) || ids.Count > 256)
            {
                throw BridgeFunctions.Contract("invalid building query context");
            }
            var requested = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!BridgeFunctions.IsValidId(id) || !requested.Add(id))
                {
                    throw BridgeFunctions.Contract("invalid requested building");
                }
            }
            if (ids.Count > 0 && value.Buildings.Count > ids.Count)
            {
                throw BridgeFunctions.Contract("incomplete building query");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Buildings)
            {
                if (row is null || row.Building is null || (ids.Count > 0 && !requested.Contains(row.Building.Id ?? "")) || !seen.Add(row.Building.Id ?? ""))
                {
                    throw BridgeFunctions.Contract("unexpected building");
                }
                ValidateConstructionRow(row, value.Context);
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
            BridgeFunctions.ValidateContext(value.Context);
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

        /// <summary>The exact-or-census building list request of Go's <c>constructionBuildingsRequest</c> (built, player, artificial).</summary>
        internal static PbObservations.ListBuildingsRequest ConstructionBuildingsRequest(PbCommon.Identity identity, IReadOnlyList<string> ids) =>
            new()
            {
                Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
                Ids = [.. ids],
                Statuses = ["built"],
                PlayerOnly = true,
                Category = "artificial",
            };

        /// <summary>Go's <c>zoneSectionRequest</c>: the zone census read.</summary>
        internal static PbObservations.ListZonesRequest ZoneSectionRequest(PbCommon.Identity identity) =>
            new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
    }
}
