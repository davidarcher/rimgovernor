using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One building's hack state as its row carries it; every field is unknown when native did not read it. The
/// static facts (defence, skill prerequisite, lockout) are the catalog's (bridge.OdysseyCatalog).
/// ProgressPercent is the hack progress, 0..1.
/// </summary>
public sealed record Hack(Fact<double> ProgressPercent, Fact<bool> Hacked, Fact<bool> LockedOut, Fact<bool> Autohack);

/// <summary>One map portal's state: whether its pocket map exists and its map id, and an ancient hatch's stockpile type and layout.</summary>
public sealed record Portal(Fact<bool> PocketMapExists, Fact<int> PocketMapId, Fact<string> StockpileType, Fact<string> Layout);
