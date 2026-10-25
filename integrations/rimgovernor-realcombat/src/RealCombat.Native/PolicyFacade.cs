using System.Reflection;
using System.Threading.Channels;
using RimGovernor.Native;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.RealCombat.Native;

/// <summary>
/// The slice of <see cref="IGameApi"/> a combat read touches, served over the arena: the definition catalog, combat geometry, the defense site census (the burn survey), the
/// frame stream (frames are published by the controller, one per decision). The facade is 64
/// calls wide; any other is not a combat read and throws by name, so a read that grows past this slice fails loudly.
/// </summary>
public class PolicyFacade : DispatchProxy
{
    private readonly FrameHandoff _frames = new();

    internal static IGameApi Create()
    {
        var facade = Create<IGameApi, PolicyFacade>();
        ((PolicyFacade)(object)facade)._frames.Attach();
        return facade;
    }

    internal static void Publish(IGameApi facade, NativeFrame frame) => ((PolicyFacade)(object)facade)._frames.Publish(frame);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
            case "get_Frames":
                return _frames.Reader;
            case "ReadDefinitionCatalogAsync":
                return Task.FromResult(NativeDefinitionCatalogTool.HandleReadDefinitionCatalog((RimGovernor.Brain.GameOrders.Observations.DefinitionCatalogRequest)args![0]!));
            case "ReadCombatGeometryAsync":
                return Task.FromResult(NativeCombatGeometryTools.HandleRead((RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest)args![0]!));
            case "ReadDefenseSiteAsync":
                return Task.FromResult(NativeDefenseObservationTools.HandleReadDefenseSite((RimGovernor.Brain.GameOrders.Observations.DefenseSiteRequest)args![0]!));
            case "OpenFramesAsync":
                return Task.FromResult(new RimGovernor.Brain.GameFacts.Observations.SnapshotStreamReply());
            default:
                throw new NotSupportedException($"realcombat: the policy facade serves a combat read only; {targetMethod.Name} is not part of one");
        }
    }
}
