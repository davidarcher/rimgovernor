namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>rooms/*</c> cases (Go <c>cases/rooms</c>).</summary>
public static class RoomsCases
{
    /// <summary>Registers every <c>rooms/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(RoomsReads.Case());
    }
}
