using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/mortar-shell</c> (Go <c>mortar_shell.go</c>).</summary>
internal static class CombatLabMortarShell
{
    // shellLoadTicks bounds each wait for the crew to fetch and load a shell (a few cells' walk and the load), inside
    // the 5,000-tick budget.
    private const int LoadTicks = 1800;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/mortar-shell",
        Scope = "The combat.orders mortar shell op (#1051), a native op no snapshot can prove: on lab-siege, a mortar_fire order for HE at the siege camp "
            + "and a man_mortar crew load Shell_HighExplosive (the combat_mortars row's loaded_shell), a mortar_fire naming Steel refuses unknown_shell, "
            + "and a mortar_fire alone naming Shell_EMP unloads the HE (if still loaded) and the crew loads the EMP shell (#1202).",
        Start = new LabStart { Colonists = 3 },
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
            throw new AcceptanceException("combatlab/mortar-shell reads the snapshot ring from Windows named shared memory");
        }

        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var cx = 0;
        var cz = 0;
        var staged = await CombatStage.StageAsync(h, "lab-siege", cancellationToken, (_, x, z) =>
        {
            cx = x;
            cz = z;
        }).ConfigureAwait(false);
        using var frames = await CombatFrames.OpenAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "combat-mortar-shell-acquire", s.Identity, cancellationToken).ConfigureAwait(false);
        var colonists = staged.Colonists();
        JsonObject Mortar() => CaseSupport.Cell(cx, cz + CombatFixtures.SiegeOurMortar);
        JsonObject Fire(string shell) => new()
        {
            ["mortarFire"] = new JsonObject { ["mortar"] = Mortar(), ["target"] = CaseSupport.Cell(cx, cz + CombatFixtures.SiegeCampMortar), ["shell"] = shell },
        };

        var results = await LabOps.IssueAsync(
            h,
            s.Identity,
            "combat-mortar-shell-1",
            new JsonArray(
                new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[0]), ["draft"] = new JsonObject() },
                Fire("Shell_HighExplosive"),
                new JsonObject { ["pawn"] = LabOps.PawnTarget(colonists[0]), ["manMortar"] = Mortar() },
                Fire("Steel")),
            cancellationToken).ConfigureAwait(false);
        report["first"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        if (results[1]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"the HE fire order: want applied, got {results[1].ToJson()}");
        }

        if (results[2]["applied"].AsBool() != true || results[2]["jobDef"].AsString() != "ManTurret")
        {
            throw new AcceptanceException($"the crew order: want applied ManTurret, got {results[2].ToJson()}");
        }

        if (results[3]["applied"].AsBool() == true || results[3]["refusal"].AsString() != "unknown_shell")
        {
            throw new AcceptanceException($"steel order: want refusal unknown_shell, got {results[3].ToJson()}");
        }

        await AwaitLoadedAsync(h, frames, cx, cz + CombatFixtures.SiegeOurMortar, "Shell_HighExplosive", report, "heTick", cancellationToken).ConfigureAwait(false);

        // The crew still mans it: the fire order alone retakes ManTurret.
        results = await LabOps.IssueAsync(h, s.Identity, "combat-mortar-shell-2", new JsonArray(Fire("Shell_EMP")), cancellationToken).ConfigureAwait(false);
        report["second"] = new JsonArray([.. results.Select(r => (JsonNode?)r.DeepClone())]);
        if (results[0]["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"the EMP order: want applied, got {results[0].ToJson()}");
        }

        await AwaitLoadedAsync(h, frames, cx, cz + CombatFixtures.SiegeOurMortar, "Shell_EMP", report, "empTick", cancellationToken).ConfigureAwait(false);
    }

    // awaitLoaded steps the clock until a frame's combat_mortars row for the mortar at (x, z) shows shell loaded, within
    // shellLoadTicks.
    [SupportedOSPlatform("windows")]
    private static async Task AwaitLoadedAsync(Harness h, CombatFrames frames, int x, int z, string shell, Report report, string key, CancellationToken cancellationToken)
    {
        var last = "";
        for (var waited = 0; waited < LoadTicks; waited += CombatLabMirror.StepTicks)
        {
            var (_, tick) = await CombatStage.TickAsync(h, CombatLabMirror.StepTicks, cancellationToken).ConfigureAwait(false);
            var v = await frames.SnapshotAsync(tick, cancellationToken).ConfigureAwait(false);
            foreach (var raw in v["combatMortars"].AsSlice())
            {
                var row = raw.AsMap() ?? [];
                var cell = row["cell"].AsMap() ?? [];
                if ((int)cell["x"].AsNumberOrZero() == x && (int)cell["z"].AsNumberOrZero() == z)
                {
                    last = row["loadedShell"].AsString();
                    if (last == shell)
                    {
                        report[key] = tick;
                        return;
                    }
                }
            }
        }

        throw new AcceptanceException($"mortar at {CaseSupport.Tag(x)},{CaseSupport.Tag(z)} never loaded {shell} within {CaseSupport.Tag(LoadTicks)} ticks (last \"{last}\")");
    }
}
