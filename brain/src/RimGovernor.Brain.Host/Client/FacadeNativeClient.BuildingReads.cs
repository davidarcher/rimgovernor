using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

// Go zones.go, zone_delete_target.go, storage_building.go, bed_use_target.go, claim_building_target.go,
// grower_crop_target.go, construction_buildings.go, construction_deficit.go and wall_upgrade_sites.go: the zone census,
// the single-target building reads, the construction census and the wall upgrade sites.
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go's <c>ReadZoneSection</c>: reads the whole zone census. A failure never silently replaces a held census.</summary>
    internal async Task<(ZonesRead Read, BridgeResult Receipt)> ReadZoneSectionAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        var reply = await Facade.ListZonesAsync(PlacementReads.ZoneSectionRequest(identity), cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        return (PlacementReads.DecodeZones(reply.Outcome?.Observed, identity), NoReceipt);
    }

    /// <summary>
    /// Go's <c>ReadZoneDeleteTarget</c>: observes one exact zone's presence via the zone listing; an absent zone reads
    /// back as not present rather than as an error, so a deletion's observer can tell success from a stale read.
    /// </summary>
    internal async Task<(ZoneDeleteTarget Target, BridgeResult Receipt)> ReadZoneDeleteTargetAsync(PbCommon.Identity identity, string zone, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (!BridgeFunctions.IsValidId(zone))
        {
            throw BridgeFunctions.Contract("invalid zone delete target identity");
        }
        var request = new PbObservations.ListZonesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Ids = [zone] };
        var reply = await Facade.ListZonesAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Observed is not { } v)
        {
            throw BridgeFunctions.Contract("zone read outcome missing");
        }
        PlacementReads.ValidateZonePage(v, identity);
        if (v.Zones.Count > 1)
        {
            throw BridgeFunctions.Contract("zone delete target ambiguous");
        }
        var output = new ZoneDeleteTarget { Context = v.Context, Zone = zone };
        if (v.Zones.Count == 0)
        {
            return (output, NoReceipt);
        }
        var row = v.Zones[0];
        if ((row.Id ?? "") != zone)
        {
            throw BridgeFunctions.Contract("zone delete target identity mismatch");
        }
        output = output with { Present = true, Type = row.Type ?? "" };
        if (row.Farm is not null)
        {
            output = output with { Planted = row.Farm.PlantedCells.GetValueOrDefault() };
        }
        return (output, NoReceipt);
    }

    /// <summary>
    /// Go's <c>ReadConstructionBuildings</c>: requests built artificial player-faction buildings. Empty
    /// <paramref name="ids"/> requests the complete bounded colony census; nonempty ids refresh exact targets.
    /// Native faction filtering supplies planning ownership, not lineage.
    /// </summary>
    internal async Task<(PbObservations.ListBuildingsReply Reply, BridgeResult Receipt)> ReadConstructionBuildingsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (ids.Count > 256)
        {
            throw BridgeFunctions.Contract("building IDs exceed 256");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!BridgeFunctions.IsValidId(id) || !seen.Add(id))
            {
                throw BridgeFunctions.Contract("invalid requested building identity");
            }
        }
        var request = PlacementReads.ConstructionBuildingsRequest(identity, ids);
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        PlacementReads.UnwrapBuildings(reply, identity, request.Ids, "building read outcome missing");
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go's <c>ReadStorageBuildingTarget</c>: observes one exact storage building's presence via the building listing;
    /// an absent building reads back as not present rather than as an error.
    /// </summary>
    internal async Task<(StorageBuildingTarget Target, BridgeResult Receipt)> ReadStorageBuildingTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (!BridgeFunctions.IsValidId(thing))
        {
            throw BridgeFunctions.Contract("invalid storage building identity");
        }
        var request = new PbObservations.ListBuildingsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Ids = [thing],
            Statuses = ["built"],
        };
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        var v = PlacementReads.UnwrapBuildings(reply, identity, request.Ids, "building read outcome missing");
        var output = new StorageBuildingTarget { Context = v.Context, Thing = thing };
        if (v.Buildings.Count == 0)
        {
            return (output, NoReceipt);
        }
        var row = v.Buildings[0];
        if (v.Buildings.Count != 1 || (row.Building?.Id ?? "") != thing || row.Settings is null)
        {
            throw BridgeFunctions.Contract("storage building target missing settings");
        }
        return (output with { Present = true }, NoReceipt);
    }

    /// <summary>Go's <c>ReadBedUseTarget</c>: observes one exact bed's medical flag, owners and CAS token via the existing building lookup.</summary>
    internal async Task<(BedUseTarget Target, BridgeResult Receipt)> ReadBedUseTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        if (!BridgeFunctions.IsValidId(thing))
        {
            throw BridgeFunctions.Contract("invalid bed medical target identity");
        }
        var (reply, _) = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
        var v = reply.Outcome?.Observed;
        if (v is null || v.Buildings.Count != 1)
        {
            throw BridgeFunctions.Contract("bed medical target missing or ambiguous");
        }
        var row = v.Buildings[0];
        if (row.Building is null || (row.Building.Id ?? "") != thing)
        {
            throw BridgeFunctions.Contract("bed medical target identity mismatch");
        }
        var settings = row.Settings;
        if (settings is null || settings.Snapshot is null || (settings.Snapshot.EntityId ?? "") != thing || settings.Medical is null || settings.TargetTemperatureC is not null)
        {
            throw BridgeFunctions.Contract("building is not a bed");
        }
        return (
            new BedUseTarget
            {
                Context = v.Context,
                Thing = thing,
                Medical = settings.Medical.GetValueOrDefault(),
                Prisoners = settings.ForPrisoners.GetValueOrDefault(),
                Owners = BridgeFunctions.RefIds(settings.AssignedPawns),
            },
            NoReceipt);
    }

    /// <summary>
    /// Go's <c>ReadClaimBuildingTarget</c>: observes one exact claimable building's faction and CAS token via the
    /// building listing, read without the player-only filter: the target is by definition not the player's yet.
    /// </summary>
    internal async Task<(ClaimBuildingTarget Target, BridgeResult Receipt)> ReadClaimBuildingTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (!BridgeFunctions.IsValidId(thing))
        {
            throw BridgeFunctions.Contract("invalid claim building target identity");
        }
        var request = new PbObservations.ListBuildingsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Ids = [thing],
            Statuses = ["built"],
            PlayerOnly = false,
            Category = "artificial",
        };
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        var v = PlacementReads.UnwrapBuildings(reply, identity, request.Ids, "building read outcome missing");
        if (v.Buildings.Count != 1)
        {
            throw BridgeFunctions.Contract("claim building target missing or ambiguous");
        }
        var row = v.Buildings[0];
        if (row.Building is null || (row.Building.Id ?? "") != thing)
        {
            throw BridgeFunctions.Contract("claim building target identity mismatch");
        }
        var settings = row.Settings;
        if (settings is null
            || settings.Snapshot is null
            || (settings.Snapshot.EntityId ?? "") != thing
            || settings.PlayerOwned is null
            || settings.Medical is not null
            || settings.TargetTemperatureC is not null
            || settings.CropDefName is not null)
        {
            throw BridgeFunctions.Contract("building is not claimable");
        }
        return (
            new ClaimBuildingTarget { Context = v.Context, Thing = thing, Token = settings.Snapshot.Token ?? "", PlayerOwned = settings.PlayerOwned.GetValueOrDefault() },
            NoReceipt);
    }

    /// <summary>Go's <c>ReadGrowerCropTarget</c>: observes one exact grower's crop via the existing building lookup; the crop is empty when the grower has none.</summary>
    internal async Task<(GrowerCropTarget Target, BridgeResult Receipt)> ReadGrowerCropTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        if (!BridgeFunctions.IsValidId(thing))
        {
            throw BridgeFunctions.Contract("invalid grower crop target identity");
        }
        var (reply, _) = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
        var v = reply.Outcome?.Observed;
        if (v is null || v.Buildings.Count != 1)
        {
            throw BridgeFunctions.Contract("grower crop target missing or ambiguous");
        }
        var row = v.Buildings[0];
        if (row.Building is null || (row.Building.Id ?? "") != thing)
        {
            throw BridgeFunctions.Contract("grower crop target identity mismatch");
        }
        var settings = row.Settings;
        if (settings is null
            || settings.Snapshot is null
            || (settings.Snapshot.EntityId ?? "") != thing
            || settings.Medical is not null
            || settings.TargetTemperatureC is not null
            || (settings.CropDefName is not null && !BridgeFunctions.IsValidId(settings.CropDefName)))
        {
            throw BridgeFunctions.Contract("building is not a plant grower");
        }
        return (new GrowerCropTarget { Context = v.Context, Thing = thing, Crop = settings.CropDefName ?? "" }, NoReceipt);
    }

    /// <summary>
    /// Go's <c>ReadConstructionDeficits</c>: reads every player blueprint and frame on the map and sums their
    /// outstanding material needs by definition. Completeness is a precondition: this read refuses a partially
    /// unreadable census rather than under-reporting a commitment.
    /// </summary>
    internal async Task<(ConstructionDeficitRead Read, BridgeResult Receipt)> ReadConstructionDeficitsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        var request = new PbObservations.ListBuildingsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Statuses = ["blueprint", "frame"],
            PlayerOnly = true,
        };
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Observed is not { } observed)
        {
            throw BridgeFunctions.Contract("construction deficit outcome missing");
        }
        PlacementReads.BuildingContext(observed.Context, identity, 0, false);
        var stillNeed = new Dictionary<string, long>(StringComparer.Ordinal);
        var structures = 0;
        foreach (var row in observed.Buildings)
        {
            if (row is null || row.Building is null || !BridgeFunctions.IsValidId(row.Building.Id ?? ""))
            {
                throw BridgeFunctions.Contract("invalid construction deficit row");
            }
            if (row.Status != PbObservations.BuildingStatus.Blueprint && row.Status != PbObservations.BuildingStatus.Frame)
            {
                throw BridgeFunctions.Contract("unexpected construction deficit status");
            }
            structures++;
            foreach (var need in row.Construction?.Resources ?? [])
            {
                if (need is null || !BridgeFunctions.IsValidId(need.DefName ?? ""))
                {
                    throw BridgeFunctions.Contract("invalid construction material deficit");
                }

                // An absent still_needed is not zero: it is unknown, and silently treating it as satisfied would
                // under-protect the material.
                if (need.StillNeeded is null || need.StillNeeded.GetValueOrDefault() < 0)
                {
                    throw BridgeFunctions.Contract("construction material deficit unknown");
                }
                var name = need.DefName ?? "";
                stillNeed[name] = stillNeed.GetValueOrDefault(name) + need.StillNeeded.GetValueOrDefault();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (new ConstructionDeficitRead { Context = observed.Context, StillNeed = stillNeed, Structures = structures }, NoReceipt);
    }

    /// <summary>Go's <c>ReadWallUpgradeSites</c>: lists current wall-upgrade candidate sites, optionally scoped to one original wall's identity.</summary>
    internal async Task<(WallUpgradeSites Sites, BridgeResult Receipt)> ReadWallUpgradeSitesAsync(PbCommon.Identity identity, string targetId, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        if (targetId.Length != 0 && !BridgeFunctions.IsValidId(targetId))
        {
            throw BridgeFunctions.Contract("invalid wall upgrade target identity");
        }
        var request = new PbObservations.WallUpgradeSitesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        if (targetId.Length != 0)
        {
            request = request with { TargetId = targetId };
        }
        var reply = await Facade.ListWallUpgradeSitesAsync(request, cancellationToken).ConfigureAwait(false);
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Observed is not { } observed)
        {
            throw BridgeFunctions.Contract("wall upgrade sites outcome missing");
        }
        var buildings = await PlacementFrameBuildingsAsync(identity, cancellationToken).ConfigureAwait(false);
        var sites = PlacementReads.ValidateWallUpgradeSites(observed, identity, buildings);
        return (new WallUpgradeSites { Context = observed.Context, Sites = sites }, NoReceipt);
    }

    // Go FrameBuildings, the no-stream path: the player building table read through the building list. The frame
    // stream's copy of the table is not consulted here; this is the same list the frames carry.
    private async Task<Buildings> PlacementFrameBuildingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        var request = new PbObservations.ListBuildingsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            PlayerOnly = true,
            Category = "artificial",
        };
        var reply = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var observed = reply.Outcome?.Observed;
        if (observed is null || !PlacementReads.Passes(() => BridgeFunctions.ValidateContext(observed.Context)) || !BridgeFunctions.SameIdentity(observed.Context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("building table without the expected context");
        }
        return Buildings.FromSnapshot(observed);
    }

    internal static partial class PlacementReads
    {
        /// <summary>Go's <c>decodeZones</c>: validates and decodes a complete zone census, rows in id order.</summary>
        internal static ZonesRead DecodeZones(PbObservations.ZonesSnapshot? value, PbCommon.Identity? identity)
        {
            ValidateZonePage(value, identity);
            var rows = new List<PbObservations.ZoneState>(value!.Zones.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in value.Zones)
            {
                if (!seen.Add(row.Id ?? ""))
                {
                    throw BridgeFunctions.Contract("duplicate zone");
                }
                rows.Add(row);
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.Id ?? "", b.Id ?? ""));
            return new ZonesRead(value.Context, rows, value.Context!.Tick.GetValueOrDefault());
        }

        /// <summary>
        /// The building list reply's outcome switch shared by the single-target reads: a failure or unavailable arm throws
        /// its typed refusal, a missing outcome is a contract error, an observed snapshot is checked against the built
        /// census rules (Go <c>ValidateConstructionBuildings</c>) and returned.
        /// </summary>
        internal static PbObservations.BuildingsSnapshot UnwrapBuildings(PbObservations.ListBuildingsReply reply, PbCommon.Identity identity, IReadOnlyList<string> ids, string missing)
        {
            if (reply.Outcome?.Failure is { } failure)
            {
                throw Failure(failure);
            }
            if (reply.Outcome?.Unavailable is { } unavailable)
            {
                throw Unavailable(unavailable);
            }
            if (reply.Outcome?.Observed is not { } observed)
            {
                throw BridgeFunctions.Contract(missing);
            }
            ValidateConstructionBuildings(observed, identity, ids);
            return observed;
        }

        /// <summary>Go's <c>validateWallUpgradeSites</c>.</summary>
        internal static IReadOnlyList<WallUpgradeSite> ValidateWallUpgradeSites(PbObservations.WallUpgradeSnapshot? value, PbCommon.Identity identity, Buildings buildings)
        {
            if (value is null || !Passes(() => BridgeFunctions.ValidateContext(value.Context)) || !BridgeFunctions.SameIdentity(value.Context!.Identity, identity))
            {
                throw BridgeFunctions.Contract("invalid wall upgrade sites context");
            }
            var rows = new List<WallUpgradeSite>(value.Sites.Count);
            foreach (var row in value.Sites)
            {
                if (row is null || row.Normal is null)
                {
                    throw BridgeFunctions.Contract("invalid wall upgrade site");
                }
                var x = 0;
                var z = 0;

                // The original wall's cell is its building row's.
                if (row.Original is { } reference && buildings.TryRow(reference, out var original) && original.Building?.Position is { } position)
                {
                    (x, z) = (position.X.GetValueOrDefault(), position.Z.GetValueOrDefault());
                }
                var targetId = "";
                var targetPresent = false;
                if (row.Target is not null)
                {
                    targetId = row.Target.Id ?? "";
                    targetPresent = row.TargetPresent.GetValueOrDefault();
                }
                var backups = new List<Cell>(row.BackupCells.Count);
                foreach (var cell in row.BackupCells)
                {
                    if (cell is null)
                    {
                        throw BridgeFunctions.Contract("invalid wall upgrade backup cell");
                    }
                    backups.Add(new Cell(cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault()));
                }
                rows.Add(new WallUpgradeSite
                {
                    TargetId = targetId,
                    TargetPresent = targetPresent,
                    Blocker = row.Blocker ?? "",
                    X = x,
                    Z = z,
                    NX = row.Normal.X.GetValueOrDefault(),
                    NZ = row.Normal.Z.GetValueOrDefault(),
                    BackupCells = backups,
                    LeftSupport = row.LeftSupport is not null,
                    RightSupport = row.RightSupport is not null,
                });
            }
            return rows;
        }
    }
}
