#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // The animal race catalog (#1625): the static facts of every animal race
    // the game knows, modded and DLC included, wild or tameable. They hold
    // for the whole load, so the controller reads them once per load token.
    public sealed class NativeAnimalRaceCatalogTool
    {
        internal const string ToolName = "rimgovernor/observations_read_animal_race_catalog";

        [Tool(ToolName, Title = "Read the animal race catalog", Description = "Static facts of every animal race: carrying capacity, trainability and trainables, wildness, body size, combat power, market value, minimum handling skill and periodic products (milk, wool, eggs, spawned items). Fixed for a load. Read-only.")]
        [ToolResponse("payload", "string", "Official AnimalRaceCatalogReply ProtoJSON.", Always = true)]
        public async Task<object> ReadAnimalRaceCatalog(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw AnimalRaceCatalogRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.AnimalRaceCatalogRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.AnimalRaceCatalogReply { Failure = failure });
            if (parsed.Scope?.ExpectedIdentity == null)
                return ProtoBoundary.Encode(new Obs.AnimalRaceCatalogReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity required.") });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out _, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.AnimalRaceCatalogReply { Failure = error });
                try { return ProtoBoundary.Encode(new Obs.AnimalRaceCatalogReply { Observed = Read(context) }); }
                catch (Exception ex)
                {
                    Log.Error(ObservationWork.Failed("animalRaceCatalog", ex));
                    return ProtoBoundary.Encode(new Obs.AnimalRaceCatalogReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "The animal race catalog could not be read completely." } });
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Obs.AnimalRaceCatalog Read(Common.ObservationContext context)
        {
            var catalog = new Obs.AnimalRaceCatalog { Context = context };
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.race?.Animal == true && ProtoBoundary.IsIdentifier(d.defName))
                .OrderBy(d => d.defName, StringComparer.Ordinal))
                catalog.Races.Add(Race(def));
            return catalog;
        }

        private static Obs.AnimalRaceFacts Race(ThingDef def)
        {
            var race = def.race;
            var row = new Obs.AnimalRaceFacts { DefName = def.defName };
            Scalar(v => row.CarryingCapacity = v, () => def.GetStatValueAbstract(StatDefOf.CarryingCapacity));
            Scalar(v => row.Wildness = v, () => def.GetStatValueAbstract(StatDefOf.Wildness));
            Scalar(v => row.BodySize = v, () => race.baseBodySize);
            Scalar(v => row.CombatPower = v, () => DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.race == def).Select(k => (double)k.combatPower).DefaultIfEmpty(-1).Max());
            Scalar(v => row.MarketValue = v, () => def.GetStatValueAbstract(StatDefOf.MarketValue));
            var skill = def.GetStatValueAbstract(StatDefOf.MinimumHandlingSkill);
            if (!float.IsNaN(skill) && !float.IsInfinity(skill) && skill >= 0) row.MinimumHandlingSkill = (int)Math.Round(skill);
            if (race.trainability != null && ProtoBoundary.IsIdentifier(race.trainability.defName))
            {
                row.Trainability = race.trainability.defName;
                foreach (var trainable in DefDatabase<TrainableDef>.AllDefsListForReading.Where(t => ProtoBoundary.IsIdentifier(t.defName) && Trainable(race, t)).OrderBy(t => t.defName, StringComparer.Ordinal))
                    row.Trainables.Add(trainable.defName);
            }
            AddMilk(row, def);
            AddWool(row, def);
            AddEggs(row, def);
            AddSpawner(row, def);
            AddFeed(row, race);
            return row;
        }

        // Feed a bench can make for the race: every ingestible some recipe
        // produces that the race can ever eat, with its nutrition per item.
        private static void AddFeed(Obs.AnimalRaceFacts row, RaceProperties race)
        {
            foreach (var item in ProducedIngestibles.Value.Where(t => ProtoBoundary.IsIdentifier(t.defName) && race.CanEverEat(t)))
            {
                var nutrition = item.GetStatValueAbstract(StatDefOf.Nutrition);
                if (float.IsNaN(nutrition) || float.IsInfinity(nutrition) || nutrition <= 0) continue;
                row.FeedItems.Add(new Obs.AnimalFeedItem { DefName = item.defName, NutritionPerItem = nutrition });
            }
        }

        private static readonly Lazy<ThingDef[]> ProducedIngestibles = new Lazy<ThingDef[]>(() =>
            DefDatabase<RecipeDef>.AllDefsListForReading
                .Where(r => r.products != null)
                .SelectMany(r => r.products.Select(p => p.thingDef))
                .Where(t => t != null && t.IsIngestible)
                .Distinct()
                .OrderBy(t => t.defName, StringComparer.Ordinal)
                .ToArray());

        // Trainable mirrors Pawn_TrainingTracker.CanAssignToTrain's race
        // rules: trainability rank, minimum body size and the tag lists.
        private static bool Trainable(RaceProperties race, TrainableDef trainable)
        {
            if (race.trainability == null || trainable.requiredTrainability == null) return false;
            if (race.trainability.intelligenceOrder < trainable.requiredTrainability.intelligenceOrder) return false;
            if (race.baseBodySize < trainable.minBodySize) return false;
            if (race.untrainableTags != null && race.untrainableTags.Any(trainable.MatchesTag)) return false;
            return race.trainableTags == null || race.trainableTags.Count == 0 || race.trainableTags.Any(trainable.MatchesTag);
        }

        private static void AddMilk(Obs.AnimalRaceFacts row, ThingDef def)
        {
            var milk = def.GetCompProperties<CompProperties_Milkable>();
            if (milk?.milkDef != null && ProtoBoundary.IsIdentifier(milk.milkDef.defName))
                row.Products.Add(Product("milk", milk.milkDef.defName, milk.milkAmount, milk.milkIntervalDays));
        }

        private static void AddWool(Obs.AnimalRaceFacts row, ThingDef def)
        {
            var wool = def.GetCompProperties<CompProperties_Shearable>();
            if (wool?.woolDef != null && ProtoBoundary.IsIdentifier(wool.woolDef.defName))
                row.Products.Add(Product("wool", wool.woolDef.defName, wool.woolAmount, wool.shearIntervalDays));
        }

        private static void AddEggs(Obs.AnimalRaceFacts row, ThingDef def)
        {
            var egg = def.GetCompProperties<CompProperties_EggLayer>();
            var product = egg?.eggUnfertilizedDef ?? egg?.eggFertilizedDef;
            if (egg != null && product != null && ProtoBoundary.IsIdentifier(product.defName))
                row.Products.Add(Product("eggs", product.defName, (egg.eggCountRange.min + egg.eggCountRange.max) * 0.5, egg.eggLayIntervalDays));
        }

        private static void AddSpawner(Obs.AnimalRaceFacts row, ThingDef def)
        {
            var spawner = def.GetCompProperties<CompProperties_Spawner>();
            if (spawner?.thingToSpawn != null && ProtoBoundary.IsIdentifier(spawner.thingToSpawn.defName))
                row.Products.Add(Product("spawner", spawner.thingToSpawn.defName, spawner.spawnCount, spawner.spawnIntervalRange.Average / 60000.0));
        }

        private static Obs.AnimalProduct Product(string kind, string defName, double amount, double intervalDays)
        {
            var product = new Obs.AnimalProduct { Kind = kind, DefName = defName };
            Scalar(v => product.Amount = v, () => amount);
            Scalar(v => product.IntervalDays = v, () => intervalDays);
            return product;
        }

        // Scalar leaves a field absent when its value is not a finite,
        // non-negative number: unknown, never zero.
        private static void Scalar(Action<double> set, Func<double> read)
        {
            double value;
            try { value = read(); } catch (Exception) { return; }
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0) set(value);
        }
    }
}
