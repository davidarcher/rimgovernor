using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Brain.Bridge;

// The game's const and static readonly members the catalog reads.
// A public member is read directly (SkillRecord.MaxLevel); a private compile-time const has no storage to bind to,
// so it is read once by name, typed, and a member the game renames or retypes fails loudly on first use.

internal static class GameConstants
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static int AnimalTalkDuration => Read<int>(typeof(JobDriver_InteractAnimal), "TalkDuration");

    internal static int AnimalFeedDuration => Read<int>(typeof(JobDriver_InteractAnimal), "FeedDuration");

    internal static int MinTicksSinceThickened => Read<int>(typeof(WorkGiver_CleanFilth), "MinTicksSinceThickened");

    internal static float DoorOpenTicks => Read<float>(typeof(Building_Door), "OpenTicks");

    internal static int DoorCloseDelayTicks => Read<int>(typeof(Building_Door), "CloseDelayTicks");

    internal static float MinDeconstructWork => Read<float>(typeof(JobDriver_Deconstruct), "MinDeconstructWork");

    internal static float MaxDeconstructWork => Read<float>(typeof(JobDriver_Deconstruct), "MaxDeconstructWork");

    // These members are private; neither DefOf nor a public typed field exposes them.
    internal static int WorshipRoomMinScore => Read<int>(typeof(RoomRoleWorker_WorshipRoom), "MinScore");
    internal static int MoodBreakRecoveryTicks => Read<int>(typeof(MentalBreaker), "MinTicksSinceRecoveryToBreak");
    internal static int RaidGiveUpTicks => Math.Max(Read<IntRange>(typeof(LordJob_AssaultColony), "AssaultTimeBeforeGiveUp").max,
        Math.Max(Read<IntRange>(typeof(LordJob_AssaultColony), "SapTimeBeforeGiveUp").max,
            Read<IntRange>(typeof(LordJob_AssaultColony), "BreachTimeBeforeGiveUp").max));

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
