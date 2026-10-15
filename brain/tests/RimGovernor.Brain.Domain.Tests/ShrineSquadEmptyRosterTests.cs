using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge.Contracts;
using Result = RimGovernor.Brain.Bridge.Contracts.Result;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Issue #2856: an empty colonist roster (all lost, or before spawn) made the colony status read fail with
/// "pawn IDs outside 1..256", because the shrine readiness squad passed zero ids to the id-scoped pawn read.
/// An empty roster is a valid state and has no squad; the read's bound still guards a real id list.
/// </summary>
public sealed class ShrineSquadEmptyRosterTests
{
    private sealed class Native : IShrineReadinessNative
    {
        public int CombatReads;

        public Task<(O.ListPawnsReply Reply, Result Result)> ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> pawns, CancellationToken ct)
        {
            CombatReads++;
            throw new InvalidOperationException("pawn IDs outside 1..256");
        }

        public Task<(DefenseSite Site, Result Result)> ReadDefenseSiteAsync(C.Identity identity, CellRect region, CancellationToken ct) => throw new NotSupportedException();

        public Task<(EmergencyObservation Observed, Result Result)> ReadEmergencyAsync(C.Identity identity, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task EmptyRosterHasNoSquadAndSkipsTheIdScopedRead()
    {
        var native = new Native();

        var squad = await BuildingRuntimePackage.ShrineSquadAsync(native, new C.Identity { ColonyId = "c", LoadToken = "l", MapId = 1 }, [], null, CancellationToken.None);

        Assert.Empty(squad);
        Assert.Equal(0, native.CombatReads);
    }

    [Fact]
    public async Task NonEmptyRosterStillReadsTheIds()
    {
        var native = new Native();

        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildingRuntimePackage.ShrineSquadAsync(native, new C.Identity { ColonyId = "c", LoadToken = "l", MapId = 1 }, ["1"], null, CancellationToken.None));

        Assert.Equal(1, native.CombatReads);
    }
}
