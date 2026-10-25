using System.Text;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.PlayerApi;
using Xunit;
using Speed = RimGovernor.Brain.GameFacts.Clock.Speed;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ClockTargetSpeedTests
{
    private sealed class Control(Speed initial) : IClockSpeedControl
    {
        public Speed Speed { get; private set; } = initial;

        public void Change(Speed speed) => Speed = speed;
    }

    private static BrainCompositionOptions Options(Speed? speed = null) => speed is { } s
        ? new() { TargetSpeed = s }
        : new();

    [Fact]
    public void TheCompositionTargetsUltrafastUnlessTheOptionSaysOtherwise()
    {
        Assert.Equal(Speed.Ultrafast, new BrainComposition(Options()).TargetSpeed.Speed);
        Assert.Equal(Speed.Fast, new BrainComposition(Options(Speed.Fast)).TargetSpeed.Speed);
        Assert.Throws<ArgumentException>(() => new BrainComposition(Options((Speed)0)));
    }

    [Fact]
    public void TestAccelerationPinsHyperfastWhateverTheTarget()
    {
        var accelerated = new BrainComposition(new() { ClockTestAcceleration = true, TargetSpeed = Speed.Fast });
        Assert.Equal(Speed.Hyperfast, accelerated.RunTargetSpeed().Speed);
        accelerated.TargetSpeed.Set(Speed.Normal);
        Assert.Equal(Speed.Hyperfast, accelerated.RunTargetSpeed().Speed);

        var played = new BrainComposition(Options(Speed.Fast));
        Assert.Same(played.TargetSpeed, played.RunTargetSpeed());
    }

    [Theory]
    [InlineData(null, Speed.Hyperfast)]
    [InlineData("0", Speed.Hyperfast)]
    [InlineData("1", Speed.Fast)]
    public void TheCaseOverrideUnpinsATestAccelerationLaunch(string? unpin, Speed expected)
    {
        var saveData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rg-save-data"));
        Assert.True(InProcEnvironment.TryResolve(name => name == InProcEnvironment.UnpinTestSpeed ? unpin : null, saveData, DateTimeOffset.UnixEpoch, out var inputs, out _));
        var composition = new BrainComposition(new() { ClockTestAcceleration = inputs!.PinsHyperfast(testAccelerationLaunch: true), TargetSpeed = Speed.Fast });
        Assert.Equal(expected, composition.RunTargetSpeed().Speed);
        Assert.False(inputs.PinsHyperfast(testAccelerationLaunch: false));
    }

    // #3648: the speed matrix's game launches with test acceleration like every acceptance game, so without the unpin in
    // its launch environment the brain ran every row at Hyperfast whatever speed the row posted.
    [Theory]
    [InlineData(Speed.Normal)]
    [InlineData(Speed.Fast)]
    [InlineData(Speed.Superfast)]
    [InlineData(Speed.Ultrafast)]
    public void TheSpeedMatrixGameRunsEachRowAtTheSpeedItPosts(Speed row)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rg-speedmatrix-root"));
        var env = SpeedMatrixCommand.Configure(SpeedMatrixCommand.Parse(["-root", root])).LaunchEnv;
        var saveData = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rg-save-data"));
        Assert.True(InProcEnvironment.TryResolve(env.GetValueOrDefault, saveData, DateTimeOffset.UnixEpoch, out var inputs, out var error), error);
        var composition = new BrainComposition(new() { ClockTestAcceleration = inputs!.PinsHyperfast(testAccelerationLaunch: true) });
        composition.TargetSpeed.Set(row);
        Assert.Equal(row, composition.RunTargetSpeed().Speed);
    }

    [Fact]
    public void TheLiveTargetChangesAndRefusesAnUnspecifiedSpeed()
    {
        var target = new ClockTargetSpeed(Speed.Ultrafast);
        target.Set(Speed.Hyperfast);
        Assert.Equal(Speed.Hyperfast, target.Speed);
        target.Set(Speed.Normal);
        Assert.Equal(Speed.Normal, target.Speed);
        Assert.Throws<ArgumentOutOfRangeException>(() => target.Set((Speed)0));
        Assert.Equal(Speed.Normal, target.Speed);
    }

    [Theory]
    [InlineData("Normal", Speed.Normal)]
    [InlineData("Fast", Speed.Fast)]
    [InlineData("Superfast", Speed.Superfast)]
    [InlineData("Ultrafast", Speed.Ultrafast)]
    [InlineData("Hyperfast", Speed.Hyperfast)]
    public void TheRouteSetsTheNamedSpeed(string name, Speed expected)
    {
        var control = new Control(Speed.Ultrafast == expected ? Speed.Normal : Speed.Ultrafast);
        var reply = PlayerRoutes.SetClockSpeed(control, Encoding.UTF8.GetBytes($"{{\"speed\":\"{name}\"}}"));
        Assert.Equal(200, reply.Status);
        Assert.Equal(expected, control.Speed);
    }

    [Theory]
    [InlineData("{\"speed\":\"Paused\"}")]
    [InlineData("{\"speed\":\"ultrafast\"}")]
    [InlineData("{\"speed\":4}")]
    [InlineData("{}")]
    [InlineData("{\"speed\":\"Fast\",\"extra\":1}")]
    public void TheRouteRefusesAnythingButAPlayableSpeedName(string body)
    {
        var control = new Control(Speed.Fast);
        var reply = PlayerRoutes.SetClockSpeed(control, Encoding.UTF8.GetBytes(body));
        Assert.Equal(400, reply.Status);
        Assert.Equal(Speed.Fast, control.Speed);
        var detail = Encoding.UTF8.GetString(reply.Body);
        foreach (var speed in new[] { "Normal", "Fast", "Superfast", "Ultrafast", "Hyperfast" })
        {
            Assert.Contains(speed, detail, StringComparison.Ordinal);
        }
    }
}
