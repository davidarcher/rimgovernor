using RimGovernor.Brain.Host;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class InProcEnvironmentTests
{
    private static readonly string SaveData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rg-save-data"));
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 1, 2, TimeSpan.Zero);

    private static (bool Ok, InProcInputs? Inputs, string? Error) Resolve(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        var ok = InProcEnvironment.TryResolve(name => map.GetValueOrDefault(name), SaveData, Now, out var inputs, out var error);
        return (ok, inputs, error);
    }

    [Fact]
    public void DefaultsLiveUnderTheSaveDataFolder()
    {
        var (ok, inputs, _) = Resolve();
        Assert.True(ok);
        Assert.Equal(Path.Combine(SaveData, "RimGovernor", "brain-state.db"), inputs!.StatePath);
        Assert.Equal(SaveData, inputs.ProfileDirectory);
        Assert.Equal(Path.Combine(SaveData, "RimGovernor", "otlp"), inputs.OtlpDirectory);
        Assert.False(string.IsNullOrWhiteSpace(inputs.RunId));
        Assert.True(inputs.AutoResume);
        Assert.True(inputs.LayoutOverlay);
    }

    [Fact]
    public void ExplicitValuesWinOverDefaults()
    {
        var state = Path.Combine(SaveData, "elsewhere", "s.db");
        var profile = Path.Combine(SaveData, "profile");
        var otlp = Path.Combine(SaveData, "otlp-here");
        var (ok, inputs, _) = Resolve((InProcEnvironment.State, state), (InProcEnvironment.Profile, profile), (InProcEnvironment.OtlpDirectory, otlp), (InProcEnvironment.RunId, "run1"), (InProcEnvironment.ContinueState, "0"));
        Assert.True(ok);
        Assert.Equal(state, inputs!.StatePath);
        Assert.Equal(profile, inputs.ProfileDirectory);
        Assert.Equal(otlp, inputs.OtlpDirectory);
        Assert.Equal("run1", inputs.RunId);
    }

    [Fact]
    public void ContinueStateOffStartsAFreshStampedDatabase()
    {
        var (ok, inputs, _) = Resolve((InProcEnvironment.ContinueState, "false"));
        Assert.True(ok);
        Assert.Equal(Path.Combine(SaveData, "RimGovernor", "brain-state-20261013-200102.db"), inputs!.StatePath);
    }

    [Fact]
    public void ObserveTurnsOffTheResumeAndTheOverlay()
    {
        var (ok, inputs, _) = Resolve((InProcEnvironment.Observe, "1"), (InProcEnvironment.LayoutOverlay, "1"));
        Assert.True(ok);
        Assert.False(inputs!.AutoResume);
        Assert.False(inputs.LayoutOverlay);
    }

    [Fact]
    public void LayoutOverlayOffIsHonouredWhilePlaying()
    {
        var (ok, inputs, _) = Resolve((InProcEnvironment.LayoutOverlay, "no"));
        Assert.True(ok);
        Assert.True(inputs!.AutoResume);
        Assert.False(inputs.LayoutOverlay);
    }

    [Theory]
    [InlineData("RIMGOVERNOR_STATE", "relative/state.db")]
    [InlineData("RIMGOVERNOR_PROFILE", "profile")]
    [InlineData("RIMGOVERNOR_OTLP_DIR", "otlp")]
    [InlineData("RIMGOVERNOR_OBSERVE", "maybe")]
    [InlineData("RIMGOVERNOR_CONTINUE_STATE", "2")]
    public void ABadValueIsRefusedByName(string name, string value)
    {
        var (ok, inputs, error) = Resolve((name, value));
        Assert.False(ok);
        Assert.Null(inputs);
        Assert.Contains(name, error);
    }
}
