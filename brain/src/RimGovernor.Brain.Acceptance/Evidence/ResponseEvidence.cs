using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Separates a native outcome from controller activity. Null ticks mean the fixture or recording did not establish
/// that phase. Detection and decision describe the controller's observed game tick, not an interpolated wall-clock
/// timestamp. There is deliberately no latency gate.
/// </summary>
public sealed class ResponseEvidence
{
    private readonly List<double> _controllerPauseMs = [];
    private readonly List<double> _reviewInclusiveMs = [];

    private ResponseEvidence(string fixture, string speed, long onset, string runner)
    {
        Fixture = fixture;
        Speed = speed;
        OnsetTick = onset;
        DetectionSource = "controller_review";
        Runner = runner;
    }

    public string Fixture { get; }

    public string Runner { get; }

    public string Speed { get; }

    public long? OnsetTick { get; }

    public long? DetectionTick { get; private set; }

    public string DetectionSource { get; }

    public long? DecisionTick { get; private set; }

    public long? DispatchTick { get; private set; }

    public long? FirstEffectTick { get; private set; }

    public string Effect { get; private set; } = "";

    public IReadOnlyList<double> ControllerPauseMs => _controllerPauseMs;

    public IReadOnlyList<double> ReviewInclusiveMs => _reviewInclusiveMs;

    public static ResponseEvidence Create(string fixture, string speed, long onset)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            var other => other.ToString().ToUpperInvariant(),
        };
        var runner = $"{Environment.MachineName} {os}/{arch} cpus={Environment.ProcessorCount}";
        return new ResponseEvidence(fixture, speed, onset, runner);
    }

    /// <summary>Accepts only the native postcondition the fixture has checked; a receipt or a clock-window admission must never be passed as an effect.</summary>
    public void ObserveEffect(long tick, string effect)
    {
        if (OnsetTick is { } onset && tick < onset)
        {
            return;
        }

        if (FirstEffectTick is null || tick < FirstEffectTick)
        {
            FirstEffectTick = tick;
            Effect = effect;
        }
    }

    /// <summary>Folds the controller's rows in the log ring at <paramref name="path"/> into the evidence: pause and review times, and the first detection, decision and dispatch ticks at or after the onset.</summary>
    public void ReadLogs(string path)
    {
        foreach (var row in ServiceLogs.ReadLogs(path))
        {
            if (row.HasTick && OnsetTick is { } onset && row.Tick < onset)
            {
                continue;
            }

            var fields = row.Fields();
            if (row.Kind == "clock_step")
            {
                if (fields.TryGetValue("controller_pause_ms", out var pause))
                {
                    _controllerPauseMs.Add(RowValue.Number(pause));
                }

                if (fields.TryGetValue("critical_wave_ms", out var wave))
                {
                    _reviewInclusiveMs.Add(RowValue.Number(wave));
                }
            }

            if (row.Kind == "planner_step" && RowValue.Text(fields.GetValueOrDefault("scope")) == "immediate" && RowValue.Text(fields.GetValueOrDefault("verdict")) != "failed")
            {
                if (RowValue.Text(fields.GetValueOrDefault("target")) == "rounds")
                {
                    if (fields.TryGetValue("observation_tick", out var observation) && DetectionTick is null && AfterOnset(observation))
                    {
                        DetectionTick = (long)RowValue.Number(observation);
                    }
                }
                else if (DecisionTick is null)
                {
                    if (fields.TryGetValue("decision_tick", out var decision) && AfterOnset(decision))
                    {
                        DecisionTick = (long)RowValue.Number(decision);
                    }
                    else if (row.HasTick)
                    {
                        DecisionTick = row.Tick;
                    }
                }
            }

            if (row.Kind == "dispatch" && DispatchTick is null && fields.TryGetValue("dispatch_tick", out var dispatch) && AfterOnset(dispatch))
            {
                DispatchTick = (long)RowValue.Number(dispatch);
            }
        }
    }

    /// <summary>The evidence as the Go JSON encoding held it (camelCase tick names; null for an unestablished phase).</summary>
    public JsonObject ToJson() => new()
    {
        ["fixture"] = Fixture,
        ["runner"] = Runner,
        ["speed"] = Speed,
        ["onsetTick"] = OnsetTick,
        ["detectionTick"] = DetectionTick,
        ["detectionSource"] = DetectionSource,
        ["decisionTick"] = DecisionTick,
        ["dispatchTick"] = DispatchTick,
        ["firstEffectTick"] = FirstEffectTick,
        ["effect"] = Effect,
        ["controllerPauseMs"] = new JsonArray([.. _controllerPauseMs.Select(v => JsonValue.Create(v))]),
        ["reviewInclusiveMs"] = new JsonArray([.. _reviewInclusiveMs.Select(v => JsonValue.Create(v))]),
    };

    private bool AfterOnset(object? value) => OnsetTick is not { } onset || (long)RowValue.Number(value) >= onset;
}
