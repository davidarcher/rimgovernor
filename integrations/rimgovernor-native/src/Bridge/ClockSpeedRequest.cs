#nullable enable
using System;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    // The player's in-game speed request (#3607): the last speed asked for and a
    // serial bumped on every request, both of the game they were asked in, so
    // another loaded game starts with none. Status reports them and the brain
    // adopts a serial it has not adopted yet; native never runs at the speed by
    // itself. Touched only on the game thread under the supervisor gate. Pure
    // state with no Verse reads, so the contract probe compiles it directly.
    internal sealed class ClockSpeedRequest
    {
        private object? _game;
        private Clock.Speed _speed;
        private ulong _serial;

        /// Records <paramref name="speed"/> for <paramref name="game"/> and
        /// returns the new serial; Normal through Hyperfast only.
        internal ulong Request(object game, Clock.Speed speed)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (!ClockRunSpeed.Ordinary(speed)) throw new ArgumentOutOfRangeException(nameof(speed), speed, "A speed request is Normal, Fast, Superfast, Ultrafast or Hyperfast.");
            if (!ReferenceEquals(_game, game)) { _game = game; _serial = 0; }
            _speed = speed;
            return ++_serial;
        }

        /// <paramref name="status"/> with the request <paramref name="game"/>
        /// has pending; unchanged when it has none.
        internal Clock.Status Report(Clock.Status status, object? game) =>
            game != null && ReferenceEquals(_game, game)
                ? status with { PlayerRequestedSpeed = _speed, PlayerSpeedRequestSerial = _serial }
                : status;
    }
}
