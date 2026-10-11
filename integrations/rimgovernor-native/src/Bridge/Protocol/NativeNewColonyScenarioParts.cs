#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Lifecycle = RimGovernor.Protocol.Lifecycle;

namespace HomeBridge.BridgeTools
{
    // The typed scenario parts of a NewColonySpec (contracts/proto/lifecycle.proto),
    // built into game ScenParts and put on the copy of the base scenario, where a
    // supplied list replaces the base's parts of that kind (an empty list keeps them).
    // Vanilla's editor builds a part with ScenarioMaker.MakeScenPart(ScenPartDef)
    // and sets its (mostly private) fields; this does the same through reflection,
    // checked against the 1.6 assemblies:
    //   - Pawn parts (ForcedTrait, ForcedHediff, Naked, SetNeedLevel) are
    //     ScenPart_PawnModifier: chance 0..1 and a PawnGenerationContext
    //     (All, PlayerStarter, NonPlayer). Hediff severity and need level are
    //     FloatRanges in the game; the wire carries one value, both ends set to it.
    //   - A hediff must be scenarioCanAdd, with severity at most the editor's cap
    //     (lethalSeverity x 0.99, else 1); a need must be major; a trait degree
    //     must be one the trait defines.
    //   - ScenPart_PermaGameCondition registers MakeConditionPermanent(def) on the
    //     start map. The editor lists only canBePermanent defs, the part does not
    //     check, so ColdSnap (not canBePermanent) works. 1.6 has no map-scoped
    //     permanent part.
    //   - Things: StartingThing_Defined (start_with), ScatterThingsNearPlayerStart
    //     (start_near) and ScatterThingsAnywhere (scattered) share ScenPart_ThingCount
    //     (thingDef, stuff, count, quality; allowRoofed on scatter parts).
    //   - DisableIncident / Rule_DisallowBuilding read their parts from Scenario.parts
    //     (IncidentWorker; PostGameStart), so the parts join that list.
    //   - Mandatory parts: playerFaction.factionDef (PlayerColony / PlayerTribe) and
    //     the ScenPart_PlayerPawnsArriveMethod already in the scenario.
    // Every def name is resolved before generation starts; an unknown or unusable
    // one is refused naming the field.
    internal static class NativeNewColonyScenarioParts
    {
        internal sealed class PartRefusal : Exception
        {
            public PartRefusal(string message) : base(message) { }
        }

        // A supplied list replaces the base scenario's parts of its kind (the
        // preset yields exactly its own parts); an empty list keeps them. The
        // three thing placements are one kind.
        private static readonly string[] ThingPartDefs = { "StartingThing_Defined", "ScatterThingsNearPlayerStart", "ScatterThingsAnywhere" };

        internal sealed class Plan
        {
            public readonly List<ScenPart> Parts = new List<ScenPart>();
            public readonly HashSet<string> ReplacedPartDefs = new HashSet<string>();
            public FactionDef? PlayerFaction;
            public PlayerPawnsArriveMethod? ArrivalMethod;
        }

        // Resolves and builds the spec's parts against the base scenario. Call on the game thread.
        internal static Plan Build(Lifecycle.NewColonySpec spec, Scenario baseScenario)
        {
            var plan = new Plan();
            if (spec.StartingThings.Count > 0) plan.ReplacedPartDefs.UnionWith(ThingPartDefs);
            if (spec.ForcedTraits.Count > 0) plan.ReplacedPartDefs.Add("ForcedTrait");
            if (spec.ForcedHediffs.Count > 0) plan.ReplacedPartDefs.Add("ForcedHediff");
            if (spec.Naked.Count > 0) plan.ReplacedPartDefs.Add("Naked");
            if (spec.SetNeedLevels.Count > 0) plan.ReplacedPartDefs.Add("SetNeedLevel");
            if (spec.PermaGameConditions.Count > 0) plan.ReplacedPartDefs.Add("PermanentGameCondition");
            if (spec.DisallowedBuildings.Count > 0) plan.ReplacedPartDefs.Add("Rule_DisallowBuilding");
            if (spec.DisabledIncidents.Count > 0) plan.ReplacedPartDefs.Add("DisableIncident");
            for (var i = 0; i < spec.StartingThings.Count; i++) plan.Parts.Add(Thing(spec.StartingThings[i], "starting_things[" + i + "]"));
            for (var i = 0; i < spec.ForcedTraits.Count; i++) plan.Parts.Add(Trait(spec.ForcedTraits[i], "forced_traits[" + i + "]"));
            for (var i = 0; i < spec.ForcedHediffs.Count; i++) plan.Parts.Add(Hediff(spec.ForcedHediffs[i], "forced_hediffs[" + i + "]"));
            foreach (var naked in spec.Naked)
            {
                var part = Make("Naked");
                SetModifier(part, naked.Chance, naked.Context);
                plan.Parts.Add(part);
            }
            for (var i = 0; i < spec.SetNeedLevels.Count; i++) plan.Parts.Add(Need(spec.SetNeedLevels[i], "set_need_levels[" + i + "]"));
            for (var i = 0; i < spec.PermaGameConditions.Count; i++)
            {
                var field = "perma_game_conditions[" + i + "]";
                var condition = Named<GameConditionDef>(spec.PermaGameConditions[i], field);
                if (condition.conditionClass == null)
                    throw new PartRefusal(field + " " + condition.defName + " has no condition class.");
                var part = Make("PermanentGameCondition");
                Set(part, "gameCondition", condition);
                plan.Parts.Add(part);
            }
            for (var i = 0; i < spec.DisallowedBuildings.Count; i++)
            {
                var field = "disallowed_buildings[" + i + "]";
                var building = Named<ThingDef>(spec.DisallowedBuildings[i], field);
                if (building.category != ThingCategory.Building || !building.BuildableByPlayer)
                    throw new PartRefusal(field + " " + building.defName + " is not a building the player can build.");
                var part = Make("Rule_DisallowBuilding");
                Set(part, "building", building);
                plan.Parts.Add(part);
            }
            for (var i = 0; i < spec.DisabledIncidents.Count; i++)
            {
                var part = Make("DisableIncident");
                Set(part, "incident", Named<IncidentDef>(spec.DisabledIncidents[i], "disabled_incidents[" + i + "]"));
                plan.Parts.Add(part);
            }
            if (spec.HasPlayerFaction)
            {
                var name = spec.PlayerFaction == Lifecycle.ScenarioPlayerFaction.NewTribe ? "PlayerTribe" : "PlayerColony";
                var faction = Named<FactionDef>(name, "player_faction");
                if (!faction.isPlayer) throw new PartRefusal("player_faction " + name + " is not a player faction.");
                plan.PlayerFaction = faction;
            }
            if (spec.HasArrivalMethod)
                plan.ArrivalMethod = spec.ArrivalMethod == Lifecycle.ScenarioArrivalMethod.DropPods ? PlayerPawnsArriveMethod.DropPods : PlayerPawnsArriveMethod.Standing;

            // The editor drops a part that cannot coexist with another (the same
            // trait for overlapping pawns, clashing conditions); here it is a refusal.
            var existing = baseScenario.AllParts.Where(p => p != null && !Replaced(plan, p)).ToList();
            for (var i = 0; i < plan.Parts.Count; i++)
            {
                var others = existing.Concat(plan.Parts.Where((_, j) => j != i));
                foreach (var other in others)
                    if (!plan.Parts[i].CanCoexistWith(other))
                        throw new PartRefusal("Scenario part " + Describe(plan.Parts[i]) + " cannot coexist with " + Describe(other) + ".");
            }
            return plan;
        }

        // Adds the plan to the editable copy. Call before the colonists are generated.
        internal static void Apply(Scenario copy, Plan plan)
        {
            var parts = Get<List<ScenPart>>(copy, "parts");
            parts.RemoveAll(p => Replaced(plan, p));
            parts.AddRange(plan.Parts);
            if (plan.PlayerFaction != null) Set(Get<ScenPart>(copy, "playerFaction"), "factionDef", plan.PlayerFaction);
            if (plan.ArrivalMethod.HasValue)
            {
                var arrival = parts.OfType<ScenPart_PlayerPawnsArriveMethod>().FirstOrDefault();
                if (arrival == null)
                {
                    arrival = (ScenPart_PlayerPawnsArriveMethod)Make("PlayerPawnsArriveMethod");
                    parts.Add(arrival);
                }
                Set(arrival, "method", plan.ArrivalMethod.Value);
            }
        }

        private static bool Replaced(Plan plan, ScenPart? part) => part?.def != null && plan.ReplacedPartDefs.Contains(part.def.defName);

        private static ScenPart Thing(Lifecycle.StartingThing thing, string field)
        {
            var definition = Named<ThingDef>(thing.ThingDef, field + ".thing_def");
            if (definition.category != ThingCategory.Item && definition.category != ThingCategory.Building)
                throw new PartRefusal(field + ".thing_def " + definition.defName + " is neither an item nor a building.");
            var name = thing.Placement == Lifecycle.StartingThingPlacement.StartWith ? "StartingThing_Defined"
                : thing.Placement == Lifecycle.StartingThingPlacement.StartNear ? "ScatterThingsNearPlayerStart"
                : "ScatterThingsAnywhere";
            if (thing.Placement == Lifecycle.StartingThingPlacement.StartWith && definition.category == ThingCategory.Building && !definition.Minifiable)
                throw new PartRefusal(field + ".thing_def " + definition.defName + " is a building that cannot be minified, so it cannot be a starting item.");
            ThingDef? stuff = null;
            if (definition.MadeFromStuff)
            {
                stuff = thing.HasStuff ? Named<ThingDef>(thing.Stuff, field + ".stuff") : GenStuff.DefaultStuffFor(definition);
                if (stuff == null || !GenStuff.AllowedStuffsFor(definition).Contains(stuff))
                    throw new PartRefusal(field + ".stuff " + (stuff?.defName ?? "(none)") + " is not a material " + definition.defName + " can be made from.");
            }
            else if (thing.HasStuff)
                throw new PartRefusal(field + ".stuff given, but " + definition.defName + " is not made from stuff.");
            QualityCategory? quality = null;
            if (thing.HasQuality)
            {
                if (!Enum.TryParse(thing.Quality, out QualityCategory parsed) || !Enum.IsDefined(typeof(QualityCategory), parsed))
                    throw new PartRefusal("Unknown " + field + ".quality \"" + thing.Quality + "\" (" + string.Join(", ", Enum.GetNames(typeof(QualityCategory))) + ").");
                if (!definition.HasComp(typeof(CompQuality)))
                    throw new PartRefusal(field + ".quality given, but " + definition.defName + " has no quality.");
                quality = parsed;
            }
            var part = Make(name);
            Set(part, "thingDef", definition);
            Set(part, "stuff", stuff);
            Set(part, "count", (int)thing.Count);
            Set(part, "quality", quality);
            if (part is ScenPart_ScatterThings scatter && thing.HasAllowRoofed) scatter.allowRoofed = thing.AllowRoofed;
            return part;
        }

        private static ScenPart Trait(Lifecycle.ForcedTrait forced, string field)
        {
            var trait = Named<TraitDef>(forced.Trait, field + ".trait");
            if (!trait.degreeDatas.Any(d => d.degree == forced.Degree))
                throw new PartRefusal(field + ".degree " + forced.Degree + " is not a degree of " + trait.defName + " ("
                    + string.Join(", ", trait.degreeDatas.Select(d => d.degree)) + ").");
            var part = Make("ForcedTrait");
            Set(part, "trait", trait);
            Set(part, "degree", forced.Degree);
            SetModifier(part, forced.Chance, forced.Context);
            return part;
        }

        private static ScenPart Hediff(Lifecycle.ForcedHediff forced, string field)
        {
            var hediff = Named<HediffDef>(forced.Hediff, field + ".hediff");
            if (!hediff.scenarioCanAdd)
                throw new PartRefusal(field + ".hediff " + hediff.defName + " cannot be added by a scenario.");
            var max = hediff.lethalSeverity > 0f ? hediff.lethalSeverity * 0.99f : 1f;
            if (forced.Severity > max)
                throw new PartRefusal(field + ".severity " + forced.Severity + " exceeds " + max + " for " + hediff.defName + ".");
            var part = Make("ForcedHediff");
            Set(part, "hediff", hediff);
            Set(part, "severityRange", new FloatRange(forced.Severity, forced.Severity));
            SetModifier(part, forced.Chance, forced.Context);
            return part;
        }

        private static ScenPart Need(Lifecycle.SetNeedLevel set, string field)
        {
            var need = Named<NeedDef>(set.Need, field + ".need");
            if (!need.major)
                throw new PartRefusal(field + ".need " + need.defName + " is not a major need.");
            var part = Make("SetNeedLevel");
            Set(part, "need", need);
            Set(part, "levelRange", new FloatRange(set.Level, set.Level));
            SetModifier(part, set.Chance, set.Context);
            return part;
        }

        private static void SetModifier(ScenPart part, float chance, Lifecycle.ScenarioPawnContext context)
        {
            Set(part, "chance", chance);
            Set(part, "context", context == Lifecycle.ScenarioPawnContext.PlayerStarter ? PawnGenerationContext.PlayerStarter
                : context == Lifecycle.ScenarioPawnContext.NonPlayer ? PawnGenerationContext.NonPlayer : PawnGenerationContext.All);
        }

        private static ScenPart Make(string scenPartDefName)
        {
            var def = DefDatabase<ScenPartDef>.GetNamedSilentFail(scenPartDefName);
            if (def == null) throw new PartRefusal("This game has no scenario part " + scenPartDefName + ".");
            return ScenarioMaker.MakeScenPart(def);
        }

        private static T Named<T>(string name, string field) where T : Def
        {
            var found = DefDatabase<T>.GetNamedSilentFail(name);
            if (found == null) throw new PartRefusal("Unknown " + field + " \"" + name + "\".");
            return found;
        }

        private static string Describe(ScenPart part) => part.def != null ? part.def.defName : part.GetType().Name;

        // A game update that renames a field fails the start with its name, not silently.
        private static void Set(object target, string name, object? value)
        {
            var field = AccessTools.Field(target.GetType(), name)
                ?? throw new InvalidOperationException(target.GetType().Name + " has no field " + name + " in this game version.");
            field.SetValue(target, value);
        }

        private static T Get<T>(object target, string name)
        {
            var field = AccessTools.Field(target.GetType(), name)
                ?? throw new InvalidOperationException(target.GetType().Name + " has no field " + name + " in this game version.");
            return (T)field.GetValue(target);
        }
    }
}
