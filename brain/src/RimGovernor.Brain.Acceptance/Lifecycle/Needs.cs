using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Freezing colonist needs for a harness whose assertion is not about them (Go <c>needs.go</c>).</summary>
public static class Needs
{
    /// <summary>Pins colonist needs at maximum (FreezeNeedsFixture); every fixture build carries it.</summary>
    public const string FreezeNeedsTool = "test/freeze_needs";

    /// <summary>
    /// Pins every free colonist need except <paramref name="keep"/> (NeedDef names such as Food, Rest, Joy) for the
    /// rest of the game, so a harness whose assertion is not about eating, sleeping or mood never loses a colonist
    /// to them. The reply names what was frozen; record it on the report. A missing op is an error: harnesses that
    /// freeze need a fixture build anyway. <paramref name="names"/> null fetches the catalog.
    /// </summary>
    public static async Task<JsonObject> FreezeNeedsAsync(Harness harness, IReadOnlyList<string>? names, IEnumerable<string> keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(keep);
        names ??= await harness.DiscoveryAsync(cancellationToken).ConfigureAwait(false) ?? [];
        if (!Session.HasTool(names, FreezeNeedsTool))
        {
            throw new AcceptanceException($"{FreezeNeedsTool} is not installed: build the mod with a fixture");
        }

        var reply = await harness.CallAsync(
            "freeze-needs",
            FreezeNeedsTool,
            new JsonObject { ["action"] = "apply", ["keep"] = string.Join(',', keep) },
            cancellationToken).ConfigureAwait(false);
        return reply["success"].AsBool() == true
            ? reply
            : throw new AcceptanceException($"{FreezeNeedsTool} refused: {reply.ToJson()}");
    }

    /// <summary>
    /// Freezes every colonist need but <paramref name="keep"/> and records the reply under
    /// <c>report["frozen_needs"]</c>: the one call every service-driven harness makes once its save is loaded and
    /// staged, so colonists never eat, sleep or break through an assertion that is not about them.
    /// <see cref="NeedDef.LiveNeeds"/> in keep skips the freeze and leaves frozen_needs unset.
    /// </summary>
    public static async Task RecordFrozenNeedsAsync(Harness harness, IReadOnlyList<string>? names, Report report, IReadOnlyList<NeedDef> keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(keep);
        var kept = new List<string>(keep.Count);
        foreach (var need in keep)
        {
            if (need == NeedDef.LiveNeeds)
            {
                return;
            }

            kept.Add(need.Value);
        }

        report["frozen_needs"] = await FreezeNeedsAsync(harness, names, kept, cancellationToken).ConfigureAwait(false);
    }
}
