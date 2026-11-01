using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using ClearanceTarget = RimGovernor.Brain.Policy.ClearanceTarget;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Native transition reads for tests: standing buildings with their uninstall, storage and deconstruct mechanics.</summary>
internal static class TransitionFixtures
{
    /// <summary>The material the fixture buildings are made of, worth one capability-day a unit.</summary>
    internal const string Steel = "Steel";

    /// <summary>A builder 100 ticks away at ConstructionSpeed 1 who uninstalls and deconstructs, 200 ticks from storage, walking 13 ticks a cell.</summary>
    internal static TransitionWorker Builder => new("builder", 100)
    {
        TicksPerCell = 13, Ability = new ServicingAbility.Construction(1), UninstallActive = true, DeconstructActive = true, StoreLeg = new TransitionLeg.Walked(200),
    };

    /// <summary>
    /// A standing building: 200 uninstall work, deconstructible at 200 work recovering half of its 10 steel, with storage accepting
    /// it unless <paramref name="storage"/> is false; it stands outdoors with no beauty or wealth unless <paramref name="room"/> names its room.
    /// </summary>
    internal static TransitionMechanics Mechanics(string id, string def, Cell cell, bool minifiable = true, bool storage = true, bool brokenDown = false, string? room = null, double beauty = 0, double wealth = 0) =>
        new(id, def, cell)
        {
            Beauty = beauty, Wealth = wealth, Room = room is null ? new TransitionRoom.None("outdoors") : new TransitionRoom.Inside(room),
            Minifiable = minifiable, UninstallWork = 200, Deconstructible = true, BrokenDown = brokenDown,
            Removal = new ServicingRemoval.Read([new ServicingCost(Steel, 10)], 0.5, 200),
            Storage = storage ? new TransitionStorage.Accepting(new Cell(0, 0)) : new TransitionStorage.None(ServicingChoiceRules.NoAcceptingStorage),
            Worker = Builder,
        };

    internal static IReadOnlyDictionary<Resource, ConstructionMaterialValue> Values =>
        new Dictionary<Resource, ConstructionMaterialValue> { [new Resource(Steel)] = new(MaterialValueBasis.Market, 1) };

    internal static TransitionFacts Facts(params TransitionMechanics[] mechanics) =>
        ServicingChoiceRules.TransitionFactsOf(CoupledFixtures.NoServicing with { Transitions = [.. mechanics] }, Values);

    /// <summary>Every row a minifiable building storage accepts: a compared removal stores (packs) it.</summary>
    internal static TransitionFacts Packing(IEnumerable<ClearanceTarget> rows) =>
        Facts([.. rows.Select(r => Mechanics(r.EntityId, r.DefName, r.Minimum))]);

    /// <summary>Every row a building that cannot be minified: a compared removal deconstructs it.</summary>
    internal static TransitionFacts Removing(IEnumerable<ClearanceTarget> rows) =>
        Facts([.. rows.Select(r => Mechanics(r.EntityId, r.DefName, r.Minimum, minifiable: false))]);
}
