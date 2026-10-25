namespace RimGovernor.Brain.Policy;

/// <summary>
/// Binds identity and access direction. Its current position is resolved by the supply census and checked by
/// Hands before a write.
/// </summary>
public sealed record ReserveSupply(string Thing, string Definition, bool Forbid);
