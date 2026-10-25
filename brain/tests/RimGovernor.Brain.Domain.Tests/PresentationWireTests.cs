using System.Text.Json;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Presentation;
using RimGovernor.Brain.GameOrders.Presentation;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A presentation request or reply names every fact (#3959): the overlay layer, render lease and notification
/// limits are required values, so a body that leaves one out is refused instead of read as a default.
/// </summary>
public sealed class PresentationWireTests
{
    private const string Identity = """{"colonyId":"c","loadToken":"l","mapId":0}""";

    [Fact]
    public void NotificationsRequestNamesEveryLimitAndFlag()
    {
        var full = $$"""{"identity":{{Identity}},"letterLimit":40,"messageLimit":12,"alertLimit":40,"includeLetters":true,"includeMessages":false,"includeAlerts":true}""";
        var request = OrdersJson.Deserialize<NotificationsRequest>(full);
        Assert.Equal(40u, request.LetterLimit);
        Assert.False(request.IncludeMessages);
        Assert.Equal(request, OrdersJson.Deserialize<NotificationsRequest>(OrdersJson.Serialize(request)));

        var missing = $$"""{"identity":{{Identity}},"letterLimit":40,"messageLimit":12,"alertLimit":40,"includeLetters":true,"includeAlerts":true}""";
        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<NotificationsRequest>(missing));
    }

    [Fact]
    public void RenderDemandAndOverlayRequireTheirFacts()
    {
        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<RenderDemand>("""{"viewer":{"identity":""" + Identity + "}}"));
        Assert.ThrowsAny<JsonException>(() => OrdersJson.Deserialize<OverlayRequest>($$"""{"identity":{{Identity}},"layerId":"x","shapes":[],"labels":[]}"""));
        var overlay = OrdersJson.Deserialize<OverlayRequest>($$"""{"identity":{{Identity}},"layerId":"x","enabled":false,"shapes":[],"labels":[]}""");
        Assert.False(overlay.Enabled);
    }
}
