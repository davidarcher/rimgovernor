using RimGovernor.Brain.BuildingRuntime;

namespace RimGovernor.Brain.Host;

/// <summary>Why the host stopped the brain from writing.</summary>
public enum SuspensionKind
{
    /// <summary>The scheduler step faulted on consecutive steps.</summary>
    Fault,

    /// <summary>A scheduler step ran past the watchdog's bound.</summary>
    Stall,

    /// <summary>A stalled step ignored its cancellation and was abandoned.</summary>
    Wedged,
}

/// <summary>
/// The brain's authority was suspended, why, and when. It holds until the player resumes: a suspended brain is
/// never resumed on its own, whatever the auto-resumer would otherwise do.
/// </summary>
public sealed record HostSuspension(SuspensionKind Kind, string Detail, DateTimeOffset At);

/// <summary>A planner that missed the wave cutoff on consecutive steps.</summary>
public sealed record PlannerHealth(string Planner, int ConsecutiveMisses, bool Reported);

/// <summary>
/// Watches the planners of each step for the stall the scheduler itself isolates: a planner that is still
/// running when the wave's cutoff passes does not fail the step, so nothing but this sees it keep missing. Only
/// reports; the scheduler offers no way to take one planner out of the wave, so the host's lever is the whole
/// brain's authority (<see cref="StepSupervisor"/>).
/// </summary>
internal sealed class PlannerSupervisor(int missLimit, HostLog log)
{
    private readonly object _mu = new();
    private readonly Dictionary<string, int> _misses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public void Observe(ClockSchedulerResult result)
    {
        lock (_mu)
        {
            var missed = new HashSet<string>(result.MissedCutoff, StringComparer.Ordinal);
            foreach (var name in result.Planners)
            {
                if (missed.Contains(name))
                {
                    continue;
                }
                _ = _misses.Remove(name);
                _ = _reported.Remove(name);
            }
            foreach (var name in missed)
            {
                var count = _misses.GetValueOrDefault(name) + 1;
                _misses[name] = count;
                if (count >= missLimit && _reported.Add(name))
                {
                    log.Warn("planner_stalled", "a planner keeps missing the wave cutoff", ("planner", name), ("consecutive_misses", count));
                }
            }
        }
    }

    public IReadOnlyList<PlannerHealth> Health()
    {
        lock (_mu)
        {
            return [.. _misses.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new PlannerHealth(m.Key, m.Value, _reported.Contains(m.Key)))];
        }
    }
}

/// <summary>
/// Counts consecutive faulted or stalled steps and holds the suspension (a latch the player's resume clears). It
/// decides nothing about the session: the loop acts on <see cref="Faulted"/> and <see cref="Suspend"/>.
/// </summary>
internal sealed class StepSupervisor(int faultLimit)
{
    private readonly object _mu = new();
    private int _consecutive;
    private long _faults;
    private long _stalls;
    private long _steps;
    private HostSuspension? _suspension;

    public SchedulerStatus Status()
    {
        lock (_mu)
        {
            return new SchedulerStatus(_steps, _faults, _stalls, _consecutive, _suspension);
        }
    }

    public HostSuspension? Suspension
    {
        get
        {
            lock (_mu)
            {
                return _suspension;
            }
        }
    }

    public void Succeeded()
    {
        lock (_mu)
        {
            _steps++;
            _consecutive = 0;
        }
    }

    /// <summary>Counts one faulted or stalled step; true when the consecutive count reached the limit.</summary>
    public bool Faulted(bool stall)
    {
        lock (_mu)
        {
            _steps++;
            _consecutive++;
            if (stall)
            {
                _stalls++;
            }
            else
            {
                _faults++;
            }
            return _consecutive >= faultLimit;
        }
    }

    /// <summary>Latches a suspension; the first one stands until cleared. True when this call set it.</summary>
    public bool Suspend(HostSuspension suspension)
    {
        lock (_mu)
        {
            if (_suspension is not null)
            {
                return false;
            }
            _suspension = suspension;
            return true;
        }
    }

    /// <summary>The player's resume: the brain may write again and the fault run starts over.</summary>
    public void Clear()
    {
        lock (_mu)
        {
            _suspension = null;
            _consecutive = 0;
        }
    }
}

/// <summary>What the scheduler loop has done so far.</summary>
public sealed record SchedulerStatus(long Steps, long Faults, long Stalls, int ConsecutiveFaults, HostSuspension? Suspension);
