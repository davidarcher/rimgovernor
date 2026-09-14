using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Test-only incident setup and ordinary ordered jobs; no stock, pawn or save edits.
    public sealed class TradeFixture
    {
        [Tool("test/trade_fixture", Description = "Disposable trade acceptance setup/readback; excluded from production builds and model execution.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "snapshot", string traderId = null, string pawnId = null)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Pause a disposable colony first.");
                if (action == "incident" || action == "visitor_incident")
                {
                    var def = DefDatabase<IncidentDef>.GetNamed(action == "incident" ? "TraderCaravanArrival" : "VisitorGroup");
                    var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                    var kind = DefDatabase<TraderKindDef>.GetNamed(action == "incident"
                        ? "Caravan_Outlander_BulkGoods" : "Visitor_Neolithic_Standard");
                    parms.faction = Find.FactionManager.AllFactions.First(f => !f.IsPlayer && !f.defeated
                        && !f.HostileTo(Faction.OfPlayer) && (action == "incident"
                            ? f.def.caravanTraderKinds.Contains(kind) : f.def.visitorTraderKinds.Contains(kind)));
                    if (action == "incident") parms.traderKind = kind;
                    var eligible = def.Worker.CanFireNow(parms);
                    var applied = eligible && def.Worker.TryExecute(parms);
                    var traderIds = applied
                        ? map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == parms.faction && p.trader != null && p.trader.traderKind != null)
                            .Select(p => p.GetUniqueLoadID()).ToArray()
                        : new string[0];
                    return new { eligible, applied, definition = def.defName, traderKind = kind.defName,
                        faction = parms.faction.Name, traderIds };
                }
                if (action == "teleport_adjacent")
                {
                    var traderPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == traderId);
                    var negotiatorPawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
                    if (traderPawn == null || negotiatorPawn == null) throw new InvalidOperationException("Exact trader and negotiator are required.");
                    var cell = GenAdjFast.AdjacentCells8Way(traderPawn.Position)
                        .FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.Walkable(map));
                    if (!cell.IsValid) throw new InvalidOperationException("No standable cell adjacent to the trader.");
                    negotiatorPawn.Position = cell;
                    negotiatorPawn.Notify_Teleported();
                    return new { success = true, x = cell.x, y = cell.y, z = cell.z };
                }
                // Read back the exact raw fields NativeTradeOperations.TraderToken/
                // NegotiatorToken hash, so an acceptance harness can self-compute
                // those tokens client side without guessing at RimWorld's
                // IntVec3.ToString() format or its pawn altitude-layer constant.
                if (action == "state")
                {
                    var traderPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == traderId);
                    var negotiatorPawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
                    if (traderPawn == null || negotiatorPawn == null) throw new InvalidOperationException("Exact trader and negotiator are required.");
                    return new {
                        trader = new {
                            x = traderPawn.Position.x, y = traderPawn.Position.y, z = traderPawn.Position.z,
                            canTradeNow = traderPawn.CanTradeNow,
                            dismissed = traderPawn.mindState != null && traderPawn.mindState.traderDismissed,
                        },
                        negotiator = new {
                            x = negotiatorPawn.Position.x, y = negotiatorPawn.Position.y, z = negotiatorPawn.Position.z,
                            downed = negotiatorPawn.Downed, dead = negotiatorPawn.Dead,
                            mental = negotiatorPawn.InMentalState,
                            socialDisabled = negotiatorPawn.WorkTagIsDisabled(WorkTags.Social),
                        },
                    };
                }
                var trader = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == traderId);
                var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
                if (action == "clear_trade_home")
                {
                    if (trader == null || pawn == null) throw new InvalidOperationException("Exact participants required.");
                    var cells = ((ITrader)trader).ColonyThingsWillingToBuy(pawn)
                        .Where(t => t.Spawned && t.def.category == ThingCategory.Item).Select(t => t.Position).Distinct().ToArray();
                    var designator = new Designator_AreaHomeClear();
                    var cleared = 0;
                    foreach (var cell in cells)
                        if (designator.CanDesignateCell(cell).Accepted)
                        {
                            designator.DesignateSingleCell(cell);
                            cleared++;
                        }
                    return new { cleared, silverStillEligible = ((ITrader)trader).ColonyThingsWillingToBuy(pawn)
                        .Any(t => t.def == ThingDefOf.Silver && t.stackCount > 0) };
                }
                if (action == "trade_job" || action == "dismiss_job")
                {
                    if (trader == null || pawn == null || !trader.CanTradeNow || trader.mindState.traderDismissed
                        || pawn.skills.GetSkill(SkillDefOf.Social).TotallyDisabled
                        || !pawn.CanReach(trader, PathEndMode.OnCell, Danger.Deadly)
                        || !pawn.CanTradeWith(trader.Faction, trader.TraderKind).Accepted)
                        throw new InvalidOperationException("Ordinary trade input is unavailable.");
                    var definition = action == "trade_job" ? JobDefOf.TradeWithPawn : JobDefOf.DismissTrader;
                    var job = JobMaker.MakeJob(definition, trader);
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    // An adjacent dismissal can complete and return its Job to the pool immediately.
                    return new { ordered = pawn.CurJobDef == definition || trader.mindState.traderDismissed,
                        job = definition.defName };
                }
                if (action != "snapshot") throw new ArgumentException("Unknown fixture action.");
                Func<Thing, object> describe = t => new { id = t.ThingID, defName = t.def.defName,
                    count = t.stackCount, spawned = t.Spawned, forbidden = t.Spawned && t.IsForbidden(Faction.OfPlayer),
                    traderProtected = trader?.GetLord()?.extraForbiddenThings.Contains(t) ?? false,
                    x = t.Position.x, z = t.Position.z };
                return new { tick = Find.TickManager.TicksGame,
                    traderPresent = trader != null, traderDismissed = trader?.mindState.traderDismissed,
                    dialogOpen = Find.WindowStack.WindowOfType<Dialog_Trade>() != null,
                    negotiator = TradeSession.Active ? TradeSession.playerNegotiator.GetUniqueLoadID() : null,
                    ground = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item).Select(describe).ToArray(),
                    goods = trader?.trader?.Goods.Where(t => t.def.category == ThingCategory.Item).Select(describe).ToArray(),
                    colonists = map.mapPawns.FreeColonistsSpawned.Select(p => new { id = p.GetUniqueLoadID(), name = p.LabelShort,
                        social = p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled ? -1 : p.skills.GetSkill(SkillDefOf.Social).Level,
                        job = p.CurJobDef?.defName, x = p.Position.x, z = p.Position.z }).ToArray() };
            }, cancellationToken);
        }
    }
}
