using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Presentation;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Presentation;

[JsonConverter(typeof(WireEnumConverter<OverlayStyle>))]
public enum OverlayStyle
{
    /// <summary>
    /// Every cell filled.
    /// </summary>
    [WireName("OVERLAY_STYLE_FILL")]
    Fill = 1,
    /// <summary>
    /// Only the boundary edges of the shape's cells.
    /// </summary>
    [WireName("OVERLAY_STYLE_OUTLINE")]
    Outline = 2,
}

/// <summary>
/// Contract-only: capabilities must deny all player-control RPCs to automation.
/// Every optional fact requires Has* checks. Bounds/admission live in normal code.
/// </summary>
public sealed record ReadRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }
}

public sealed record ColonistRosterRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    public required bool CurrentMapOnly { get; init; }
}

public sealed record NotificationsRequest
{
    public RimGovernor.Brain.GameFacts.Common.Identity? Identity { get; init; }

    /// <summary>The newest letters to list, at least 1; read only when <see cref="IncludeLetters"/> is set.</summary>
    public required uint LetterLimit { get; init; }

    public required uint MessageLimit { get; init; }

    public required uint AlertLimit { get; init; }

    /// <summary>
    /// False omits that section from the snapshot.
    /// </summary>
    public required bool IncludeLetters { get; init; }

    public required bool IncludeMessages { get; init; }

    public required bool IncludeAlerts { get; init; }
}

public sealed record RenderDemand
{
    public required PlayerIdentity Viewer { get; init; }

    public required uint LeaseSeconds { get; init; }
}

/// <summary>
/// RimGovernor map overlay: a native MapComponent draws named layers
/// as meshes and labels. Output only and never saved: each call replaces
/// the one layer it names and leaves the others; the brain resends after
/// a load. Layer ids group producers: "layout", "heat.colonist", ...
/// </summary>
public sealed record OverlayColor
{
    public required float R { get; init; }

    public required float G { get; init; }

    public required float B { get; init; }

    public required float A { get; init; }
}

/// <summary>
/// A row run: cells (x .. x+length-1, z).
/// </summary>
public sealed record OverlayRun
{
    public required int X { get; init; }

    public required int Z { get; init; }

    public required int Length { get; init; }
}

public sealed record OverlayShape
{
    public required OverlayColor Color { get; init; }

    public required OverlayStyle Style { get; init; }

    /// <summary>
    /// Inclusive.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<MapRect>))]
    public required EquatableArray<MapRect> Rects { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayRun>))]
    public required EquatableArray<OverlayRun> Runs { get; init; }

    /// <summary>
    /// Drawn centre to centre in the shape's color.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayLine>))]
    public required EquatableArray<OverlayLine> Lines { get; init; }
}

/// <summary>
/// A segment between two map cells; arrow puts a head on `to`.
/// </summary>
public sealed record OverlayLine
{
    public required RimGovernor.Brain.Domain.Cell From { get; init; }

    public required RimGovernor.Brain.Domain.Cell To { get; init; }

    public required bool Arrow { get; init; }
}

/// <summary>
/// Text drawn at a map cell while the layer is visible.
/// </summary>
public sealed record OverlayLabel
{
    public required string Text { get; init; }

    public required RimGovernor.Brain.Domain.Cell Cell { get; init; }
}

public sealed record OverlayRequest
{
    public required RimGovernor.Brain.GameFacts.Common.Identity Identity { get; init; }

    public required string LayerId { get; init; }

    /// <summary>
    /// false removes the layer.
    /// </summary>
    public required bool Enabled { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayShape>))]
    public required EquatableArray<OverlayShape> Shapes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<OverlayLabel>))]
    public required EquatableArray<OverlayLabel> Labels { get; init; }
}
