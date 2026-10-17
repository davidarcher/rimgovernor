using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using EquatableArray = RimGovernor.Brain.Domain.EquatableArray;

namespace RimGovernor.Brain.Host;

// Go internal/bridge equip_things.go (ReadEquipWeapons) and supply_allow.go (ReadAllowSupplies, ReadForbidSupplies,
// ReadFoodReserveSupplies): the supply census reads that locate loose weapons and loose supply items.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.ReadEquipWeapons</c>: observes loose weapons within one rectangle, forbidden or not: equipping a
    /// forbidden weapon is simply refused downstream by the native preview, not filtered out here.
    /// </summary>
    internal async Task<EquipRead> ReadEquipWeaponsAsync(PbCommon.Identity identity, DomainCell minimum, DomainCell maximum, CancellationToken cancellationToken)
    {
        if (minimum.X < 0 || minimum.Z < 0 || maximum.X < minimum.X || maximum.Z < minimum.Z)
        {
            throw BridgeFunctions.Contract("invalid equip scope");
        }
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter
            {
                Category = PbObservations.StockCategory.Weapons,
                Ownership = PbObservations.StockOwnership.Ours,
                IncludeHeld = false,
                ForbiddenOnly = false,
                Region = new PbObservations.Rectangle
                {
                    Minimum = new PbCommon.Cell { X = minimum.X, Z = minimum.Z },
                    Maximum = new PbCommon.Cell { X = maximum.X, Z = maximum.Z },
                },
            },
        };
        var reply = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply?.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return SupplyDecodeEquipWeapons(reply, identity);
    }

    /// <summary>Go <c>Client.ReadAllowSupplies</c>: the loose forbidden supply items at one cell, as allow targets.</summary>
    internal Task<SupplyRead> ReadAllowSuppliesAsync(PbCommon.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, false, cancellationToken);

    /// <summary>Go <c>Client.ReadForbidSupplies</c>: the loose unforbidden supply items at one cell, as forbid targets.</summary>
    internal Task<SupplyRead> ReadForbidSuppliesAsync(PbCommon.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, true, cancellationToken);

    /// <summary>
    /// Go <c>Client.ReadFoodReserveSupplies</c>: locates durable shared food through the existing supply census; the
    /// food forecast intentionally carries no item positions.
    /// </summary>
    internal async Task<SupplyRead> ReadFoodReserveSuppliesAsync(PbCommon.Identity identity, bool forbid, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter
            {
                DefNames = EquatableArray.From(PolicyFunctions.ReserveFoodDefinitions()),
                Ownership = PbObservations.StockOwnership.Ours,
                IncludeHeld = false,
                ForbiddenOnly = !forbid,
            },
        };
        var reply = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        return SupplyDecodeAccess(reply, identity, forbid);
    }

    /// <summary>Go <c>Client.readSupplyAccess</c>.</summary>
    private async Task<SupplyRead> SupplyReadAccessAsync(PbCommon.Identity identity, DomainCell cell, bool forbid, CancellationToken cancellationToken)
    {
        if (cell.X < 0 || cell.Z < 0)
        {
            throw BridgeFunctions.Contract("invalid supply scope");
        }
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter
            {
                Category = PbObservations.StockCategory.Haulable,
                Ownership = PbObservations.StockOwnership.Ours,
                IncludeHeld = false,
                ForbiddenOnly = !forbid,
                Region = new PbObservations.Rectangle
                {
                    Minimum = new PbCommon.Cell { X = cell.X, Z = cell.Z },
                    Maximum = new PbCommon.Cell { X = cell.X, Z = cell.Z },
                },
            },
        };
        var reply = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply?.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return SupplyDecodeAccess(reply, identity, forbid);
    }

    /// <summary>Go <c>refSnapshot</c>: whether <paramref name="snapshot"/> is the CAS token of <paramref name="reference"/> read under <paramref name="context"/>.</summary>
    private static bool SupplyRefSnapshot(PbObservations.SnapshotRef? snapshot, PbCommon.Ref? reference, PbCommon.ObservationContext? context) =>
        snapshot is not null
        && reference is not null
        && (snapshot.EntityId ?? "") == (reference.Id ?? "")
        && BridgeFunctions.IsValidId(snapshot.Token ?? "")
        && Equals(snapshot.Context, context);

    /// <summary>Go <c>decodeEquipWeapons</c>.</summary>
    private static EquipRead SupplyDecodeEquipWeapons(PbObservations.ListSuppliesReply? reply, PbCommon.Identity identity)
    {
        if (reply is null)
        {
            throw BridgeFunctions.Contract("invalid equip reply");
        }
        var v = reply.Outcome?.Observed;
        if (v is null || !BridgeFunctions.SameIdentity(v.Context?.Identity, identity))
        {
            throw BridgeFunctions.Contract("equip census unavailable");
        }
        if (v.Completeness is null || v.Completeness.Filtered.GetValueOrDefault() != 0)
        {
            throw BridgeFunctions.Contract("incomplete equip census");
        }
        var targets = new List<EquipCandidate>();
        foreach (var stock in v.Stocks)
        {
            var owners = new Dictionary<string, PbObservations.GearItem>(StringComparer.Ordinal);
            foreach (var weapon in stock.WeaponItems)
            {
                owners[weapon.Thing?.Id ?? ""] = weapon;
            }
            foreach (var item in stock.Items)
            {
                var id = item.Item.Id ?? "";
                var at = item.Cell;
                owners.TryGetValue(id, out var weapon);
                // Native issues distinguish ineligible items from an incomplete census.
                if (item.Snapshot is null)
                {
                    continue;
                }
                if (!SupplyRefSnapshot(item.Snapshot, item.Item, v.Context))
                {
                    throw BridgeFunctions.Contract("equip CAS scope mismatch");
                }
                targets.Add(
                    new EquipCandidate
                    {
                        Thing = id,
                        Definition = stock.Definition.DefName ?? "",
                        Cell = new DomainCell(at.X.GetValueOrDefault(), at.Z.GetValueOrDefault()),
                        Token = item.Snapshot.Token ?? "",
                        BiocodedTo = new RimGovernor.Brain.Domain.PawnId(weapon?.BiocodedTo ?? ""),
                        Biocoded = weapon?.Biocoded ?? false,
                    }
                );
            }
        }
        return new EquipRead { Context = v.Context, Targets = targets };
    }

    /// <summary>Go <c>decodeSupplyAccess</c>: the stocks' items with a CAS token, as allow or forbid targets.</summary>
    private static SupplyRead SupplyDecodeAccess(PbObservations.ListSuppliesReply? reply, PbCommon.Identity identity, bool forbid)
    {
        if (reply is null)
        {
            throw BridgeFunctions.Contract("invalid supply reply");
        }
        var v = reply.Outcome?.Observed;
        if (v is null || !BridgeFunctions.SameIdentity(v.Context?.Identity, identity))
        {
            throw BridgeFunctions.Contract("supply census unavailable");
        }
        if (v.Completeness is null || v.Completeness.Filtered.GetValueOrDefault() != 0)
        {
            throw BridgeFunctions.Contract("incomplete supply census");
        }
        var targets = new List<SupplyTarget>();
        foreach (var stock in v.Stocks)
        {
            var def = stock.Definition.DefName ?? "";
            foreach (var item in stock.Items)
            {
                var id = item.Item.Id ?? "";
                var cell = new DomainCell(item.Cell.X.GetValueOrDefault(), item.Cell.Z.GetValueOrDefault());
                // Native issues distinguish ineligible items from an incomplete census.
                if (item.Snapshot is null)
                {
                    continue;
                }
                if (!SupplyRefSnapshot(item.Snapshot, item.Item, v.Context))
                {
                    throw BridgeFunctions.Contract("supply CAS scope mismatch");
                }
                var supply = forbid ? RimGovernor.Brain.Domain.SupplyAllow.NewSupplyForbid(id, def, cell) : new RimGovernor.Brain.Domain.SupplyAllow(id, def, cell);
                targets.Add(new SupplyTarget { Supply = supply });
            }
        }
        return new SupplyRead { Context = v.Context, Targets = targets };
    }
}
