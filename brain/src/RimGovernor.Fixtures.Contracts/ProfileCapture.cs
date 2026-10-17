#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>The arguments of <c>test/profile_capture</c>: how many captures, and whether to run the ColonyFacts equality probe after.</summary>
public sealed record ProfileCaptureRequest
{
    public int Count { get; init; }

    public bool Equality { get; init; }
}

/// <summary>One ObservationWork section of a capture.</summary>
public sealed record ProfileCaptureSection
{
    public string Name { get; init; } = "";

    public double Ms { get; init; }

    public long Rows { get; init; }

    public bool Detail { get; init; }
}

/// <summary>One SnapshotFrames.Capture call: its total and its sections.</summary>
public sealed record ProfileCaptureSample
{
    public double TotalMs { get; init; }

    public IReadOnlyList<ProfileCaptureSection> Sections { get; init; } = [];
}

/// <summary>
/// The ColonyFacts equality probe's full reply as <c>test/profile_capture</c> carries it (<see cref="FactsEquality"/> is
/// the subset <c>test/colony_facts_equality</c>'s case reads); profile-capture prints and records it whole, in this order.
/// </summary>
public sealed record ProfileEquality
{
    public bool Equal { get; init; }

    public bool ResourcesEqual { get; init; }

    public long ResourceRows { get; init; }

    public double ResourcesOffMs { get; init; }

    public double ResourcesOnMs { get; init; }

    public long OffBytes { get; init; }

    public long OnBytes { get; init; }

    public long UpkeepItems { get; init; }

    public long UpkeepStructures { get; init; }

    public long UpkeepFilth { get; init; }

    public long UpkeepBeds { get; init; }

    public long WasteRows { get; init; }
}

/// <summary>The reply of <c>test/profile_capture</c>: the raw captures, the colony they ran on and the equality probe's reply when asked.</summary>
public sealed record ProfileCaptureReply
{
    public long Tick { get; init; }

    public long FrameBytes { get; init; }

    public long Pawns { get; init; }

    public long Things { get; init; }

    public IReadOnlyList<ProfileCaptureSample> Captures { get; init; } = [];

    public ProfileEquality? Equality { get; init; }
}
