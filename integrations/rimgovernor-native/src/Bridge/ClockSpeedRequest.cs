using System;

namespace RimGovernor.Native
{
    // The player's in-game speed request (#3607): the last speed asked for and a
    // serial bumped on every request, both of the game they were asked in, so
    // another loaded game starts with none. Status reports them and the brain
    // adopts a serial it has not adopted yet; native never runs at the speed by
    // itself. For the time controls' highlight (#3608) the request stays pending
    // until a Run at that speed answers it. Touched only on the game thread under
    // the supervisor gate. Pure state with no Verse reads, so the contract probe
    // compiles it directly.
    internal sealed class ClockSpeedRequest
    {
        private object? _game;
        private RimGovernor.Brain.GameFacts.Clock.Speed _speed;
        private ulong _serial;
        private bool _answered;

        /// Records <paramref name="speed"/> for <paramref name="game"/> and
        /// returns the new serial; Normal through Hyperfast only. A request for
        /// the speed the last Run already named (<paramref name="run"/>) is
        /// answered at once.
        internal ulong Request(object game, RimGovernor.Brain.GameFacts.Clock.Speed speed, RimGovernor.Brain.GameFacts.Clock.Speed? run)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (!ClockRunSpeed.Ordinary(speed)) throw new ArgumentOutOfRangeException(nameof(speed), speed, "A speed request is Normal, Fast, Superfast, Ultrafast or Hyperfast.");
            if (!ReferenceEquals(_game, game)) { _game = game; _serial = 0; }
            _speed = speed;
            _answered = run == speed;
            return ++_serial;
        }

        /// A Run at <paramref name="speed"/> took in <paramref name="game"/>:
        /// it answers a pending request for that speed.
        internal void Ran(object? game, RimGovernor.Brain.GameFacts.Clock.Speed speed)
        {
            if (game != null && ReferenceEquals(_game, game) && speed == _speed) _answered = true;
        }

        /// The speed <paramref name="game"/>'s request asks for while no Run at
        /// that speed has answered it; null otherwise.
        internal RimGovernor.Brain.GameFacts.Clock.Speed? Pending(object? game) =>
            game != null && ReferenceEquals(_game, game) && _serial > 0 && !_answered ? _speed : (RimGovernor.Brain.GameFacts.Clock.Speed?)null;

        /// <paramref name="status"/> with the request <paramref name="game"/>
        /// has pending; unchanged when it has none.
        internal RimGovernor.Brain.GameFacts.Clock.Status Report(RimGovernor.Brain.GameFacts.Clock.Status status, object? game) =>
            game != null && ReferenceEquals(_game, game) && _serial > 0
                ? status with { PlayerRequest = new RimGovernor.Brain.GameFacts.Clock.PlayerSpeedRequest.RequestedCase(_speed, _serial) }
                : status;
    }
}
