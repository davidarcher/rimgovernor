#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the waste and burial cases' bespoke fixture ops (#3408): each stages physical facts the stage
// primitives cannot express (rotted corpses, worn gear, a morgue, a sarcophagus) and reads back where vanilla hauling
// put them. A record carries only what a case reads.

/// <summary>A staged item a case follows: its label and unique load id.</summary>
public sealed record StagedItem
{
    public string Label { get; init; } = "";

    public string Id { get; init; } = "";
}

/// <summary><c>test/burnable_stage</c>: the staged items and the silver cutoff the burnable rule judges gear by.</summary>
public sealed record BurnableStaged
{
    public bool Success { get; init; }

    public IReadOnlyList<StagedItem> Items { get; init; } = [];

    public double Cutoff { get; init; }
}

/// <summary><c>test/burnable_read</c>: one row per requested id, in request order.</summary>
public sealed record BurnableRead
{
    public IReadOnlyList<BurnableRow> Rows { get; init; } = [];
}

/// <summary>Where a staged item stands and what the burnable filters say about it.</summary>
public sealed record BurnableRow
{
    public bool Present { get; init; }

    public string Def { get; init; } = "";

    /// <summary>The holding stockpile's StoragePriority ("Low" for the dump, "Normal" for the store); empty outside both.</summary>
    public string Priority { get; init; } = "";

    public double MarketValue { get; init; }

    public bool Burnable { get; init; }

    public bool NotBurnable { get; init; }

    /// <summary>The holding stockpile's own settings admit the item.</summary>
    public bool ZoneAllows { get; init; }
}

/// <summary><c>test/colony_facts_equality</c>: whether ColonyFacts and ResourceSources read the same with read optimizations off and on, and the rows compared.</summary>
public sealed record FactsEquality
{
    public bool Equal { get; init; }

    public int WasteRows { get; init; }

    public int UpkeepItems { get; init; }

    public int ResourceRows { get; init; }
}

/// <summary><c>test/burial_stage</c>: the morgue's colonist and stranger corpses and the free grave.</summary>
public sealed record BurialStaged
{
    public bool Success { get; init; }

    public string ColonistCorpse { get; init; } = "";

    public string StrangerCorpse { get; init; } = "";

    public string Grave { get; init; } = "";
}

/// <summary><c>test/burial_read</c>: where each requested corpse lies.</summary>
public sealed record BurialRead
{
    public IReadOnlyList<BurialThing> Things { get; init; } = [];
}

public sealed record BurialThing
{
    public string Id { get; init; } = "";

    public bool InGrave { get; init; }

    /// <summary>The corpse lies spawned in a stockpile zone (the morgue).</summary>
    public bool Zone { get; init; }
}

/// <summary><c>test/tomb_stage</c>: the fresh sarcophagus and stranger corpse.</summary>
public sealed record TombStaged
{
    public bool Success { get; init; }

    public string Sarcophagus { get; init; } = "";

    public string Corpse { get; init; } = "";
}

/// <summary><c>test/tomb_read</c>: the sarcophagus contents, each colonist's KnowBuriedInSarcophagus memories and the listed corpses.</summary>
public sealed record TombRead
{
    public bool Holds { get; init; }

    public IReadOnlyList<TombColonist> Colonists { get; init; } = [];

    public IReadOnlyList<TombCorpse> Corpses { get; init; } = [];
}

public sealed record TombColonist
{
    public string Id { get; init; } = "";

    /// <summary>The colonist has a mood need, so a memory could land.</summary>
    public bool Mood { get; init; }

    public int Memories { get; init; }
}

public sealed record TombCorpse
{
    public string Id { get; init; } = "";

    public bool InSarcophagus { get; init; }

    public bool EverBuried { get; init; }
}
