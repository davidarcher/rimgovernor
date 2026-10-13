using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/hive-temperature</c> (Go <c>hive_temperature.go</c>).</summary>
internal static class CombatLabHiveTemperature
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/hive-temperature",
        Scope = "The combat frame's hive temperature (#1073) on lab-infestation: one step after staging, a snapshot frame carries "
            + "combat_hive_temperature_c, it decodes as a known fact, and an unheated mountain room reads between -60 and 60 C.",
        Start = new LabStart { Colonists = 5 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new AcceptanceException("combatlab/hive-temperature reads the snapshot ring from Windows named shared memory");
        }

        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        _ = await CombatStage.StageAsync(h, "lab-infestation", cancellationToken).ConfigureAwait(false);
        using var frames = await CombatFrames.OpenAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        var (_, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
        var v = await frames.SnapshotAsync(tick, cancellationToken).ConfigureAwait(false);
        if (v["combatHiveTemperatureC"] is null)
        {
            throw new AcceptanceException($"frame at tick {CaseSupport.Tag((long)new CombatState(v).Tick)} has no hive temperature");
        }

        // A known fact is a finite number; the proto wrapper's unknown state never serializes one.
        var known = v["combatHiveTemperatureC"] is JsonValue value && value.TryGetValue<double>(out _);
        var temp = known ? v["combatHiveTemperatureC"].AsNumber() : 0;
        s.Report["hiveTemperatureC"] = temp;
        if (!known || temp < -60 || temp > 60)
        {
            throw new AcceptanceException($"hive temperature {temp.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} (known {(known ? "true" : "false")}): want an unheated room's");
        }
    }
}
