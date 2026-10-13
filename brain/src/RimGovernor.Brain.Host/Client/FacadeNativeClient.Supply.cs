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
    internal async Task<(EquipRead Value, BridgeResult Receipt)> ReadEquipWeaponsAsync(PbCommon.Identity identity, DomainCell minimum, DomainCell maximum, CancellationToken cancellationToken)
    {
        if (!SupplyIdentityValid(identity) || minimum.X < 0 || minimum.Z < 0 || maximum.X < minimum.X || maximum.Z < minimum.Z)
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
        return (SupplyDecodeEquipWeapons(reply, identity, minimum, maximum), NoReceipt);
    }

    /// <summary>Go <c>Client.ReadAllowSupplies</c>: the loose forbidden supply items at one cell, as allow targets.</summary>
    internal Task<(SupplyRead Value, BridgeResult Receipt)> ReadAllowSuppliesAsync(PbCommon.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, false, cancellationToken);

    /// <summary>Go <c>Client.ReadForbidSupplies</c>: the loose unforbidden supply items at one cell, as forbid targets.</summary>
    internal Task<(SupplyRead Value, BridgeResult Receipt)> ReadForbidSuppliesAsync(PbCommon.Identity identity, DomainCell cell, CancellationToken cancellationToken) =>
        SupplyReadAccessAsync(identity, cell, true, cancellationToken);

    /// <summary>
    /// Go <c>Client.ReadFoodReserveSupplies</c>: locates durable shared food through the existing supply census; the
    /// food forecast intentionally carries no item positions.
    /// </summary>
    internal async Task<(SupplyRead Value, BridgeResult Receipt)> ReadFoodReserveSuppliesAsync(PbCommon.Identity identity, bool forbid, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
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
        var result = SupplyDecodeAccessAt(reply, identity, null, forbid);
        foreach (var target in result.Targets)
        {
            if (!PolicyFunctions.ReserveFoodDefinition(new Resource(target.Supply.Definition)))
            {
                throw BridgeFunctions.Contract("unexpected reserve definition");
            }
        }
        return (result, NoReceipt);
    }

    /// <summary>Go <c>Client.readSupplyAccess</c>.</summary>
    private async Task<(SupplyRead Value, BridgeResult Receipt)> SupplyReadAccessAsync(PbCommon.Identity identity, DomainCell cell, bool forbid, CancellationToken cancellationToken)
    {
        if (!SupplyIdentityValid(identity) || cell.X < 0 || cell.Z < 0)
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
        return (SupplyDecodeAccessAt(reply, identity, cell, forbid), NoReceipt);
    }

    /// <summary>Go <c>ValidateIdentity(identity) == nil</c>.</summary>
    private static bool SupplyIdentityValid(PbCommon.Identity? identity)
    {
        try
        {
            BridgeFunctions.ValidateIdentity(identity);
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }

    /// <summary>Go <c>buildingContext(value, identity, 0, false) == nil</c>: a valid context of the same identity.</summary>
    private static bool SupplyContextMatches(PbCommon.ObservationContext? context, PbCommon.Identity identity)
    {
        try
        {
            BridgeFunctions.ValidateContext(context);
        }
        catch (BridgeContractException)
        {
            return false;
        }
        return BridgeFunctions.SameIdentity(context!.Identity, identity);
    }

    /// <summary>Go <c>refSnapshot</c>: whether <paramref name="snapshot"/> is the CAS token of <paramref name="reference"/> read under <paramref name="context"/>.</summary>
    private static bool SupplyRefSnapshot(PbObservations.SnapshotRef? snapshot, PbCommon.Ref? reference, PbCommon.ObservationContext? context) =>
        snapshot is not null
        && reference is not null
        && (snapshot.EntityId ?? "") == (reference.Id ?? "")
        && BridgeFunctions.IsValidId(snapshot.Token ?? "")
        && Equals(snapshot.Context, context);

    /// <summary>Go <c>validBiocode</c>: a gear item biocoded to a valid pawn, and never stated as not biocoded.</summary>
    private static bool SupplyValidBiocode(PbObservations.GearItem weapon) =>
        weapon.BiocodedTo is null || (BridgeFunctions.IsValidId(weapon.BiocodedTo) && (weapon.Biocoded is null || weapon.Biocoded.GetValueOrDefault()));

    /// <summary>Go <c>decodeEquipWeapons</c>.</summary>
    private static EquipRead SupplyDecodeEquipWeapons(PbObservations.ListSuppliesReply? reply, PbCommon.Identity identity, DomainCell minimum, DomainCell maximum)
    {
        if (reply is null)
        {
            throw BridgeFunctions.Contract("invalid equip reply");
        }
        var v = reply.Outcome?.Observed;
        if (v is null || !SupplyContextMatches(v.Context, identity))
        {
            throw BridgeFunctions.Contract("equip census unavailable");
        }
        if (v.Completeness is null || v.Completeness.Filtered.GetValueOrDefault() != 0)
        {
            throw BridgeFunctions.Contract("incomplete equip census");
        }
        var targets = new List<EquipCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stock in v.Stocks)
        {
            if (stock is null || stock.Units is null || stock.Units.GetValueOrDefault() < 0)
            {
                throw BridgeFunctions.Contract("invalid equip stock");
            }
            var owners = new Dictionary<string, PbObservations.GearItem>(StringComparer.Ordinal);
            foreach (var weapon in stock.WeaponItems)
            {
                var weaponId = weapon?.Thing?.Id ?? "";
                if (weapon is null || !BridgeFunctions.IsValidId(weaponId) || owners.ContainsKey(weaponId) || !SupplyValidBiocode(weapon))
                {
                    throw BridgeFunctions.Contract("invalid equip biocode");
                }
                owners[weaponId] = weapon;
            }
            if (owners.Count != 0 && owners.Count != stock.Items.Count)
            {
                throw BridgeFunctions.Contract("incomplete equip biocodes");
            }
            foreach (var item in stock.Items)
            {
                var id = item?.Item?.Id ?? "";
                var at = item?.Cell;
                if (item is null
                    || !BridgeFunctions.ValidRef(item.Item)
                    || seen.Contains(id)
                    || at is null
                    || at.X is null
                    || at.Z is null
                    || at.X.GetValueOrDefault() < minimum.X
                    || at.X.GetValueOrDefault() > maximum.X
                    || at.Z.GetValueOrDefault() < minimum.Z
                    || at.Z.GetValueOrDefault() > maximum.Z)
                {
                    throw BridgeFunctions.Contract("equip entity mismatch");
                }
                owners.TryGetValue(id, out var weapon);
                if (owners.Count != 0 && weapon is null)
                {
                    throw BridgeFunctions.Contract("equip biocode identity mismatch");
                }
                seen.Add(id);
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
                        Definition = stock.Definition?.DefName ?? "",
                        Cell = new DomainCell(at.X.GetValueOrDefault(), at.Z.GetValueOrDefault()),
                        Token = item.Snapshot.Token ?? "",
                        BiocodedTo = new RimGovernor.Brain.Domain.PawnId(weapon?.BiocodedTo ?? ""),
                        Biocoded = weapon?.Biocoded.GetValueOrDefault() ?? false,
                    }
                );
            }
        }
        return new EquipRead { Context = v.Context, Targets = targets };
    }

    /// <summary>Go <c>decodeSupplyAccessAt</c> (<c>decodeSupplyAccess</c> with a cell): <paramref name="at"/> null accepts any cell.</summary>
    private static SupplyRead SupplyDecodeAccessAt(PbObservations.ListSuppliesReply? reply, PbCommon.Identity identity, DomainCell? at, bool forbid)
    {
        if (reply is null)
        {
            throw BridgeFunctions.Contract("invalid supply reply");
        }
        var v = reply.Outcome?.Observed;
        if (v is null || !SupplyContextMatches(v.Context, identity))
        {
            throw BridgeFunctions.Contract("supply census unavailable");
        }
        if (v.Completeness is null || v.Completeness.Filtered.GetValueOrDefault() != 0)
        {
            throw BridgeFunctions.Contract("incomplete supply census");
        }
        var targets = new List<SupplyTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stock in v.Stocks)
        {
            if (stock is null
                || stock.Units is null
                || stock.Forbidden is null
                || stock.Units.GetValueOrDefault() < 0
                || stock.Forbidden.GetValueOrDefault() < 0
                || stock.Forbidden.GetValueOrDefault() > stock.Units.GetValueOrDefault()
                || (!forbid && stock.Forbidden.GetValueOrDefault() != stock.Units.GetValueOrDefault()))
            {
                throw BridgeFunctions.Contract("invalid forbidden supply stock");
            }
            var def = stock.Definition?.DefName ?? "";
            foreach (var item in stock.Items)
            {
                var id = item?.Item?.Id ?? "";
                var pos = item?.Cell;
                if (item is null
                    || !BridgeFunctions.ValidRef(item.Item)
                    || seen.Contains(id)
                    || pos is null
                    || pos.X is null
                    || pos.Z is null
                    || pos.X.GetValueOrDefault() < 0
                    || pos.Z.GetValueOrDefault() < 0)
                {
                    throw BridgeFunctions.Contract("supply entity mismatch");
                }
                var cell = new DomainCell(pos.X.GetValueOrDefault(), pos.Z.GetValueOrDefault());
                if (at is { } wanted && cell != wanted)
                {
                    throw BridgeFunctions.Contract("supply cell mismatch");
                }
                seen.Add(id);
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
