using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBot.Tools;

namespace RimBot.Colony
{
    public static class ActivitySummary
    {
        public static string Tool(ToolCall call, string result, bool success)
        {
            if (!success) return "Could not complete " + Label(call.Name) + ": " + Short(result, 350);
            try
            {
                switch (call.Name)
                {
                    case "find_items":
                        var found=JObject.Parse(result);
                        return "Found " + found["totalStacks"] + " matching item stacks; showing " + ((JArray)found["items"]).Count + " nearest the selected point.";
                    case "inspect_area":
                        var cells = JArray.Parse(result);
                        return "Checked " + cells.Count + " cells near (" + call.Arguments["x"] + ", " + call.Arguments["z"] + "): " +
                            cells.Count(c => c["walkable"]?.Value<bool>() == true) + " walkable, " + cells.Count(c => c["roofed"]?.Value<bool>() == true) + " roofed.";
                    case "list_buildables":
                        var buildings = JArray.Parse(result);
                        return buildings.Count == 0 ? "No available structures matched that search." : "Available structures: " + string.Join(", ", buildings.Take(5).Select(b => b["label"]?.Value<string>())) + (buildings.Count > 5 ? " and more." : ".");
                    case "inspect_colonist":
                        var pawn = JObject.Parse(result);
                        var skills = (JArray)pawn["skills"];
                        return "Checked " + pawn["name"] + "'s work settings. Strongest skills: " + string.Join(", ", skills.OrderByDescending(s => s["level"].Value<int>()).Take(3).Select(s => s["skill"] + " " + s["level"])) + ".";
                    case "inspect_work_orders":
                        var orders = JArray.Parse(result);
                        return orders.Count == 0 ? "No pending construction orders." : "Checked " + orders.Count + " pending orders: " +
                            string.Join(", ", orders.GroupBy(o => o["defName"].Value<string>()).Take(5).Select(g => g.Count() + " " + g.Key)) + ".";
                    case "set_plan": return "Updated the shared plan.";
                    default: return Short(result, 500);
                }
            }
            catch { return "Completed " + Label(call.Name) + ". Details are in the debug log."; }
        }
        public static string Model(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string trimmed = text.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("[") || trimmed.StartsWith("```"))
            {
                try {
                    var json = JObject.Parse(trimmed);
                    string summary = (json["summary"] ?? json["message"] ?? json["plan"])?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(summary)) return Short(summary, 600);
                } catch { }
                return "Received a structured model response. Details are in the debug log.";
            }
            return Short(trimmed, 900);
        }
        private static string Label(string name) => (name ?? "action").Replace('_', ' ');
        private static string Short(string text, int max) => text == null ? "" : text.Length <= max ? text : text.Substring(0, max) + "...";
    }
}
