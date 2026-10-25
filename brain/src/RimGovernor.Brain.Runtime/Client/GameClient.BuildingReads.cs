using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Runtime;

// The zone census and one zone,
// the single-target building reads, the construction census and the wall upgrade sites.
public sealed partial class GameClient
{
    /// <summary>Reads the whole zone census, in id order. A failure throws and never replaces a held census.</summary>
    internal Task<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> ReadZoneSectionAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        Facade.ListZonesAsync(PlacementChecks.ZoneSectionRequest(identity), cancellationToken);

    /// <summary>
    /// Reads one exact zone by id; null when no such zone stands, so an edit's observer can tell a deleted zone
    /// from a failed read (which throws).
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.ZoneState?> ReadZoneAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, string zone, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.ListZonesRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity }, Ids = [zone] };
        var census = await Facade.ListZonesAsync(request, cancellationToken).ConfigureAwait(false);
        return census.Zones.Count == 0 ? null : census.Zones[0];
    }

    /// <summary>
    /// Requests built artificial player-faction buildings. Empty
    /// <paramref name="ids"/> requests the complete bounded colony census; nonempty ids refresh exact targets.
    /// Native faction filtering supplies planning ownership, not lineage. A refused or unreadable census throws.
    /// </summary>
    internal Task<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot> ReadConstructionBuildingsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
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
    internal async Task<StorageBuildingTarget> ReadStorageBuildingTargetAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Ids = [thing],
            Statuses = [RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built],
        };
        var v = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        return new StorageBuildingTarget { Context = v.Context, Present = v.Buildings.Count != 0 };
    }

    /// <summary>Observes one exact bed's medical flag, owners and CAS token via the existing building lookup.</summary>
    internal async Task<BedUseTarget> ReadBedUseTargetAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var v = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
        if (v.Buildings.Count != 1)
        {
            throw BridgeContractException.Contract("bed medical target missing or ambiguous");
        }
        if (v.Buildings[0].Settings.Control is not RimGovernor.Brain.GameFacts.Observations.BuildingControl.Bed)
        {
            throw BridgeContractException.Contract("building is not a bed");
        }
        return new BedUseTarget
        {
            Context = v.Context,
            Observed = v.Buildings[0],
        };
    }

    /// <summary>Observes one exact grower's crop via the existing building lookup; the crop is empty when the grower has none.</summary>
    internal async Task<GrowerCropTarget> ReadGrowerCropTargetAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, string thing, CancellationToken cancellationToken)
    {
        var v = await ReadConstructionBuildingsAsync(identity, [thing], cancellationToken).ConfigureAwait(false);
        if (v.Buildings.Count != 1)
        {
            throw BridgeContractException.Contract("grower crop target missing or ambiguous");
        }
        if (v.Buildings[0].Settings.Control is not RimGovernor.Brain.GameFacts.Observations.BuildingControl.Grower)
        {
            throw BridgeContractException.Contract("building is not a plant grower");
        }
        return new GrowerCropTarget { Context = v.Context, Observed = v.Buildings[0] };
    }

    /// <summary>
    /// Reads every player blueprint and frame on the map and sums their
    /// outstanding material needs by definition. Completeness is a precondition: this read refuses a partially
    /// unreadable census rather than under-reporting a commitment.
    /// </summary>
    internal async Task<ConstructionDeficitRead> ReadConstructionDeficitsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Statuses = [RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint, RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Frame],
        };
        var observed = await Facade.ListBuildingsAsync(request, cancellationToken).ConfigureAwait(false);
        var stillNeed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in observed.Buildings)
        {
            if (row.Construction is not RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build build)
            {
                continue;
            }
            foreach (var need in build.Resources)
            {
                stillNeed[need.DefName] = stillNeed.GetValueOrDefault(need.DefName) + need.StillNeeded;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ConstructionDeficitRead { Context = observed.Context, StillNeed = stillNeed };
    }

    /// <summary>
    /// Lists current wall-upgrade candidate sites, optionally scoped to one original wall's identity. Throws when the
    /// native refuses the request (no colonist wall with that id) or cannot read the census.
    /// </summary>
    internal Task<RimGovernor.Brain.GameFacts.Observations.WallUpgradeSnapshot> ReadWallUpgradeSitesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, string targetId, CancellationToken cancellationToken) =>
        Facade.ListWallUpgradeSitesAsync(new RimGovernor.Brain.GameOrders.Observations.WallUpgradeSitesRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            TargetId = targetId.Length != 0 ? targetId : null,
        }, cancellationToken);
}
