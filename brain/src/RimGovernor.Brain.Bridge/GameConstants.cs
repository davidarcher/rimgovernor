using System.Reflection;
using RimWorld;
using Verse;

namespace RimGovernor.Brain.Bridge;

// The game's const and static readonly members the catalog reads (Go GameConstants, the typed mirror of defs.proto).
// A public member is read directly (SkillRecord.MaxLevel); a private compile-time const has no storage to bind to,
// so it is read once by name, typed, and a member the game renames or retypes fails loudly on first use.

internal static class GameConstants
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static int AnimalTalkDuration => Read<int>(typeof(JobDriver_InteractAnimal), "TalkDuration");

    internal static int AnimalFeedDuration => Read<int>(typeof(JobDriver_InteractAnimal), "FeedDuration");

    internal static float AnimalNutritionPercentagePerFeed => Read<float>(typeof(JobDriver_InteractAnimal), "NutritionPercentagePerFeed");

    internal static float AnimalMaxMinNutritionPerFeed => Read<float>(typeof(JobDriver_InteractAnimal), "MaxMinNutritionPerFeed");

    internal static int MinTicksSinceThickened => Read<int>(typeof(WorkGiver_CleanFilth), "MinTicksSinceThickened");

    internal static float DoorOpenTicks => Read<float>(typeof(Building_Door), "OpenTicks");

    internal static int DoorCloseDelayTicks => Read<int>(typeof(Building_Door), "CloseDelayTicks");

    internal static float MinDeconstructWork => Read<float>(typeof(JobDriver_Deconstruct), "MinDeconstructWork");

    internal static float MaxDeconstructWork => Read<float>(typeof(JobDriver_Deconstruct), "MaxDeconstructWork");

    internal static T Read<T>(Type owner, string name)
    {
        var field = owner.GetField(name, Any) ?? throw new CatalogException($"game constant {owner.Name}.{name} is absent");
        return field.IsLiteral && field.GetRawConstantValue() is T value
            ? value
            : field.GetValue(null) is T fromStatic
                ? fromStatic
                : throw new CatalogException($"game constant {owner.Name}.{name} is not a {typeof(T).Name}");
    }
}
