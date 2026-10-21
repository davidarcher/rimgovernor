using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3046: the remote-pickup rule both delivery paths load by. The brain's Supply sizing (ConstructionMethodCandidates.RemoteLoad,
/// CoordinatedConstructionTests) and native's postfix on vanilla's delivery job (RemotePickupGuard) call it with their own stacks.
/// </summary>
public sealed class RemotePickupTests
{
    // wall/remote_pickup's geometry: the first stack holds 15, 40 cells from the frame; 20 more at 8 cells and 20 at 14 cells.
    private static readonly (string Stack, double Detour, double Available)[] Staged = [("near", 8, 20), ("far", 14, 20)];

    [Fact]
    public void ARemoteLoadCollectsOnlyTheStacksWorthTheirDetour()
    {
        // 8 < 20 * 40 / capacity separates the near stack below 100; 14 < 20 * 40 / capacity keeps the far one below 57.1.
        Assert.Equal(["near"], RemotePickup.Collect(40, 75, 15, Staged));
        Assert.Equal(["near"], RemotePickup.Collect(40, 58, 15, Staged));
        Assert.Equal(["near", "far"], RemotePickup.Collect(40, 57, 15, Staged));
        Assert.Empty(RemotePickup.Collect(40, 100, 15, Staged));
        // A load already full collects nothing; a stack past D/2 is never worth it, however large.
        Assert.Empty(RemotePickup.Collect(40, 75, 75, Staged));
        Assert.Empty(RemotePickup.Collect(40, 75, 15, [("past-half", 21, 75)]));
    }

    [Fact]
    public void ARemoteLoadTakesTheNearestStacksAndAsksUsabilityOnlyOfThoseWorthIt()
    {
        var asked = new List<string>();
        var chosen = RemotePickup.Collect(40, 75, 15, [("far", 14, 20), ("blocked", 2, 20), ("near", 8, 20)], s => { asked.Add(s); return s != "blocked"; });
        Assert.Equal(["near"], chosen);
        Assert.Equal(["blocked", "near"], asked);
        var many = Enumerable.Range(0, 20).Select(i => ("s" + i, 0.1, 1.0)).ToArray();
        Assert.Equal(RemotePickup.MaxCollect, RemotePickup.Collect(40, 75, 15, many).Count);
    }

    [Fact]
    public void ARemoteDeliveryIsFartherFromItsSiteThanFromStorageAndLoadsAFullCarry()
    {
        Assert.True(RemotePickup.IsRemote(40, 0));
        Assert.False(RemotePickup.IsRemote(40, 40));
        Assert.Equal(75, RemotePickup.Load(5, 75, 120));
        Assert.Equal(35, RemotePickup.Load(5, 75, 35));
        Assert.Equal(5, RemotePickup.Load(5, 75, 3));
    }
}
