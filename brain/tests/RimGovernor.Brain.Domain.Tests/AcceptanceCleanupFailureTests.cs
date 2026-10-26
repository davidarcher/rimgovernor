using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.HttpChannel;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class AcceptanceCleanupFailureTests
{
    [Fact]
    public void FailedPruneNamesRetainedDirectoryAndPreservesPrimaryFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "rg-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "evidence.txt");
        File.WriteAllText(path, "retained evidence");
        using var diagnostics = new StringWriter();
        var primary = new AcceptanceException("case failed");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var observed = Assert.Throws<AcceptanceException>((Action)(() =>
            {
                try { throw primary; }
                finally { Checkpoints.TryDeleteTree(root, diagnostics); }
            }));
            Assert.Same(primary, observed);
            Assert.Contains(root, diagnostics.ToString(), StringComparison.Ordinal);
            Assert.Contains("directory retained", diagnostics.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(path));
        }
        Checkpoints.TryDeleteTree(root, diagnostics);
        Assert.False(Directory.Exists(root));
        var before = diagnostics.ToString();
        Checkpoints.TryDeleteTree(root, diagnostics);
        Assert.Equal(before, diagnostics.ToString());
    }

    [Fact]
    public void SetupCreatesAbsentConfigurationButRefusesAnUnreadableExistingOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "rg-setup-failure-" + Guid.NewGuid().ToString("N"));
        var layout = SetupLayout.Under(root, root);
        var inputs = new SetupInputs(root, "", "", true, new Dictionary<string, string>());
        var notes = new List<string>();
        try
        {
            SetupEnvironment.BridgeRoot(layout, inputs, notes.Add);
            var config = Path.Combine(layout.Root, "config", "config.json");
            Assert.True(File.Exists(config));
            Assert.Contains(notes, note => note.Contains("configuration absent", StringComparison.Ordinal));
            using var held = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Throws<IOException>(() => SetupEnvironment.BridgeRoot(layout, inputs, notes.Add));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void MissingPidIsAlreadyStopped()
    {
        Assert.True(GameProcess.KillTree(int.MaxValue));
        SetupProcesses.StopPids([int.MaxValue]);
    }

    [Fact]
    public async Task ServiceStopRecordsFailedRoutineEvidenceAndCompletesHandleShutdown()
    {
        var root = Path.Combine(Path.GetTempPath(), "rg-service-stop-" + Guid.NewGuid().ToString("N"));
        var routes = new RouteTable();
        using var clock = routes.Map("POST", "/api/player/clock-speed", (_, _) => Task.FromResult(ChannelResponse.Json(200, "{}")));
        using var routines = routes.Map("GET", "/api/routines", (_, _) => Task.FromResult(ChannelResponse.Json(200, "invalid-json")));
        try
        {
            await using var channel = new HttpChannelServer(new() { EndpointDirectory = ServiceLogs.OtlpDir(root) }, routes);
            channel.Start();
            var service = await ServiceProcess.LaunchServiceAsync(new Config { Output = root }, new(), null, TestContext.Current.CancellationToken);
            await service.StopAsync();
            Assert.Equal("stopped", service.Entry["state"]!.GetValue<string>());
            Assert.Contains("decode GET /api/routines", service.Entry["routine_read_error"]!.GetValue<string>(), StringComparison.Ordinal);
            await service.AssertStoppedAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }
}
