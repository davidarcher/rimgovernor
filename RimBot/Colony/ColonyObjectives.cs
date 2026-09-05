using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RimBot.Colony
{
    public enum ObjectiveState { Needed, Ordered, InProgress, Satisfied, Blocked }

    // Plain facts keep the policy independent of both the game and the language model.
    public sealed class ColonyFacts
    {
        public int Unarmed;
        public int Colonists, Patients, Bleeding, Downed, TemperatureInjuries, Hostiles;
        public int Doctors, ActiveTending, Cooks, CookingOrders, ActiveCooking;
        public float Nutrition, DailyNutrition;
        public int ShelteredSlots, RegularBedSlots, PendingBedSlots, BedFrames, Builders, ActiveConstruction;
        public int Blueprints, Frames;
        public float ConstructionWork;
        public bool ConstructionStalled;
        public int PendingOrders => Blueprints + Frames;
        public float FoodDays => DailyNutrition > 0 ? Nutrition / DailyNutrition : 0;
    }

    public sealed class ColonyObjective
    {
        public string Id, Title, Evidence, NextStep;
        public ObjectiveState State;
        public int Priority;
        public JObject ToJson() => new JObject {
            ["id"] = Id, ["state"] = State.ToString(), ["priority"] = Priority,
            ["evidence"] = Evidence, ["next"] = NextStep
        };
        public string StateLabel => State == ObjectiveState.InProgress ? "In progress" : State.ToString();
    }

    public static class ColonyObjectives
    {
        public const float FoodTargetDays = 2f;
        private static ColonyObjective Objective(string id, string title, ObjectiveState state, int priority, string evidence, string next)
            => new ColonyObjective { Id = id, Title = title, State = state, Priority = priority, Evidence = evidence, NextStep = next };

        public static List<ColonyObjective> Evaluate(ColonyFacts f)
        {
            var result = new List<ColonyObjective>();
            bool healthRisk = f.Patients > 0 || f.Bleeding > 0 || f.Downed > 0 || f.TemperatureInjuries > 0;
            var healthState = !healthRisk ? ObjectiveState.Satisfied : f.ActiveTending > 0 ? ObjectiveState.InProgress :
                f.Doctors == 0 || f.TemperatureInjuries > 0 ? ObjectiveState.Blocked : ObjectiveState.Needed;
            result.Add(Objective("health", "Care for colonists", healthState, healthRisk ? 0 : 5,
                f.Patients + " needing tending; " + f.Bleeding + " bleeding; " + f.Downed + " downed; " + f.TemperatureInjuries + " with heat/cold injury; " + f.Doctors + " enabled doctors.",
                !healthRisk ? "No detected urgent health conditions." : f.ActiveTending > 0 ? "Tending is underway; monitor patients." :
                "Player may need to rescue/tend or correct temperatures. Manager can inspect and adjust work priorities, but cannot order emergency rescue or treatment."));

            result.Add(Objective("safety", "Respond to threats", f.Hostiles > 0 ? ObjectiveState.Blocked : f.Unarmed>0 ? ObjectiveState.Needed : ObjectiveState.Satisfied,
                f.Hostiles > 0 ? 0 : f.Unarmed>0 ? 1 : 5, f.Hostiles + " active hostile pawns; " + f.Unarmed + " capable colonists without weapons.",
                f.Hostiles > 0 ? "Player action required: combat controls are not implemented. Avoid new construction during danger." : f.Unarmed>0 ? "Find and allow nearby weapons, then equip capable colonists. This checks immediate threats/equipment, not overall defensive readiness." : "No active hostile pawns detected; capable colonists are armed. This is not a complete defense/hazard assessment."));

            bool enoughFood = f.DailyNutrition <= 0 || f.FoodDays >= FoodTargetDays;
            var foodState = enoughFood ? ObjectiveState.Satisfied : f.ActiveCooking > 0 ? ObjectiveState.InProgress :
                f.CookingOrders > 0 ? (f.Cooks > 0 ? ObjectiveState.Ordered : ObjectiveState.Blocked) : ObjectiveState.Needed;
            result.Add(Objective("food", "Maintain two days of food", foodState, enoughFood ? 5 : f.FoodDays < 1 ? 1 : 2,
                f.FoodDays.ToString("F1", CultureInfo.InvariantCulture) + " estimated days of loose, allowed food; " + f.CookingOrders + " active food bills; " + f.Cooks + " enabled cooks.",
                enoughFood ? "Monitor reserves. Estimate excludes carried food and does not prove reachability or balanced diets." :
                f.ActiveCooking > 0 ? "Cooking is underway; wait for output before duplicating orders." :
                f.CookingOrders > 0 ? "Reuse existing food bills. Check cook priorities and ingredients if output stalls." :
                "Establish appropriate growing zones and food production bills. Inspect crops, built workstations and existing bills. Hunt suitable animals only with equipped capable hunters and arrange butchering. A blueprint alone does not supply food."));

            int missing = Math.Max(0, f.Colonists - f.ShelteredSlots);
            var shelterState = missing == 0 ? ObjectiveState.Satisfied :
                f.PendingBedSlots >= missing ? (f.Builders == 0 || f.ConstructionStalled ? ObjectiveState.Blocked :
                    f.ActiveConstruction > 0 && f.BedFrames > 0 ? ObjectiveState.InProgress : ObjectiveState.Ordered) : ObjectiveState.Needed;
            result.Add(Objective("shelter", "Provide sheltered sleeping places", shelterState, missing > 0 ? 2 : 5,
                f.ShelteredSlots + "/" + f.Colonists + " sheltered slots; " + f.RegularBedSlots + " existing regular slots total; " + f.PendingBedSlots + " slots in pending bed orders.",
                missing == 0 ? "Enough physical sleeping places. Assignment, access, and temperature comfort still require checking." :
                f.RegularBedSlots >= f.Colonists ? "Enough beds already exist. Enclose/roof their locations rather than duplicate beds; use areas_build_roof after checking supports and enclosure." :
                f.PendingBedSlots >= missing ? "Existing bed orders cover the slot deficit. Check their enclosure and construction progress; do not duplicate beds." :
                "Inspect an existing enclosed room first, then plan missing beds. Unroofed/outdoor beds do not satisfy this objective."));

            var workState = f.PendingOrders == 0 ? ObjectiveState.Satisfied : f.Builders == 0 || f.ConstructionStalled ? ObjectiveState.Blocked :
                f.ActiveConstruction > 0 ? ObjectiveState.InProgress : ObjectiveState.Ordered;
            result.Add(Objective("construction", "Finish existing construction", workState, workState == ObjectiveState.Blocked ? 2 : 3,
                f.Blueprints + " blueprints; " + f.Frames + " frames; " + f.Builders + " enabled builders; " + f.ActiveConstruction + " currently constructing.",
                f.PendingOrders == 0 ? "No pending construction." : f.Builders == 0 ? "Enable a capable builder if appropriate; manual priorities must be enabled by the player." :
                f.ConstructionStalled ? "No measured construction progress for six in-game hours. Inspect existing orders/materials before creating more." :
                "Let existing orders finish. construction_list can identify pending structures and locations."));
            return result.OrderBy(o => o.Priority).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        }

        // A changed state/priority is meaningful; tiny nutrition fluctuations or pawn movement are not.
        public static string DecisionKey(IEnumerable<ColonyObjective> objectives) => string.Join("|",
            objectives.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id + ":" + o.State + ":" + o.Priority));
    }
}
