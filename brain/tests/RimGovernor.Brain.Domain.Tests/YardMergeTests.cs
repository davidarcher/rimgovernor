using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using JournalStore = RimGovernor.Brain.Store.Store;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3232: the waste yard merged into the materials yard. The one outdoor store takes the native outdoor_safe_waste
/// preset, and a saved plan that still holds a waste-yard room restores as no plan, so the survey derives a fresh one.
/// </summary>
public sealed class YardMergeTests
{
    private static readonly PlannedRoom Storeroom = new()
    {
        Id = 1,
        Role = PlannedRole.Storage,
        Region = RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 5 }),
        Door = new Cell(12, 9),
        DoorRot = Rotation.South,
    };

    private static readonly PlannedRoom Yard = new()
    {
        Id = 2,
        Role = PlannedRole.Yard,
        Region = RectRegion(new Rectangle { X = 30, Z = 10, Width = 13, Height = 9 }),
        Outdoor = true,
    };

    [Fact]
    public void TheYardStoreUsesTheOutdoorSafeWastePreset()
    {
        var plan = new LayoutPlan { Rooms = [Storeroom, Yard], NextRoomId = 3 };
        var yard = Assert.Single(DeclareStores(new StoreView { Layout = plan }).Stores, s => s.Role == DomainConstants.YardRole);
        var filter = Assert.IsType<StockpileFilter>(yard.Site.Filter);
        Assert.Equal(StockpileFilter.YardFilter(), filter);
        Assert.Equal(FilterBase.OutdoorSafeWaste, filter.Base);
        Assert.Empty(filter.Allow);
        Assert.Empty(filter.Disallow);
        Assert.Equal(Op.FilterPreset.OutdoorSafeWaste, Zones.StockpileSettings(filter, StockpilePriority.Low).Preset);
    }

    [Fact]
    public void ASavedPlanHoldingAWasteYardRoomRestoresAsNoPlan()
    {
        using var lifetime = new CancellationTokenSource();
        var world = new GenerationSnapshot(new ColonyId("colony"), new MapId(1), new LoadId("load"), new PlanId("review"), new PlanRevision(1), default);
        var plan = new LayoutPlan { Rooms = [Storeroom], NextRoomId = 2 };
        Assert.True(plan.Valid());

        using var source = JournalStore.Open(Path.Combine(Path.GetTempPath(), "rg-yard-" + Guid.NewGuid().ToString("N") + ".db"), lifetime.Token);
        source.RecordLayoutPlan(world, new Tick(100), plan, lifetime.Token);
        var blobs = new Dictionary<string, string>(source.GovernorStateBlobs(lifetime.Token));
        var saved = blobs[JournalStore.GovernorLayoutPlanKey];

        using var restored = JournalStore.Open(Path.Combine(Path.GetTempPath(), "rg-yard-" + Guid.NewGuid().ToString("N") + ".db"), lifetime.Token);
        restored.RebuildFamilies(blobs, lifetime.Token);
        var kept = Assert.IsType<LayoutPlan>(restored.LayoutPlan(world, new Tick(100), lifetime.Token)?.Plan);
        Assert.Equal(PlannedRole.Storage, Assert.Single(kept.Rooms).Role);

        // The same plan with its room relabelled to the deleted role.
        var stale = saved.Replace("\"storage\"", "\"waste_yard\"", StringComparison.Ordinal);
        Assert.NotEqual(saved, stale);
        blobs[JournalStore.GovernorLayoutPlanKey] = stale;
        restored.RebuildFamilies(blobs, lifetime.Token);
        Assert.Null(restored.LayoutPlan(world, new Tick(100), lifetime.Token));
    }
}
