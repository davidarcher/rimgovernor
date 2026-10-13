namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>trade/*</c> cases (Go <c>cases/trade</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class TradeCases
{
    /// <summary>Registers every <c>trade/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(TradeOpen.Case());
        registry.Register(TradeExecute.Case());
        registry.Register(TradeBrowse.Case());
        registry.Register(TradeRounds.Case());
        registry.Register(TradeSettlementRoundtrip.Case());
        foreach (var request in TradeRequest.Cases())
        {
            registry.Register(request);
        }

        foreach (var session in TradeWorldSession.Cases())
        {
            registry.Register(session);
        }
    }
}
