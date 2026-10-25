using System;
using RimWorld;
using Verse;

namespace RimGovernor.Native;

/// <summary>The current weather's known sky-glow interval; future weather is a later native random draw.</summary>
internal sealed record NativeDeathrestWeather(int Age, int RemainingTicks, float PreviousMax, float CurrentMax)
{
    internal static NativeDeathrestWeather Read(Map map)
    {
        var manager = map.weatherManager;
        // WeatherDecider exposes no public accessor for the duration already drawn by StartNextWeather.
        var duration = BridgeCommon.PrivateInstanceField(typeof(WeatherDecider), "curWeatherDuration")?.GetValue(map.weatherDecider) as int?
            ?? throw new InvalidOperationException("WeatherDecider.curWeatherDuration is not readable.");
        if (map.fireWatcher.LargeFireDangerPresent || !manager.curWeather.temperatureRange.Includes(map.mapTemperature.OutdoorTemp)
            || ModsConfig.AnomalyActive && map.gameConditionManager.BrightnessChanging)
            duration = (int)(duration * .25f);
        var forced = map.weatherDecider.ForcedWeather;
        if (forced != null && manager.curWeather != forced)
            duration = !ModsConfig.AnomalyActive || forced.transitionTicksOverride == int.MaxValue
                ? (int)WeatherManager.TransitionTicks : forced.transitionTicksOverride;
        var previous = manager.lastWeather.maxGlow;
        var current = manager.curWeather.maxGlow;
        if (manager.curWeatherAge < 0 || !float.IsFinite(previous) || !float.IsFinite(current))
            throw new InvalidOperationException("Current weather sky facts are not readable.");
        return new(manager.curWeatherAge, Math.Max(0, duration - manager.curWeatherAge), previous, current);
    }

    internal float GlowAt(float celestialGlow, int ahead)
    {
        // WeatherWorker.CurSkyTarget caps celestial glow by maxGlow; SkyManager then interpolates the two weather targets.
        var last = Math.Min(celestialGlow, PreviousMax);
        var current = Math.Min(celestialGlow, CurrentMax);
        var fraction = Math.Clamp((Age + ahead) / WeatherManager.TransitionTicks, 0, 1);
        return last + (current - last) * fraction;
    }
}
