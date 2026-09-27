using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Empty stand-in for the retired #726 label component, so saves that
    /// carry it load without a missing-class error. Its saved lists are
    /// dropped. Removed at Layout v2 D1 with the legacy plan cleanup.
    /// </summary>
    public sealed class LayoutPlanLabels : MapComponent
    {
        public LayoutPlanLabels(Map map) : base(map) { }
    }
}
