using System;
using System.Reflection;
using RimGovernor.Native;

internal static class NativeDeathrestWeatherProbe
{
    internal static void Invoke()
    {
        var map = new Verse.Map();
        map.weatherManager.curWeatherAge = 2000;
        map.weatherManager.curWeather.maxGlow = 0;
        var read = NativeDeathrestWeather.Read(map);
        Require(read.RemainingTicks == 8000, "reads the drawn native duration");
        Require(read.GlowAt(1, 0) == .5f && read.GlowAt(1, 2000) == 0, "weather transition caps sky glow before the sunlight test");
        map.fireWatcher.LargeFireDangerPresent = true;
        Require(NativeDeathrestWeather.Read(map).RemainingTicks == 500, "fire quarters the native duration");
        map.fireWatcher.LargeFireDangerPresent = false;
        map.weatherManager.curWeather.temperatureRange.Valid = false;
        Require(NativeDeathrestWeather.Read(map).RemainingTicks == 500, "temperature quarters the duration");
        map.weatherManager.curWeather.temperatureRange.Valid = true;
        Verse.ModsConfig.AnomalyActive = true;
        map.gameConditionManager.BrightnessChanging = true;
        Require(NativeDeathrestWeather.Read(map).RemainingTicks == 500, "brightness changes quarter the duration");
        map.gameConditionManager.BrightnessChanging = false;
        map.weatherDecider.ForcedWeather = new() { transitionTicksOverride = 3000 };
        Require(NativeDeathrestWeather.Read(map).RemainingTicks == 1000, "forced weather uses its native override");
        Verse.ModsConfig.AnomalyActive = false;
        Require(NativeDeathrestWeather.Read(map).RemainingTicks == 2000, "without Anomaly forced weather uses TransitionTicks");
        map.weatherManager.curWeather.maxGlow = float.NaN;
        var refused = false;
        try { NativeDeathrestWeather.Read(map); } catch (InvalidOperationException) { refused = true; }
        Require(refused, "unreadable native sky evidence fails the read");
        Console.WriteLine("native-deathrest-weather: duration, transition, shortening, forcing and unread-fact refusal passed");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

namespace RimGovernor.Native
{
    internal static class BridgeCommon
    {
        internal static FieldInfo? PrivateInstanceField(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
namespace Verse
{
    public partial class Map
    {
        public RimWorld.WeatherManager weatherManager = new();
        public RimWorld.WeatherDecider weatherDecider = new();
        public WeatherFireWatcher fireWatcher = new();
        public WeatherTemperature mapTemperature = new();
        public WeatherConditions gameConditionManager = new();
    }
    public sealed class WeatherFireWatcher { public bool LargeFireDangerPresent; }
    public sealed class WeatherTemperature { public float OutdoorTemp = 20; }
    public sealed class WeatherConditions { public bool BrightnessChanging; }
    public sealed class WeatherRange { public bool Valid = true; public bool Includes(float _) => Valid; }
    public sealed class WeatherDef { public float maxGlow = 1; public WeatherRange temperatureRange = new(); public int transitionTicksOverride = int.MaxValue; }
    public static class ModsConfig { public static bool AnomalyActive; }
}
namespace RimWorld
{
    public sealed class WeatherManager
    {
        public const float TransitionTicks = 4000;
        public Verse.WeatherDef curWeather = new(), lastWeather = new();
        public int curWeatherAge;
    }
    public sealed class WeatherDecider
    {
        private int curWeatherDuration = 10000;
        public int DurationForProbe => curWeatherDuration;
        public Verse.WeatherDef? ForcedWeather;
    }
}
