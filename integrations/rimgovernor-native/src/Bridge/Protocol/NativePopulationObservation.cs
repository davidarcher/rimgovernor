#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Dedicated per-cycle population census on the official protobuf
    // boundary, the same migration NativeHusbandryOperations/NativePrisonerInteractionOperations
    // already made for the write side. Population-*'s recruit/maintain deficit
    // facts (recruitable, current interaction) live only here -- the generic
    // colony/upkeep census NativeUpkeepFacts populates has no guest section --
    // so this is the one native read Go's routine planner needs to detect and
    // select a prisoner-interaction write. Single complete page, like
    // ReadStatus/ReadHusbandry/ReadPrisonerInteraction; a paginated population
    // is deferred to whatever candidate search eventually needs one.
    public sealed class NativePopulationObservation
    {
        [Tool("rimgovernor/observations_read_population", Title = "Read colony population and custody",
            Description = "Official PopulationRequest ProtoJSON. Read current map humanlike population: admission, guest/prisoner status, recruitment eligibility, current exclusive interaction and owned bed. Single complete page 1..256; unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations PopulationReply ProtoJSON.", Always = true)]
        public async Task<object> ReadPopulation(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a PopulationRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_population", request!, Obs.PopulationRequest.Parser, out var parsed, out var failure)
                || !ValidatePopulation(parsed, out failure)) return ProtoBoundary.Encode(new Obs.PopulationReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.PopulationReply { Failure = failure });
                try { return ProtoBoundary.Encode(new Obs.PopulationReply { Observed = Population(map, parsed, context) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.PopulationReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native population facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        // The population as a frame section (issue #180): the same rows the
        // tool answers, or false for any read failure the frame then omits.
        internal static bool TryRead(Map map, Obs.PopulationRequest request, Common.ObservationContext context, [NotNullWhen(true)] out Obs.PopulationSnapshot? snapshot)
        {
            snapshot = null;
            try { snapshot = Population(map, request, context); return true; }
            catch (Exception) { return false; }
        }

        internal static bool ValidatePopulation(Obs.PopulationRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope is required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        private static Obs.PopulationSnapshot Population(Map map, Obs.PopulationRequest request, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).OrderBy(p => p.thingIDNumber).ToList();
            var snapshot = new Obs.PopulationSnapshot { Context = context };
            foreach (var p in people)
            {
                var person = new Obs.PopulationPerson { Pawn = NativePawnObservationTools.Ref(p), Admitted = p.IsFreeColonist && p.Faction == player, Guest = p.HostFaction == player };
                if (p.guest != null)
                {
                    person.Recruitable = p.guest.Recruitable;
                    person.Resistance = Number(p.guest.Resistance);
                    var interaction = p.guest.ExclusiveInteractionMode?.defName;
                    if (interaction != null) person.Interaction = NativePawnObservationTools.Id(interaction);
                    // Time held so far (the TimeAsPrisoner record, in ticks): the
                    // routine release path's "held N days with resistance
                    // unbroken" clock. Only a current prisoner carries it.
                    if (p.IsPrisoner && p.records != null) person.PrisonerTicks = (long)p.records.GetValue(RecordDefOf.TimeAsPrisoner);
                    if (ModsConfig.IdeologyActive && p.IsPrisoner) person.Will = Number(p.guest.will);
                }
                if (p.Ideo != null) person.IdeoId = NativePawnObservationTools.Id(p.Ideo.GetUniqueLoadID());
                // Prospect facts (#1036): MaintainPopulation weighs a prisoner's
                // skills, traits, age and health against the free colonists';
                // a hostile's skills rank it as a lance target (#1038).
                if (p.IsPrisonerOfColony || person.Admitted || p.HostileTo(player))
                {
                    person.WildMan = p.IsWildMan();
                    person.Biography = NativePawnDetails.Biography(p);
                    if (p.health?.summaryHealth != null) person.HealthSummary = Number(p.health.summaryHealth.SummaryHealthPercent);
                    // After combat (#1079): a luciferium addict is stripped and
                    // finished, not captured; every raider is stripped first.
                    person.LuciferiumAddicted = CombatMirror.HasHediff(p, "LuciferiumAddiction");
                    person.WearingApparel = p.apparel?.WornApparel.Count > 0;
                }
                // Organ harvest facts (#1169): a colony prisoner's surgery
                // facts through the care read's own producer, its home faction
                // and vanilla's harvest goodwill report (Recipe_RemoveBodyPart
                // reports -70 to HomeFaction when the pawn has a faction).
                // Medical care cap inputs (#1301): colony prisoners and hosted guests.
                if (p.IsPrisonerOfColony || (p.HostFaction == player && !p.IsPrisoner && !p.IsSlave))
                {
                    if (p.playerSettings != null) person.MedicalCare = NativeEnums.Care(p.playerSettings.medCare);
                    person.Conditions = NativePawnDetails.Conditions(p);
                }
                if (p.IsPrisonerOfColony)
                {
                    var health = new Obs.PawnHealth();
                    var bills = p.BillStack?.Bills;
                    if (bills == null) health.Issues.Add(NativePawnObservationTools.Issue("surgery_bills", Common.UnavailableReason.NativeComponentMissing, "No bill stack."));
                    else foreach (var bill in bills.OfType<Bill_Medical>())
                    {
                        var b = new Obs.SurgeryBill { Id = NativePawnObservationTools.Id(bill.GetUniqueLoadID()), Recipe = NativePawnObservationTools.Id(bill.recipe.defName), Suspended = bill.suspended };
                        if (bill.Part != null) b.PartIndex = p.RaceProps.body.AllParts.IndexOf(bill.Part);
                        health.SurgeryBills.Add(b);
                    }
                    NativePawnDetails.Surgery(p, health);
                    person.Surgery = health;
                    if (p.playerSettings != null) person.MedicalCare = NativeEnums.Care(p.playerSettings.medCare);
                    // Peg-leg control (#1236): an addiction a prisoner cannot feed.
                    person.Withdrawal = p.health?.hediffSet?.hediffs?.Any(h => h is Hediff_Addiction) == true;
                    var home = p.Faction == null ? null : p.HomeFaction;
                    person.HarvestGoodwillChange = 0;
                    if (home != null && !home.IsPlayer)
                    {
                        person.Faction = NativeRef.Of(NativePawnObservationTools.Id(home.GetUniqueLoadID()));
                        if (player.CanChangeGoodwillFor(home, -70))
                            person.HarvestGoodwillChange = Math.Max(player.CalculateAdjustedGoodwillChange(home, -70), -100 - player.GoodwillWith(home));
                    }
                }
                if (p.ownership?.OwnedBed is Building_Bed bed && bed.Spawned) person.OwnedBed = NativeBuildingObservationTools.Ref(bed);
                if (p.needs?.food != null) person.NutritionPerDay = Number(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f);
                // The prisoner custody and interaction settings token
                // (NativePrisonerInteractionOperations.Settings).
                person.PawnSnapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = person.Pawn.Id, Token = NativePrisonerInteractionOperations.Settings(p) };
                snapshot.Persons.Add(person);
            }
            // Owned-pawn names (#1310): the census the unique-name planner reads.
            foreach (var owned in NativePawnSettings.OwnedNamedPawns())
                snapshot.OwnedNames.Add(new Obs.OwnedName { PawnId = NativePawnObservationTools.Id(owned.GetUniqueLoadID()), ShortName = NativePawnObservationTools.Text(owned.Name.ToStringShort), ThingId = owned.thingIDNumber });
            snapshot.IdeologyActive = ModsConfig.IdeologyActive;
            // OrganUse precept (#1169): every player ideoligion carries one;
            // OrganUse_Classic stands without the Ideology DLC.
            var organUse = player.ideos?.PrimaryIdeo?.PreceptsListForReading.FirstOrDefault(pr => pr.def.issue?.defName == "OrganUse");
            if (organUse != null) snapshot.OrganUsePrecept = NativePawnObservationTools.Id(organUse.def.defName);
            if (ModsConfig.IdeologyActive)
            {
                snapshot.ClassicIdeoMode = Find.IdeoManager.classicMode;
                var ideo = player.ideos?.PrimaryIdeo;
                if (ideo != null)
                {
                    snapshot.ColonyIdeoId = NativePawnObservationTools.Id(ideo.GetUniqueLoadID());
                    var slavery = ideo.PreceptsListForReading.FirstOrDefault(pr => pr.def.issue?.defName == "Slavery");
                    if (slavery != null) snapshot.SlaveryPrecept = NativePawnObservationTools.Id(slavery.def.defName);
                }
            }
            // The installed subset of the modes PrisonerInteractionIntent accepts.
            foreach (var name in new[] { "AttemptRecruit", "MaintainOnly", "ReduceResistance", "Release", "Enslave", "Convert" })
            {
                var def = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(name);
                if (def != null) snapshot.SupportedInteractions.Add(new Obs.DefinitionRef { DefName = NativePawnObservationTools.Id(def.defName), Label = NativePawnObservationTools.Text(def.label) });
            }
            // Storyteller population outlook (#1031).
            var intent = StorytellerUtilityPopulation.PopulationIntent;
            var difficulty = Find.Storyteller.difficulty;
            snapshot.PopulationIntent = Number(intent);
            snapshot.AdjustedPopulation = Number(StorytellerUtilityPopulation.AdjustedPopulation);
            snapshot.DeathOnDownedChance = Number(DeathOnDownedChance(intent, difficulty.unwaveringPrisoners, difficulty.enemyDeathOnDownedChanceFactor));
            snapshot.UnrecruitableChance = Number(UnrecruitableChance(intent));
            return snapshot;
        }

        // The humanlike branch of Pawn_HealthTracker's death-on-downed roll for a
        // non-colony pawn downed by violence, with no per-pawn override.
        internal static float DeathOnDownedChance(float intent, bool unwaveringPrisoners, float enemyFactor) =>
            (unwaveringPrisoners ? HealthTuning.DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve
                : HealthTuning.DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve_WaveringPrisoners).Evaluate(intent) * enemyFactor;

        internal static float UnrecruitableChance(float intent) => HealthTuning.NonRecruitableChanceOverPopulationIntentCurve.Evaluate(intent);

        private static double Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
    }
}
