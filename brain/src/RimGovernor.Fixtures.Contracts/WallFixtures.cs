#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>A map cell a wall fixture names.</summary>
public sealed record FixtureCell
{
    public int X { get; init; }

    public int Z { get; init; }
}

/// <summary>
/// The reply of <c>test/player_designate</c>: a player (not supervisor) Deconstruct designation laid on one thing, as a
/// player's own order the supervisor may adopt. The primitives and the stage cannot lay a designation.
/// </summary>
public sealed record PlayerDesignateReply
{
    public bool Success { get; init; }

    /// <summary>Whether the thing carries a Deconstruct designation after the call.</summary>
    public bool Designated { get; init; }
}

/// <summary>
/// The reply of <c>test/wall_layers</c>. prepare and audit fill the ring counts; controls fills the completion
/// controls (a sealing completion refused and skipped, a pawn-on-frame completion relocating the pawn, who escapes).
/// </summary>
public sealed record WallLayersReply
{
    public bool Success { get; init; }

    public int Expected { get; init; }

    public int Frames { get; init; }

    public int Funded { get; init; }

    public int Standable { get; init; }

    public int Standing { get; init; }

    public int Trapped { get; init; }

    public bool Refused { get; init; }

    public bool Skipped { get; init; }

    public bool Completed { get; init; }

    public bool Escaped { get; init; }
}

/// <summary>The reply of <c>test/wall_replacement</c>: the target cell and the per-tick enclosure audit of its run action.</summary>
public sealed record WallReplacementReply
{
    public bool Success { get; init; }

    public FixtureCell? Centre { get; init; }

    public int Violations { get; init; }

    public bool Impassable { get; init; }

    public string BuiltDef { get; init; } = "";

    public string BuiltStuff { get; init; } = "";

    public string FrameDef { get; init; } = "";

    public bool FrameStandable { get; init; }
}

/// <summary>One loose WoodLog stack <c>test/remote_pickup</c> reports.</summary>
public sealed record PickupPile
{
    public int X { get; init; }

    public int Z { get; init; }

    public int Count { get; init; }
}

/// <summary>The reply of <c>test/remote_pickup</c>: the frame's delivered wood, the loose stacks and the carriers.</summary>
public sealed record RemotePickupReply
{
    public bool Success { get; init; }

    public int Delivered { get; init; }

    public int Cost { get; init; }

    public IReadOnlyList<PickupPile> Loose { get; init; } = [];

    public int Carrying { get; init; }

    public int Capacity { get; init; }

    /// <summary>The last staged wall frame.</summary>
    public FixtureCell? Site { get; init; }

    /// <summary>The south-west cell of the hut's 3x3 interior.</summary>
    public FixtureCell? Interior { get; init; }

    public FixtureCell? Door { get; init; }
}

/// <summary>
/// The reply of <c>test/construction_skill</c>. prepare names the bed blueprint and its cell; convert and audit read the
/// bed site's finishing setting and eligibility; controls reads the direct-completion controls.
/// </summary>
public sealed record ConstructionSkillReply
{
    public bool Success { get; init; }

    public string Target { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }

    public int? Minimum { get; init; }

    public int? Tier { get; init; }

    public bool LowRefused { get; init; }

    public bool HighAllowed { get; init; }

    public bool WallProgress { get; init; }

    public bool BedPending { get; init; }

    public bool LowCompletionRefused { get; init; }

    public bool Completed { get; init; }

    public bool NoLeak { get; init; }
}

/// <summary>One staged tier-gate wall blueprint.</summary>
public sealed record TierSite
{
    public int X { get; init; }

    public int Z { get; init; }

    public string Target { get; init; } = "";
}

/// <summary>The delivery job the patched work-giver hands the builder for one tier-gate site.</summary>
public sealed record TierJob
{
    public string? Job { get; init; }

    public bool Reachable { get; init; }
}

/// <summary>The reply of <c>test/construction_tier_gate</c>: prepare names the four sites; query and enclose read each site's job.</summary>
public sealed record TierGateReply
{
    public bool Success { get; init; }

    public IReadOnlyList<TierSite> Sites { get; init; } = [];

    public bool SupervisorActive { get; init; }

    public TierJob? A { get; init; }

    public TierJob? B { get; init; }

    public TierJob? C { get; init; }

    public TierJob? D { get; init; }
}
