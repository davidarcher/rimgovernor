using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A served sweep's bounded observation schedule and canonical fight completion.</summary>
internal sealed class SweepFightWatch
{
    // One nominal second of game time: observe outcomes promptly without serializing the cumulative ledger every
    // 50 ms clock poll. Reads do not drain the damage or shot ledgers; the final paused reads still collect both.
    internal const int ReadInterval = 60;

    private readonly Fx.LabFightRead _initial;
    private readonly IReadOnlyDictionary<string, string> _staged;
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
    private readonly int _until;
    private int _lastRead;
    private bool _resolved;

    internal SweepFightWatch(Fx.LabFightRead initial, IReadOnlyDictionary<string, string> staged, int until)
    {
        _initial = initial;
        _staged = staged;
        _until = until;
        _lastRead = initial.Tick;
        ObserveParticipants(initial);
    }

    internal bool Finished(int tick, bool combatSummary) => combatSummary || _resolved || tick >= _until;

    internal bool ReadDue(int tick) => !Finished(tick, false) && tick - _lastRead >= ReadInterval;

    internal void Observe(Fx.LabFightRead read)
    {
        _lastRead = read.Tick;
        ObserveParticipants(read);
        // The staging read proves no outcome. Nor can a later read call an unseen pod passenger "gone": every staged
        // participant must first have appeared in native pawn facts or the cumulative native damage ledger.
        if (read.Tick > _initial.Tick && _staged.Keys.All(_observed.Contains))
        {
            _resolved = CombatLabBundle.Aggregate("", _staged, [_initial, read]).Winner.Length > 0;
        }
    }

    private void ObserveParticipants(Fx.LabFightRead read)
    {
        foreach (var pawn in read.Pawns)
        {
            _ = _observed.Add(pawn.Id);
        }
        foreach (var hit in read.Damage)
        {
            _ = _observed.Add(hit.Victim);
            _ = _observed.Add(hit.Instigator);
        }
    }
}
