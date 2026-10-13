namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Runs a zone preview and separates the game refusing the site (a cell the zone designator will not take)
    /// from a failed read. A refusal is a site outcome the planner moves on from, as the building planners do
    /// with an unplaceable preview; every other error still fails the step. The native detail names the refusing
    /// condition. Reply is null when the site is refused; Detail then says why.
    /// </summary>
    internal static (OperationsPb.ZonePreviewReply? Reply, string Detail) PreviewZone(CancellationToken ct, IFieldNative native, C.Identity identity, ZoneCreate target)
    {
        OperationsPb.ZonePreviewReply reply;
        try
        {
            (reply, _) = native.PreviewZone(ct, identity, target);
        }
        catch (Bridge.NativeFailure refusal)
        {
            var detail = refusal.Value.Detail;
            if (detail.Length == 0)
            {
                detail = refusal.Value.Code.ToString();
            }
            return (null, detail);
        }
        if (reply.Evaluated is not { Accepted: true })
        {
            return (null, "preview not accepted");
        }
        return (reply, "");
    }
}
