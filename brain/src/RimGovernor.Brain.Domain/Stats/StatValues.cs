namespace RimGovernor.Brain.Domain;

// The values of the load's def stat table (RimGovernor.Brain.Bridge.DefStatTable): the game's own StatWorker
// evaluation of a ThingDef or TerrainDef with optional stuff, and its adjusted cost list.

/// <summary>One stat evaluation: the final value and whether the game shows the stat for the subject.</summary>
public readonly record struct StatResult(float Value, bool Shown);

/// <summary>An adjusted cost entry: a ThingDef name and a unit count.</summary>
public readonly record struct StatCost(string Def, long Units);

/// <summary>The stat table could not answer: an unknown def, stuff or stat, or a value the game could not compute.</summary>
public sealed class StatException(string message) : DomainException(message);
