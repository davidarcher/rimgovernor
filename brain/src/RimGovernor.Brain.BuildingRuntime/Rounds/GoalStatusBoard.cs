using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The in-process hand-off of the goals Family's latest review (#3497): the goal and theme statuses its full read
/// derived, tagged with the world they were derived for. The in-game panel reads it with the stored record; nothing is
/// saved or sent. A reader in another world sees no statuses.
/// </summary>
public sealed class GoalStatusBoard
{
    private readonly object _mu = new();
    private World _world;
    private IReadOnlyList<GoalStatus> _goals = [];
    private IReadOnlyList<ThemeStatus> _themes = [];

    /// <summary>Replaces the board with the review of <paramref name="world"/>.</summary>
    public void Publish(World world, IReadOnlyList<GoalStatus> goals, IReadOnlyList<ThemeStatus> themes)
    {
        lock (_mu)
        {
            (_world, _goals, _themes) = (world, goals, themes);
        }
    }

    /// <summary>The statuses of the last review of <paramref name="world"/>'s colony; empty when none was filed for it.</summary>
    public (IReadOnlyList<GoalStatus> Goals, IReadOnlyList<ThemeStatus> Themes) Read(World world)
    {
        lock (_mu)
        {
            return _world.Colony == world.Colony ? (_goals, _themes) : ([], []);
        }
    }
}
