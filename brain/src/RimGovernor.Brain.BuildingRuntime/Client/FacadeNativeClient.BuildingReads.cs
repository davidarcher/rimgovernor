using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

// Go zones.go, zone_delete_target.go, storage_building.go, bed_use_target.go, claim_building_target.go,
// grower_crop_target.go, construction_buildings.go, construction_deficit.go and wall_upgrade_sites.go: the zone census,
// the single-target building reads, the construction census and the wall upgrade sites.
public sealed partial class FacadeNativeClient
{
    /// <summary>Go's <c>ReadZoneSection</c>: reads the whole zone census. A failure never silently replaces a held census.</summary>
    internal async Task<ZonesRead> ReadZoneSectionAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var reply = await Facade.ListZonesAsync(PlacementChecks.ZoneSectionRequest(identity), cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        return BuildingDecode.DecodeZones(reply.Outcome.Observed!);
    }

    /// <summary>
    /// Go's <c>ReadZoneDeleteTarget</c>: observes one exact zone's presence via the zone listing; an absent zone reads
    /// back as not present rather than as an error, so a deletion's observer can tell success from a stale read.
    /// </summary>
    internal async Task<ZoneDeleteTarget> ReadZoneDeleteTargetAsync(PbCommon.Identity identity, string zone, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListZonesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Ids = [zone] };
        var reply = await Facade.ListZonesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var v = reply.Outcome.Observed!;
        var output = new ZoneDeleteTarget { Context = v.Context, Zone = zone };
        if (v.Zones.Count == 0)
        {
            return output;
        }
        var row = v.Zones[0];
        output = output with { Present = true, Type = row.Type ?? "" };
        if (row.Farm is not null)
        {
            output = output with { Planted = row.Farm.PlantedCells.GetValueOrDefault() };
        }
        return output;
    }

    /// <summary>
    /// Go's <c>ReadConstructionBuildings</c>: requests built artificial player-faction buildings. Empty
    /// <paramref name="ids"/> requests the complete bounded colony census; nonempty ids refresh exact targets.
    /// Native faction filtering supplies planning ownership, not lineage.
    /// </summary>
    internal async Task<PbObservations.ListBuildingsReply> ReadConstructionBuildingsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count > 256)
        {
            throw BridgeContractException.Contract("building IDs exceed 256");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw BridgeContractException.Contract("duplicate requested building identity");
            }
        }
        var request = PlacementChecks.ConstructionBuildingsRequest(identity, ids);
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingDecode.UnwrapBuildings(reply);
        return reply;
    }

    /// <summary>
    /// Go's <c>ReadStorageBuildingTarget</c>: observes one exact storage building's presence via the building listing;
    /// an absent building reads back as not present rather than as an error.
    /// </summary>
    internal async Task<StorageBuildingTarget> ReadStorageBuildingTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListBuildingsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Ids = [thing],
            Statuses = [PbObservations.BuildingStatus.Built],
        };
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        var v = BuildingDecode.UnwrapBuildings(reply);
        return new StorageBuildingTarget { Context = v.Context, Thing = thing, Present = v.Buildings.Count != 0 };
    }

    /// <summary>Go's <c>ReadBedUseTarget</c>: observes one exact bed's medical flag, owners and CAS token via the existing building lookup.</summary>
    internal async Task<BedUseTarget> ReadBedUseTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var reply = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
        var v = reply.Outcome.Observed!;
        if (v.Buildings.Count != 1)
        {
            throw BridgeContractException.Contract("bed medical target missing or ambiguous");
        }
        var settings = v.Buildings[0].Settings;
        if (settings.Medical is null)
        {
            throw BridgeContractException.Contract("building is not a bed");
        }
        return new BedUseTarget
        {
            Context = v.Context,
            Thing = thing,
            Medical = settings.Medical.GetValueOrDefault(),
            Prisoners = settings.ForPrisoners.GetValueOrDefault(),
            Owners = FactHelpers.RefIds(settings.AssignedPawns),
        };
    }

    /// <summary>Go's <c>ReadGrowerCropTarget</c>: observes one exact grower's crop via the existing building lookup; the crop is empty when the grower has none.</summary>
    internal async Task<GrowerCropTarget> ReadGrowerCropTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var reply = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
        var v = reply.Outcome.Observed!;
        if (v.Buildings.Count != 1)
        {
            throw BridgeContractException.Contract("grower crop target missing or ambiguous");
        }
        var settings = v.Buildings[0].Settings;
        if (settings.Snapshot is null || settings.Medical is not null || settings.TargetTemperatureC is not null || settings.PlayerOwned is not null)
        {
            throw BridgeContractException.Contract("building is not a plant grower");
        }
        return new GrowerCropTarget { Context = v.Context, Thing = thing, Crop = settings.CropDefName ?? "" };
    }

    /// <summary>
    /// Go's <c>ReadConstructionDeficits</c>: reads every player blueprint and frame on the map and sums their
    /// outstanding material needs by definition. Completeness is a precondition: this read refuses a partially
    /// unreadable census rather than under-reporting a commitment.
    /// </summary>
    internal async Task<ConstructionDeficitRead> ReadConstructionDeficitsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListBuildingsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Statuses = [PbObservations.BuildingStatus.Blueprint, PbObservations.BuildingStatus.Frame],
        };
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        var observed = BuildingDecode.UnwrapBuildings(reply);
        var stillNeed = new Dictionary<string, long>(StringComparer.Ordinal);
        var structures = 0;
        foreach (var row in observed.Buildings)
        {
            structures++;
            foreach (var need in row.Construction?.Resources ?? [])
            {
                stillNeed[need.DefName] = stillNeed.GetValueOrDefault(need.DefName) + need.StillNeeded;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ConstructionDeficitRead { Context = observed.Context, StillNeed = stillNeed, Structures = structures };
    }

    /// <summary>Go's <c>ReadWallUpgradeSites</c>: lists current wall-upgrade candidate sites, optionally scoped to one original wall's identity.</summary>
    internal async Task<WallUpgradeSites> ReadWallUpgradeSitesAsync(PbCommon.Identity identity, string targetId, CancellationToken cancellationToken)
    {
        var request = new PbObservations.WallUpgradeSitesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        if (targetId.Length != 0)
        {
            request = request with { TargetId = targetId };
        }
        var reply = await Facade.ListWallUpgradeSitesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var observed = reply.Outcome.Observed!;
        var buildings = await PlacementFrameBuildingsAsync(identity, cancellationToken).ConfigureAwait(false);
        var sites = BuildingDecode.DecodeWallUpgradeSites(observed, buildings);
        return new WallUpgradeSites { Context = observed.Context, Sites = sites };
    }

    // Go FrameBuildings, the no-stream path: the player building table read through the building list. The frame
    // stream's copy of the table is not consulted here; this is the same list the frames carry.
    private async Task<Buildings> PlacementFrameBuildingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListBuildingsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
        };
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        return Buildings.FromSnapshot(BuildingDecode.UnwrapBuildings(reply));
    }

    internal static class BuildingDecode
    {
        /// <summary>Go's <c>decodeZones</c>: decodes a complete zone census, rows in id order.</summary>
        internal static ZonesRead DecodeZones(PbObservations.ZonesSnapshot value)
        {
            List<PbObservations.ZoneState> rows = [.. value.Zones];
            rows.Sort((a, b) => string.CompareOrdinal(a.Id ?? "", b.Id ?? ""));
            return new ZonesRead(value.Context, rows, value.Context!.Tick);
        }

        /// <summary>
        /// The building list reply's outcome check shared by the building reads: a failure or unavailable arm throws
        /// its typed refusal; otherwise the observed snapshot is returned.
        /// </summary>
        internal static PbObservations.BuildingsSnapshot UnwrapBuildings(PbObservations.ListBuildingsReply reply)
        {
            if (reply.Outcome.Failure is { } failure)
            {
                throw Failure(failure);
            }
            if (reply.Outcome.Unavailable is { } unavailable)
            {
                throw Unavailable(unavailable);
            }
            return reply.Outcome.Observed!;
        }

        /// <summary>Go's <c>validateWallUpgradeSites</c>: decodes the sites, each original wall's cell taken from its building row.</summary>
        internal static IReadOnlyList<WallUpgradeSite> DecodeWallUpgradeSites(PbObservations.WallUpgradeSnapshot value, Buildings buildings)
        {
            var rows = new List<WallUpgradeSite>(value.Sites.Count);
            foreach (var row in value.Sites)
            {
                var x = 0;
                var z = 0;

                // The original wall's cell is its building row's.
                if (buildings.TryRow(row.Original, out var original) && original.Building.Position is { } position)
                {
                    (x, z) = (position.X, position.Z);
                }
                rows.Add(new WallUpgradeSite
                {
                    TargetId = row.Target.Id ?? "",
                    TargetPresent = row.TargetPresent,
                    Blocker = row.Blocker ?? "",
                    X = x,
                    Z = z,
                    NX = row.Normal.X,
                    NZ = row.Normal.Z,
                    BackupCells = [.. row.BackupCells.Select(cell => new Cell(cell.X, cell.Z))],
                });
            }
            return rows;
        }
    }
}
