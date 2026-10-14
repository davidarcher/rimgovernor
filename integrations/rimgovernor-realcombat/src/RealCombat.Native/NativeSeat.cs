using HarmonyLib;
using HomeBridge.BridgeTools;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.RealCombat.Native;

/// <summary>
/// The seat the native mod gives the controller in a live game, stood up over a headless arena map: the combat mirror
/// observes the fight, and the combat order handler applies the controller's orders under native authority, both as
/// compiled into the mod. What this class adds is only the game-level state those two expect: a colony identity, the
/// control authority granted to the controller, and the hooks that authority audits.
/// </summary>
/// <remarks>
/// <c>Find.CurrentMap</c> stays null on purpose: with a current map the game turns on the view-gated effects (motes,
/// flecks, sounds) that need a camera, and shooting stops. The authority reads its map through
/// <c>NativeControlAuthority.Capture</c>, which is redirected here to the arena map.
/// </remarks>
public sealed class NativeSeat
{
    private static Map? s_map;

    private readonly Map _map;
    private readonly ColonyIdentity _colony;

    private NativeSeat(Map map, ColonyIdentity colony)
    {
        _map = map;
        _colony = colony;
    }

    /// <summary>The wire identity the controller addresses orders with.</summary>
    public Common.Identity Identity => new() { ColonyId = _colony.ColonyId, LoadToken = _colony.LoadToken, MapId = _map.uniqueID };

    /// <summary>Hook installs that failed, as the mirror reports them; empty when every hook is in.</summary>
    public IReadOnlyList<string> HookErrors => [.. CombatMirror.HookErrors];

    /// <summary>Seats the controller on the map of the running game: installs the hooks and grants it control.</summary>
    public static NativeSeat Open(Map map)
    {
        var game = Current.Game;
        var colony = game.GetComponent<ColonyIdentity>();
        if (colony is null)
        {
            colony = new ColonyIdentity(game);
            game.components.Add(colony);
        }

        s_map = map;
        new Harmony("realcombat.native-seat").Patch(
            typeof(NativeControlAuthority).GetMethod("Capture", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingMethodException("NativeControlAuthority.Capture is gone"),
            prefix: new HarmonyMethod(typeof(NativeSeat).GetMethod(nameof(CaptureIdentity), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!));
        CombatMirror.EnsureHooks();
        NativePawnControlState.Initialize();
        var authority = NativeControlAuthority.ForGame(game);
        authority.SetHookHealth(true);
        var granted = authority.SetMode(authority.Status().Generation, NativeControlMode.Auto);
        if (granted.Error != NativeControlError.None || !authority.Status().Active || !NativePawnControlState.IsReady)
        {
            throw new InvalidOperationException($"realcombat: the native control seat is not usable (grant {granted.Error}, ready {NativePawnControlState.IsReady})");
        }

        return new NativeSeat(map, colony);
    }

    /// <summary>The combat mirror's projection of the map now: pawn rows and the event ring.</summary>
    public Observations.BundleSnapshot Capture() => CombatMirror.Capture(_map, new Observations.BundleSnapshot());

    /// <summary>Applies one batch of combat orders in request order and returns the per-order results.</summary>
    public IReadOnlyList<Receipts.CombatOrderResult> Apply(Operations.CombatOrders orders)
    {
        var effect = NativeCombatOrders.Apply(orders, new Common.ObservationContext { Identity = Identity, Tick = Find.TickManager.TicksGame });
        return ((Receipts.EffectEvidenceEffect.CombatOrdersCase)effect.Effect!).Value.Results;
    }

    private static bool CaptureIdentity(ref NativeControlIdentity? __result)
    {
        var game = Current.Game;
        var colony = game?.GetComponent<ColonyIdentity>();
        __result = game is null || s_map is null || colony is null ? null : new NativeControlIdentity(game, s_map, colony.ColonyId, colony.LoadToken);
        return false;
    }
}
