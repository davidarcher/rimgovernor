using System.Collections.Concurrent;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store;

/// <summary>
/// Holds, per world, the newest tick of settled evidence a retired plan carried: stock observed before it was
/// already spent by that plan, so it does not buy a second method (Go <c>retirementFloors</c>). It is
/// session-only, shared by every handle on one database path, and resets on a goal rebuild and a process
/// restart. The load is not part of the key.
/// </summary>
internal sealed class RetirementFloors
{
    private static readonly ConcurrentDictionary<string, RetirementFloors> _registry = new(StringComparer.Ordinal);

    private readonly object _lock = new();
    private readonly Dictionary<(ColonyId Colony, MapId Map), Tick> _ticks = [];

    public static RetirementFloors For(string path) => _registry.GetOrAdd(path, static _ => new RetirementFloors());

    public void Raise(IReadOnlyDictionary<(ColonyId Colony, MapId Map), Tick> settled)
    {
        lock (_lock)
        {
            foreach (var (world, tick) in settled)
            {
                if (!_ticks.TryGetValue(world, out var floor) || tick > floor)
                {
                    _ticks[world] = tick;
                }
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _ticks.Clear();
        }
    }

    /// <summary>Refuses an observation older than its world's floor.</summary>
    public void Guard(GenerationSnapshot current, Tick tick)
    {
        bool known;
        Tick floor;
        lock (_lock)
        {
            known = _ticks.TryGetValue((current.Colony, current.Map), out floor);
        }
        if (known && tick < floor)
        {
            throw new DomainException("observation predates retired accounting evidence");
        }
    }
}
