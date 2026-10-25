using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using EquatableArray = RimGovernor.Brain.Domain.EquatableArray;

namespace RimGovernor.Brain.Runtime;

// ReadAllowSupplies, ReadForbidSupplies and ReadFoodReserveSupplies: the supply census reads that locate loose weapons and loose supply items.
public sealed partial class GameClient
{
    /// <summary>
    /// Observes loose weapons within one rectangle, forbidden or not: equipping a
    /// forbidden weapon is simply refused downstream by the native preview, not filtered out here.
    /// </summary>
    internal async Task<EquipRead> ReadEquipWeaponsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, DomainCell minimum, DomainCell maximum, CancellationToken cancellationToken)
    {
        if (minimum.X < 0 || minimum.Z < 0 || maximum.X < minimum.X || maximum.Z < minimum.Z)
        {
            throw BridgeContractException.Contract("invalid equip scope");
        }
        var request = new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Filter = new RimGovernor.Brain.GameFacts.Observations.StockFilter
            {
                Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Weapons,
                Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours,
                IncludeHeld = false,
                Region = new RimGovernor.Brain.GameFacts.Observations.Rectangle
                {
                    Minimum = new RimGovernor.Brain.Domain.Cell { X = minimum.X, Z = minimum.Z },
                    Maximum = new RimGovernor.Brain.Domain.Cell { X = maximum.X, Z = maximum.Z },
                },
            },
        };
        return SupplyDecodeEquipWeapons(await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The loose forbidden supply items at one cell, as allow targets.</summary>
    internal Task<SupplyRead> ReadAllowSuppliesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, false, cancellationToken);

    /// <summary>The loose unforbidden supply items at one cell, as forbid targets.</summary>
    internal Task<SupplyRead> ReadForbidSuppliesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, true, cancellationToken);

    /// <summary>
    /// Locates durable shared food through the existing supply census; the
    /// food forecast intentionally carries no item positions.
    /// </summary>
    internal async Task<SupplyRead> ReadFoodReserveSuppliesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, bool forbid, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Filter = new RimGovernor.Brain.GameFacts.Observations.StockFilter
            {
                DefNames = EquatableArray.From(FoodReserveRules.ReserveFoodDefinitions()),
                Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable,
                Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours,
                IncludeHeld = false,
                ForbiddenOnly = !forbid,
            },
        };
        return SupplyDecodeAccess(await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false), forbid);
    }

    private async Task<SupplyRead> SupplyReadAccessAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, DomainCell cell, bool forbid, CancellationToken cancellationToken)
    {
        if (cell.X < 0 || cell.Z < 0)
        {
            throw BridgeContractException.Contract("invalid supply scope");
        }
        var request = new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Filter = new RimGovernor.Brain.GameFacts.Observations.StockFilter
            {
                Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable,
                Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours,
                IncludeHeld = false,
                ForbiddenOnly = !forbid,
                Region = new RimGovernor.Brain.GameFacts.Observations.Rectangle
                {
                    Minimum = new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z },
                    Maximum = new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z },
                },
            },
        };
        return SupplyDecodeAccess(await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false), forbid);
    }

    internal static EquipRead SupplyDecodeEquipWeapons(RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot v)
    {
        var targets = new List<EquipCandidate>();
        foreach (var stock in v.Stocks)
        {
            var owners = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.GearItem>(StringComparer.Ordinal);
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
                        Stock = stock,
                        Item = item,
                        Gear = weapon,
                    }
                );
            }
        }
        return new EquipRead { Context = v.Context, Targets = targets };
    }

    /// <summary>The stocks' items with a CAS token, as allow or forbid targets.</summary>
    private static SupplyRead SupplyDecodeAccess(RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot v, bool forbid)
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
