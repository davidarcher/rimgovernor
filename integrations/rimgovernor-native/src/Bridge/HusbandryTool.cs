#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    public sealed class HusbandryTools
    {
        private static string Census(Pawn p) => string.Join(",", p.Map.mapPawns.AllPawnsSpawned
            .Where(a => a.def == p.def && a.Faction == Faction.OfPlayer && !a.Dead)
            .Select(a => a.GetUniqueLoadID() + ":" + a.gender + ":" + a.ageTracker.CurLifeStage.reproductive + ":" + a.Sterile()).OrderBy(id => id));
        private static bool Designated(Pawn p, DesignationDef def) => p.Map.designationManager.DesignationOn(p, def) != null;
        private static string Settings(Pawn p)
        {
            var values = new List<string> { p.GetUniqueLoadID(), p.Faction?.GetUniqueLoadID() ?? "",
                p.playerSettings?.Master?.GetUniqueLoadID() ?? "", p.playerSettings?.AreaRestrictionInPawnCurrentMap?.GetUniqueLoadID() ?? "",
                p.playerSettings?.followDrafted.ToString() ?? "", p.playerSettings?.followFieldwork.ToString() ?? "",
                Designated(p, DesignationDefOf.Slaughter).ToString(),
                Designated(p, DesignationDefOf.ReleaseAnimalToWild).ToString() };
            values.AddRange(TrainableUtility.GetAllColonistBondsFor(p).Select(b => b.GetUniqueLoadID()).OrderBy(x => x));
            if (p.training != null)
                values.AddRange(DefDatabase<TrainableDef>.AllDefsListForReading.OrderBy(t => t.defName)
                    .Select(t => t.defName + "=" + p.training.GetWanted(t)));
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", values)))).Replace("-", "");
        }

        private static bool SafeToSlaughter(Pawn p) => !p.Dead && !p.Downed && !p.InMentalState
            && p.Faction == Faction.OfPlayer && p.playerSettings != null && p.playerSettings.Master == null
            && !TrainableUtility.GetAllColonistBondsFor(p).Any()
            && !p.health.hediffSet.hediffs.OfType<Hediff_Pregnant>().Any()
            && !Designated(p, DesignationDefOf.ReleaseAnimalToWild)
            && new Designator_Slaughter().CanDesignateThing(p).Accepted;

        private static object Animal(Pawn p)
        {
            var animal = PawnSettingsRead.AnimalBlock(p);
            var produce = animal["produce"] as Dictionary<string, object?> ?? throw new InvalidOperationException("Animal block has no produce record.");
            var training = animal["training"] as Dictionary<string, object?> ?? throw new InvalidOperationException("Animal block has no training record.");
            var penned = AnimalPenUtility.NeedsToBeManagedByRope(p);
            var pen = penned ? AnimalPenUtility.GetCurrentPenOf(p, false) : null;
            return new { id = p.GetUniqueLoadID(), race = p.def.defName, label = p.LabelShort,
                gender = p.gender.ToString(), ageYears = p.ageTracker.AgeBiologicalYearsFloat,
                fertileAdult = p.ageTracker.CurLifeStage.reproductive && !p.Sterile() && !p.RaceProps.disableMating,
                minimumHandlingSkill = TrainableUtility.MinimumHandlingSkill(p),
                handlers = p.Map.mapPawns.FreeColonistsSpawned.Where(h => !h.Dead && !h.Downed && !h.Drafted
                    && !h.InMentalState && !h.WorkTypeIsDisabled(WorkTypeDefOf.Handling)
                    && h.skills?.GetSkill(SkillDefOf.Animals)?.Level >= TrainableUtility.MinimumHandlingSkill(p)
                    && h.CanReach(p, PathEndMode.Touch, Danger.None)).Select(h => new {
                        id = h.GetUniqueLoadID(), priority = h.workSettings.GetPriority(WorkTypeDefOf.Handling),
                        job = h.CurJobDef?.defName, target = h.CurJob?.targetA.Thing?.GetUniqueLoadID() }).ToArray(),
                settingsToken = Settings(p), censusToken = Census(p), safeToSlaughter = SafeToSlaughter(p),
                slaughter = Designated(p, DesignationDefOf.Slaughter),
                release = Designated(p, DesignationDefOf.ReleaseAnimalToWild),
                contained = penned ? pen != null && pen.PenState.Enclosed :
                    p.playerSettings?.AreaRestrictionInPawnCurrentMap == null || p.playerSettings.AreaRestrictionInPawnCurrentMap[p.Position],
                pen = pen?.parent.GetUniqueLoadID(), training = training["trainables"],
                milkFull = produce["milkFull"], woolFull = produce["woolFull"],
                milkFullness = produce["milkFullness"], woolFullness = produce["woolFullness"],
                pregnant = produce["pregnant"], gestation = produce["gestationProgress"],
                parents = p.relations.DirectRelations.Where(r => r.def == PawnRelationDefOf.Parent)
                    .Select(r => r.otherPawn.GetUniqueLoadID()).ToArray(),
                foodLevel = p.needs?.food?.CurLevel, job = p.CurJobDef?.defName };
        }

        [Tool("home/husbandry_facts", Title = "Native herd observations",
            Description = "Read current-map player animals, actual enclosed pen membership, native training progress, pregnancy and parent identities, product readiness and player settings tokens. No grass or future birth is credited as feed or stock.")]
        public async Task<object> Facts(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (map == null || identity == null) return new { success = false, error = "No colony/map identity" };
                return new { success = true, colonyId = identity.ColonyId, loadToken = identity.LoadToken,
                    mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                    corpses = map.listerThings.AllThings.OfType<Corpse>().Where(c => c.InnerPawn?.RaceProps.Animal == true)
                        .Select(c => new { id = c.GetUniqueLoadID(), animal = c.InnerPawn.GetUniqueLoadID(), dead = c.InnerPawn.Dead }).ToArray(),
                    animals = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && !p.Dead
                        && p.Faction == Faction.OfPlayer).OrderBy(p => p.thingIDNumber).Select(Animal).ToArray() };
            }, cancellationToken);
        }

        [Tool("home/husbandry_config", Title = "Guarded animal management",
            Description = "Request one native recursive training setting or slaughter designation. Requires paused exact identity and unchanged animal settings. Slaughter refuses bonded, mastered, pregnant, downed, released or otherwise ineligible animals. Does not train instantly, create animals or gather products.")]
        public async Task<object> Configure(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact colony ID")] string colonyId,
            [ToolParameter(Description = "Exact load token")] string loadToken,
            [ToolParameter(Description = "Exact map ID")] int mapId,
            [ToolParameter(Description = "Exact observed animal load ID")] string animal,
            [ToolParameter(Description = "Exact observed settingsToken")] string expected,
            [ToolParameter(Description = "Exact observed censusToken; population changes invalidate this request")] string census,
            [ToolParameter(Description = "Native TrainableDef to request; omit for slaughter")] string? trainable = null,
            [ToolParameter(Description = "Designate eligible surplus animal; explicit player policy required")] bool slaughter = false,
            [ToolParameter(Description = "Preview only", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (map == null || identity == null || identity.ColonyId != colonyId || identity.LoadToken != loadToken
                    || map.uniqueID != mapId || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                    return new { success = false, error = "Paused exact colony/load/map required" };
                var pawn = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == animal);
                if (pawn == null || !pawn.RaceProps.Animal || pawn.Faction != Faction.OfPlayer || pawn.Dead
                    || Settings(pawn) != expected || Census(pawn) != census || slaughter == !string.IsNullOrEmpty(trainable))
                    return new { success = false, error = "Animal/settings changed or exactly one operation required" };
                if (slaughter)
                {
                    if (!SafeToSlaughter(pawn)) return new { success = false, error = "Animal protected or native slaughter eligibility refused" };
                    if (!dryRun && !Designated(pawn, DesignationDefOf.Slaughter))
                        map.designationManager.AddDesignation(new Designation(pawn, DesignationDefOf.Slaughter));
                }
                else
                {
                    var def = DefDatabase<TrainableDef>.GetNamedSilentFail(trainable);
                    if (def == null || pawn.training == null || !pawn.training.CanAssignToTrain(def).Accepted
                        || Designated(pawn, DesignationDefOf.Slaughter) || Designated(pawn, DesignationDefOf.ReleaseAnimalToWild))
                        return new { success = false, error = "Native training unavailable or animal designated for removal" };
                    if (!dryRun) pawn.training.SetWantedRecursive(def, true);
                }
                return new { success = true, dryRun, animal, before = expected, after = Settings(pawn), observed = Animal(pawn) };
            }, cancellationToken);
        }
    }
}
