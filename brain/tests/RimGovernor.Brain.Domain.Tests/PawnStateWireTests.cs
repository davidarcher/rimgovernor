using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using Common = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A pawn row is read whole (#3768): every section is present, and the cases a section can lack are named states
/// (not an animal, no DLC, no colonist, no hostiles, no care, no ideoligion, not a guest). Each named state, and the
/// opt-in tend table, round-trips through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class PawnStateWireTests
{
    [Fact]
    public void EmptyRowRoundTripsWithEveryNamedState()
    {
        var row = PawnFixtures.Row("p1");

        var json = WireJson.Serialize(row);

        Assert.Contains("\"animalState\":{\"type\":\"not_animal\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"nearestColonist\":{\"type\":\"none\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"raidArmor\":{\"type\":\"no_hostiles\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"biotech\":{\"type\":\"inactive\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"care\":{\"type\":\"none\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"guest\":{\"type\":\"free\"}", json, StringComparison.Ordinal);
        Assert.Equal(row, WireJson.Deserialize<PawnState>(json));
    }

    [Fact]
    public void FilledRowRoundTrips()
    {
        var row = PawnFixtures.Row("p1") with
        {
            NearestColonist = new NearestColonist.At(12.5),
            RaidArmor = new RaidArmor.Rated(0.4),
            Royalty = new PawnRoyaltyState.NoTracker(),
            Settings = PawnFixtures.Row("p1").Settings with
            {
                Care = new PawnCare.Configured(RimGovernor.Contracts.Wire.Operations.MedicalCare.Best, true, RimGovernor.Contracts.Wire.Operations.HostilityResponse.Attack),
                PolicyInputs = PawnFixtures.Inputs("ideo1", 0.75, ["Cannibalism_Preferred"], "Leader", RimGovernor.Contracts.Wire.Defs.GuestStatus.Prisoner),
            },
        };

        var json = WireJson.Serialize(row);

        Assert.Contains("\"ideo\":{\"type\":\"member\",\"ideoId\":\"ideo1\"", json, StringComparison.Ordinal);
        Assert.Equal(row, WireJson.Deserialize<PawnState>(json));
    }

    [Fact]
    public void TendTableRoundTripsBothReachStates()
    {
        var table = new TendDoctorSnapshot
        {
            Context = new Common.ObservationContext { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
            Doctors =
            [
                new TendDoctor { PawnId = "a", ControlEligible = true, Spawned = true, HasDrafter = true, CapacitiesOk = true, Reach = new TendReach.Read(["b"]) },
                new TendDoctor { PawnId = "b", ControlEligible = false, Spawned = true, HasDrafter = true, CapacitiesOk = false, Reach = new TendReach.Skipped() },
            ],
        };

        var json = WireJson.Serialize(table);

        Assert.Contains("\"reach\":{\"type\":\"read\",\"pawnIds\":[\"b\"]}", json, StringComparison.Ordinal);
        Assert.Contains("\"reach\":{\"type\":\"reach_skipped\"}", json, StringComparison.Ordinal);
        Assert.Equal(table, WireJson.Deserialize<TendDoctorSnapshot>(json));
    }
}
