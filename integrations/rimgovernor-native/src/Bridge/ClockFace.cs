namespace HomeBridge.BridgeTools
{
    // The clock face the date readout shows in place of vanilla's hour, in the game's
    // 12-hour ("3:45:12 PM") or 24-hour ("15:45:12") form. No Verse types, so the
    // contract probe compiles it directly.
    internal static class ClockFace
    {
        // GenDate.TicksPerDay: 60000 ticks make the 86400 s day, so a tick is 1.44 s.
        private const int TicksPerDay = 60000;
        private const int SecondsPerDay = 86400;

        // dayTick is the local tick of day (GenDate.DayTick); any value wraps into the
        // day. Seconds are floored, so the face steps by one or two seconds per tick.
        internal static string Format(int dayTick, bool twelveHour)
        {
            var tick = (dayTick % TicksPerDay + TicksPerDay) % TicksPerDay;
            var seconds = (long)tick * SecondsPerDay / TicksPerDay;
            var hour = (int)(seconds / 3600);
            var minute = (int)(seconds / 60 % 60);
            var second = (int)(seconds % 60);
            if (!twelveHour) return $"{hour:00}:{minute:00}:{second:00}";
            var face = hour % 12 == 0 ? 12 : hour % 12;
            return $"{face}:{minute:00}:{second:00} {(hour < 12 ? "AM" : "PM")}";
        }
    }
}
