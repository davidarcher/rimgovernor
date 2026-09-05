using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimBot.Tools;
namespace RimBot.Colony
{
    public static class DailyPlanning
    {
        public static bool Due(int previousTick,int tick,bool missing,bool directed,bool blocked) =>
            missing || directed || previousTick<0 || tick-previousTick>=60000 || (blocked && tick-previousTick>=12000);
        public static string Parse(JObject data)
        {
            if(data?["plan"]?.Type!=JTokenType.String) throw new ArgumentException("Daily plan must contain text.");
            string plan=data.Value<string>("plan").Trim();
            if(plan.Length==0 || plan.Length>1000) throw new ArgumentException("Daily plan must be 1–1000 characters.");
            return plan;
        }
        public static List<ToolDefinition> Tools()=>new List<ToolDefinition>{new ToolDefinition {
            Name="save_daily_plan",Description="Save today's brief work plan: priorities, parallel work and wait conditions. No game changes.",
            ParametersJson="{\"type\":\"object\",\"properties\":{\"plan\":{\"type\":\"string\",\"maxLength\":1000}},\"required\":[\"plan\"],\"additionalProperties\":false}"
        }};
    }
}
