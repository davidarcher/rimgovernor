namespace RimGovernor.Brain.Runtime;

internal static class ZonePreviews
{
    /// <summary>
    /// Runs a zone preview and separates the game refusing the site (a cell the zone designator will not take)
    /// from a failed read. A refusal is a site outcome the planner moves on from, as the building planners do
    /// with an unplaceable preview; every other error still fails the step. The native detail names the refusing
    /// condition. Reply is null when the site is refused; Detail then says why.
    /// </summary>
    internal static async Task<(RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply? Reply, string Detail)> PreviewZoneAsync(GameClient native, RimGovernor.Brain.GameFacts.Common.Identity identity, ZoneCreate target, CancellationToken ct)
    {
        RimGovernor.Brain.GameFacts.Operations.ZonePreviewReply reply;
        try
        {
            reply = await native.PreviewZoneAsync(identity, target, ct).ConfigureAwait(false);
        }
        catch (NativeFailureException refusal)
        {
            var detail = (refusal.Value.Detail ?? "");
            if (detail.Length == 0)
            {
                detail = refusal.Value.Code.ToString();
            }
            return (null, detail);
        }
        if (reply.Outcome?.Evaluated is not { Accepted: true })
        {
            return (null, "preview not accepted");
        }
        return (reply, "");
    }
}
