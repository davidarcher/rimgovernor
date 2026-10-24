using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using EquatableArray = RimGovernor.Brain.Domain.EquatableArray;

namespace RimGovernor.Brain.BuildingRuntime;

// ReadAllowSupplies, ReadForbidSupplies and ReadFoodReserveSupplies: the supply census reads that locate loose weapons and loose supply items.
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Observes loose weapons within one rectangle, forbidden or not: equipping a
    /// forbidden weapon is simply refused downstream by the native preview, not filtered out here.
    /// </summary>
    internal async Task<EquipRead> ReadEquipWeaponsAsync(PbCommon.Identity identity, DomainCell minimum, DomainCell maximum, CancellationToken cancellationToken)
    {
        if (minimum.X < 0 || minimum.Z < 0 || maximum.X < minimum.X || maximum.Z < minimum.Z)
        {
            throw BridgeContractException.Contract("invalid equip scope");
        }
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter
            {
                Category = PbObservations.StockCategory.Weapons,
                Ownership = PbObservations.StockOwnership.Ours,
                IncludeHeld = false,
                Region = new PbObservations.Rectangle
                {
                    Minimum = new PbCommon.Cell { X = minimum.X, Z = minimum.Z },
                    Maximum = new PbCommon.Cell { X = maximum.X, Z = maximum.Z },
                },
            },
        };
        return SupplyDecodeEquipWeapons(await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The loose forbidden supply items at one cell, as allow targets.</summary>
    internal Task<SupplyRead> ReadAllowSuppliesAsync(PbCommon.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, false, cancellationToken);

    /// <summary>The loose unforbidden supply items at one cell, as forbid targets.</summary>
    internal Task<SupplyRead> ReadForbidSuppliesAsync(PbCommon.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, true, cancellationToken);

    /// <summary>
    /// Locates durable shared food through the existing supply census; the
    /// food forecast intentionally carries no item positions.
    /// </summary>
    internal async Task<SupplyRead> ReadFoodReserveSuppliesAsync(PbCommon.Identity identity, bool forbid, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListSuppliesRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.StockFilter
            {
                DefNames = EquatableArray.From(FoodReserveRules.ReserveFoodDefinitions()),
                Category = PbObservations.StockCategory.Haulable,
                Ownership = PbObservations.StockOwnership.Ours,
                IncludeHeld = false,
                ForbiddenOnly = !forbid,
            },
        };
        return SupplyDecodeAccess(await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false), forbid);
    }

    private async Task<SupplyRead> SupplyReadAccessAsync(PbCommon.Identity identity, DomainCell cell, bool forbid, CancellationToken cancellationToken)
    {
        if (cell.X < 0 || cell.Z < 0)
        {
            throw BridgeContractException.Contract("invalid supply scope");
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
        return SupplyDecodeAccess(await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false), forbid);
    }

    private static EquipRead SupplyDecodeEquipWeapons(PbObservations.SuppliesSnapshot v)
    {
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
                var id = item.Id;
                owners.TryGetValue(id, out var weapon);
                // An item with no Allow snapshot is an ineligible loose supply.
                if (item.Snapshot is null)
                {
                    continue;
                }
                targets.Add(
                    new EquipCandidate
                    {
                        Thing = id,
                        Definition = stock.DefName,
                        Cell = item.Cell,
                        Token = item.Snapshot.Token ?? "",
                        BiocodedTo = new RimGovernor.Brain.Domain.PawnId(weapon?.BiocodedTo ?? ""),
                        Biocoded = weapon?.Biocoded ?? false,
                    }
                );
            }
        }
        return new EquipRead { Context = v.Context, Targets = targets };
    }

    /// <summary>The stocks' items with a CAS token, as allow or forbid targets.</summary>
    private static SupplyRead SupplyDecodeAccess(PbObservations.SuppliesSnapshot v, bool forbid)
    {
        var targets = new List<SupplyTarget>();
        foreach (var stock in v.Stocks)
        {
            var def = stock.DefName;
            foreach (var item in stock.Items)
            {
                // An item with no Allow snapshot is an ineligible loose supply.
                if (item.Snapshot is null)
                {
                    continue;
                }
                var supply = forbid ? RimGovernor.Brain.Domain.SupplyAllow.NewSupplyForbid(item.Id, def, item.Cell) : new RimGovernor.Brain.Domain.SupplyAllow(item.Id, def, item.Cell);
                targets.Add(new SupplyTarget { Supply = supply });
            }
        }
        return new SupplyRead { Context = v.Context, Targets = targets };
    }
}
