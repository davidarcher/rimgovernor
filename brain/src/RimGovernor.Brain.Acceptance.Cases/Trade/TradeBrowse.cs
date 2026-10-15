namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Unsupported autonomous opening followed by explicit priced browsing and cancellation.</summary>
internal static class TradeBrowse
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/browse",
        Scope = "Normal review refuses unsupported negotiator labor; explicit native opening reads priced medicine, stages a line and cancels with unchanged silver and goods. No bot-selected negotiator claim.",
        Start = TradeRounds.RoutineFixture(),
        Serve = new ServeSpec { Families = ["trade"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "trade-browse" },
        Budget = TimeSpan.FromMinutes(5), Crew = new Crew { Size = 3 },
        Run = (s, ct) => TradeRounds.RunPreparedAsync(s, false, ct)
    };
}
