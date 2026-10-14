using HomeBridge.BridgeTools;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Policy;
using RimGovernor.RealCombat;
using Verse;
using Tick = RimGovernor.Brain.Domain.Tick;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.RealCombat.Native;

/// <summary>
/// Plays the colonists with the live combat policy: each decision reads the fight through the mod's own snapshot capture
/// and the brain's own combat read, decides it with <c>DecideCombat</c>, and applies the orders through the mod's order
/// handler, as the defense round does in the game. Nothing here is a combat rule.
/// </summary>
public sealed class PolicyController : IFightController
{
    private const int CheckEvery = 10;
    private const int BackstopTicks = 300;

    private readonly List<PawnId> _orderable = [];
    private NativeSeat? _seat;
    private INativeFacade? _facade;
    private FacadeNativeClient? _client;
    private CombatMemory _memory = new();
    private int _decided = int.MinValue;
    private int _serial;

    /// <summary>Receives one line per decision and per check that changed what a colonist is doing; null is silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The defense layout the colony holds for the fight (unknown when it holds none, so only the squad fallback forms).</summary>
    public RimGovernor.Brain.Domain.Fact<CombatLayout> Layout { get; set; } = RimGovernor.Brain.Domain.Fact.Unknown<CombatLayout>();

    /// <summary>Decisions that sent a batch.</summary>
    public int Decisions { get; private set; }

    /// <summary>Orders the handler applied.</summary>
    public int Applied { get; private set; }

    /// <summary>Orders the handler refused.</summary>
    public int Refused { get; private set; }

    /// <summary>The last failure of a decision, or empty.</summary>
    public string Failure { get; private set; } = "";

    /// <summary>Why the last check did not decide, for a run that decided nothing.</summary>
    public string Idle { get; private set; } = "";

    /// <summary>One line of what the controller did.</summary>
    public string Summary => $"decisions={Decisions} applied={Applied} refused={Refused} idle={Idle} failure={(Failure.Length == 0 ? "none" : Failure)}";

    public void Begin(Map map)
    {
        _seat = NativeSeat.Open(map);
        _facade = PolicyFacade.Create();
        _client = FacadeNativeClient.Create(_facade, CatalogOfArena);
        _client.OpenFramesAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Decide(Map map, int tick)
    {
        if (tick % CheckEvery != 0)
        {
            return;
        }

        if (Log is not null && tick % 30 == 0)
        {
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                Log($"t={tick} {p.LabelShort} {(p.Faction?.IsPlayer == true ? "col" : "host")} pos={p.Position.x},{p.Position.z} hp={p.health.summaryHealth.SummaryHealthPercent:0.00} drafted={p.Drafted} downed={p.Downed} job={p.CurJobDef?.defName} target={p.CurJob?.targetA.Thing?.LabelShort} verb={p.TryGetAttackVerb(null)?.GetType().Name} aiming={p.stances.curStance.GetType().Name}");
            }
        }

        try
        {
            var request = new Obs.SnapshotStreamRequest { LinesOfFireBudgetMs = 15 };
            var frame = SnapshotFrames.Capture(map, request);
            if (frame is null)
            {
                Idle = "no frame";
                return;
            }

            PolicyFacade.Publish(_facade!, new NativeFrame(frame, ++_serial, 0));
            var combat = _client!.ReadCombatAsync(_seat!.Identity, CancellationToken.None).GetAwaiter().GetResult();
            var stop = BuildingRuntimePackage.CombatStop(combat, _memory.Tick);
            if (stop.Kind.IsEmpty && _decided != int.MinValue && tick - _decided < BackstopTicks)
            {
                return;
            }

            var step = BuildingRuntimePackage.DecideCombatStep(combat, _orderable, _memory with { Tick = new Tick(tick) }, $"rc-{tick}", _seat.Identity, GeometryOf, Layout);
            if (step is null)
            {
                Idle = "no fight: " + BuildingRuntimePackage.CombatFrameInputs(combat, []).Reason;
                return;
            }

            _memory = step.Memory;
            _decided = tick;
            if (step.Batch is not { } batch)
            {
                Idle = $"empty batch: tactic={step.Memory.Tactic} roles={step.Memory.Roles.Count} refusal={step.Memory.Refusal} orders={step.Orders.Count} pawns={step.View.Pawns.Count}";
                return;
            }

            Decisions++;
            Log?.Invoke($"t={tick} decide tactic={step.Memory.Tactic} roles=[{string.Join(" ", step.Memory.Roles.Select(r => $"{r.Pawn.Value}->{r.Target.Value}@{r.Cell?.X},{r.Cell?.Z}"))}] stop={stop.Kind.Value}");
            foreach (var o in batch.Orders)
            {
                Log?.Invoke($"   order {o.Pawn?.EntityId} {o.Order}");
            }

            if (Log is not null)
            {
                foreach (var v in step.View.Pawns.Items)
                {
                    Log($"   view {v.Id.Value} dead={v.Dead} downed={v.Downed} bloodLoss={v.BloodLoss} bleed/day={v.BleedRatePerDay} hoursToDeath={v.HoursUntilBleedDeath}");
                }

                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    Log($"   game {p.ThingID} bleedRate={p.health.hediffSet.BleedRateTotal:0.000} bloodLoss={p.health.hediffSet.GetFirstHediffOfDef(RimWorld.HediffDefOf.BloodLoss)?.Severity} hediffs=[{string.Join(", ", p.health.hediffSet.hediffs.Select(h => h.def.defName + ":" + h.Part?.def.defName + ":" + h.Severity.ToString("0.00") + (h.Bleeding ? ":bleeding" : "")))}]");
                }
            }

            var results = _seat.Apply(batch);
            foreach (var r in results)
            {
                Log?.Invoke($"   result {r.PawnId} applied={r.Applied} refusal={r.Refusal} job={r.JobDef}");
            }

            Applied += results.Count(r => r.Applied == true);
            Refused += results.Count(r => r.Applied != true);
            _memory = BuildingRuntimePackage.CombatStepResults(step, results);
            foreach (var d in step.Drafts)
            {
                if (!_orderable.Contains(d))
                {
                    _orderable.Add(d);
                }
            }
        }
        catch (Exception e)
        {
            Failure = e.GetType().Name + ": " + e.Message;
        }
    }

    private RimGovernor.Contracts.Wire.Mirror.CombatGeometry GeometryOf(RimGovernor.Contracts.Wire.Mirror.CombatGeometryRequest request) =>
        _client!.CombatGeometryAsync(request, CancellationToken.None).GetAwaiter().GetResult().Value;

    // The catalog of the load over the game's Defs, with stats read inline: the mod's GameThreadStatService refuses a
    // caller on the game thread, which is where an arena fight is decided.
    private static DefinitionCatalog CatalogOfArena(Obs.DefinitionCatalog wire, RimGovernor.Contracts.Wire.Common.Identity identity)
    {
        var derived = wire.Derived ?? throw new InvalidOperationException("The definition catalog carries no derived facts.");
        var races = new Dictionary<string, RaceGameFacts>(StringComparer.Ordinal);
        foreach (var row in wire.ThingFacts)
        {
            if (row.Race is { } race)
            {
                races.Add(row.DefName, new RaceGameFacts([.. race.EdibleDefs], race.TamenessCanDecay, race.TamenessDecayPeriodTicks, race.TameChanceFactor, race.AdultFeedPerDay));
            }
        }

        return new DefinitionCatalog(NativeCatalogFactory.AllDefs(), InlineStatService.Instance, new CatalogDerived(derived.CurrencyDef, derived.WortDef, derived.FullRotRateC), races);
    }
}
