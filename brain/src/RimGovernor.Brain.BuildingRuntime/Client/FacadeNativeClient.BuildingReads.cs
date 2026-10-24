using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

// The zone census and one zone,
// the single-target building reads, the construction census and the wall upgrade sites.
public sealed partial class FacadeNativeClient
{
    /// <summary>Reads the whole zone census, in id order. A failure throws and never replaces a held census.</summary>
    internal Task<PbObservations.ZonesSnapshot> ReadZoneSectionAsync(PbCommon.Identity identity, CancellationToken cancellationToken) =>
        Facade.ListZonesAsync(PlacementChecks.ZoneSectionRequest(identity), cancellationToken);

    /// <summary>
    /// Reads one exact zone by id; null when no such zone stands, so an edit's observer can tell a deleted zone
    /// from a failed read (which throws).
    /// </summary>
    internal async Task<PbObservations.ZoneState?> ReadZoneAsync(PbCommon.Identity identity, string zone, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListZonesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Ids = [zone] };
        var census = await Facade.ListZonesAsync(request, cancellationToken).ConfigureAwait(false);
        return census.Zones.Count == 0 ? null : census.Zones[0];
    }

    /// <summary>
    /// Requests built artificial player-faction buildings. Empty
    /// <paramref name="ids"/> requests the complete bounded colony census; nonempty ids refresh exact targets.
    /// Native faction filtering supplies planning ownership, not lineage. A refused or unreadable census throws.
    /// </summary>
    internal Task<PbObservations.BuildingsSnapshot> ReadConstructionBuildingsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
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
        return Facade.ListBuildingsAsync(PlacementChecks.ConstructionBuildingsRequest(identity, ids), cancellationToken);
    }

    /// <summary>
    /// Observes one exact storage building's presence via the building listing;
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
        var v = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        return new StorageBuildingTarget { Context = v.Context, Thing = thing, Present = v.Buildings.Count != 0 };
    }

    /// <summary>Observes one exact bed's medical flag, owners and CAS token via the existing building lookup.</summary>
    internal async Task<BedUseTarget> ReadBedUseTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var v = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
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

    /// <summary>Observes one exact grower's crop via the existing building lookup; the crop is empty when the grower has none.</summary>
    internal async Task<GrowerCropTarget> ReadGrowerCropTargetAsync(PbCommon.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var v = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
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
    /// Reads every player blueprint and frame on the map and sums their
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
        var observed = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        var stillNeed = new Dictionary<string, long>(StringComparer.Ordinal);
        var structures = 0;
        foreach (var row in observed.Buildings)
        {
            structures++;
            if (row.Construction is not PbObservations.ConstructionState.Build build)
            {
                continue;
            }
            foreach (var need in build.Resources)
            {
                stillNeed[need.DefName] = stillNeed.GetValueOrDefault(need.DefName) + need.StillNeeded;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ConstructionDeficitRead { Context = observed.Context, StillNeed = stillNeed, Structures = structures };
    }

    /// <summary>
    /// Lists current wall-upgrade candidate sites, optionally scoped to one original wall's identity. Throws when the
    /// native refuses the request (no colonist wall with that id) or cannot read the census.
    /// </summary>
    internal Task<PbObservations.WallUpgradeSnapshot> ReadWallUpgradeSitesAsync(PbCommon.Identity identity, string targetId, CancellationToken cancellationToken) =>
        Facade.ListWallUpgradeSitesAsync(new PbObservations.WallUpgradeSitesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            TargetId = targetId.Length != 0 ? targetId : null,
        }, cancellationToken);
}
