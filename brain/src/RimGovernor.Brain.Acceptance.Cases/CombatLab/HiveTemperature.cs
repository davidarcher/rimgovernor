using System.Globalization;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/hive-temperature</c> (#1073): the combat frame's hive temperature. On lab-infestation a frame carries a
/// known combat_hive_temperature_c, and the unheated mountain room reads between -60 and 60 C.
/// </summary>
internal static class CombatLabHiveTemperature
{
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-infestation");

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/hive-temperature",
        Scope = "The combat frame's hive temperature (#1073), a native read: on lab-infestation a snapshot frame carries combat_hive_temperature_c "
            + "as a known fact, and the unheated mountain room reads between -60 and 60 C.",
        Ticks = CombatLabKit.ProbeTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var frame = await new LabFrames(s).UntilAsync("frame", CombatLabKit.ProbeTicks, 0, f => f.CombatHiveTemperatureC is not null, cancellationToken).ConfigureAwait(false);
        var temp = frame.CombatHiveTemperatureC!.Value;
        s.Record("hive_temperature_c", temp);
        if (!float.IsFinite(temp) || temp < -60 || temp > 60)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"hive temperature {temp:0.###} C at tick {frame.Context?.Tick}: want an unheated room's"));
        }
    }
}
