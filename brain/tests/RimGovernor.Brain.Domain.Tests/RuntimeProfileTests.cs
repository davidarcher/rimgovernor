using System.Diagnostics;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RuntimeProfileTests
{
    private sealed class ProfiledAllocation(int n)
    {
        public readonly byte[] Pad = new byte[n];
    }

    [Fact]
    public void DrainNamesTheTypeThatWasAllocated()
    {
        using var profile = new RuntimeProfile();
        var keep = new List<ProfiledAllocation>();
        // The runtime dispatches its events on its own thread a little after they happen; the deadline only
        // guards against a hang.
        var deadline = Stopwatch.StartNew();
        var seen = 0L;
        while (seen == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            for (var i = 0; i < 20_000; i++)
            {
                keep.Add(new ProfiledAllocation(0));
            }
            keep.Clear();
            Thread.Sleep(50);
            seen = profile.Drain().Allocations.Where(a => a.Type.EndsWith(nameof(ProfiledAllocation), StringComparison.Ordinal)).Sum(a => a.Bytes);
        }
        Assert.True(seen > 0, "no allocation tick named " + nameof(ProfiledAllocation));
    }
}
