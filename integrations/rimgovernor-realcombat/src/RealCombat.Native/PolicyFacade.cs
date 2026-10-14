using System.Reflection;
using System.Threading.Channels;
using HomeBridge.BridgeTools;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.NativeFacade;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.RealCombat.Native;

/// <summary>
/// The slice of <see cref="INativeFacade"/> a combat read touches, served over the arena: the definition catalog, the
/// frame stream (frames are published by the controller, one per decision) and the stat service. The facade is 64
/// calls wide; any other is not a combat read and throws by name, so a read that grows past this slice fails loudly.
/// </summary>
public class PolicyFacade : DispatchProxy
{
    private readonly FrameHandoff _frames = new();

    internal static INativeFacade Create()
    {
        var facade = Create<INativeFacade, PolicyFacade>();
        ((PolicyFacade)(object)facade)._frames.Attach();
        return facade;
    }

    internal static void Publish(INativeFacade facade, NativeFrame frame) => ((PolicyFacade)(object)facade)._frames.Publish(frame);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
            case "get_Stats":
                return InlineStatService.Instance;
            case "get_Frames":
                return _frames.Reader;
            case "ReadDefinitionCatalogAsync":
                return Task.FromResult(NativeDefinitionCatalogTool.HandleReadDefinitionCatalog((Obs.DefinitionCatalogRequest)args![0]!));
            case "OpenFramesAsync":
                return Task.FromResult(new Obs.SnapshotStreamReply { Outcome = new Obs.SnapshotStreamReplyOutcome.OpenedCase(new Obs.SnapshotStreamOpened { Name = "in-process" }) });
            default:
                throw new NotSupportedException($"realcombat: the policy facade serves a combat read only; {targetMethod.Name} is not part of one");
        }
    }
}
