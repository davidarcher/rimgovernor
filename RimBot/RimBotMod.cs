using System;
using System.Collections.Generic;
using RimBot.Models;
using UnityEngine;
using Verse;
namespace RimBot
{
    public class RimBotMod : Mod
    {
        private Vector2 settingsScroll;
        private float settingsHeight=1000;
        public static RimBotSettings Settings { get; private set; }
        public RimBotMod(ModContentPack content) : base(content) { Settings=GetSettings<RimBotSettings>(); }
        public override string SettingsCategory() => "RimBot Colony Manager";
        public override void DoSettingsWindowContents(Rect rect)
        {
            Widgets.BeginScrollView(rect,ref settingsScroll,new Rect(0,0,rect.width-20,settingsHeight));
            var l=new Listing_Standard(); l.Begin(new Rect(0,0,rect.width-20,10000));
            l.Label("One colony manager. Existing pawns and normal game rules are preserved.");
            if(l.ButtonText("Provider: "+(Settings.managerProvider==LLMProviderType.Local?"LM Studio (local)":Settings.managerProvider.ToString()+" (paid API)")))
            {
                var choices=new List<FloatMenuOption>();
                foreach(LLMProviderType p in Enum.GetValues(typeof(LLMProviderType))) {
                    var selected=p;
                    choices.Add(new FloatMenuOption(p==LLMProviderType.Local?"LM Studio (local)":p+" (paid API)",()=> { Settings.managerProvider=selected; Settings.managerModel=""; }));
                }
                Find.WindowStack.Add(new FloatMenu(choices));
            }
            if(Settings.managerProvider==LLMProviderType.Local) {
                l.Label("LM Studio server address (include /v1):"); Settings.localUrl=l.TextEntry(Settings.localUrl);
                l.Label("Server API token (optional; leave blank unless authentication is enabled):"); Settings.localApiKey=l.TextEntry(Settings.localApiKey);
                l.Label("Daily reasoning effort (none; blank uses server default):"); Settings.localReasoningEffort=l.TextEntry(Settings.localReasoningEffort);
                l.Label("Strategic reasoning effort (medium; requires model/server support):"); Settings.strategicReasoningEffort=l.TextEntry(Settings.strategicReasoningEffort);
                l.Label("Local output length uses the server/context allowance; no mod token cap.");
                l.Label("Start the server in LM Studio's Developer tab. Use a model that supports tools.");
            } else {
                l.Label("API key (usage is billed separately from chat subscriptions):");
                string key=l.TextEntry(Settings.GetApiKeyForProvider(Settings.managerProvider));
                if(Settings.managerProvider==LLMProviderType.OpenAI) Settings.openAIApiKey=key;
                if(Settings.managerProvider==LLMProviderType.Anthropic) Settings.anthropicApiKey=key;
                if(Settings.managerProvider==LLMProviderType.Google) Settings.googleApiKey=key;
            }
            l.Label("Model identifier (copy the exact identifier from your server):"); Settings.managerModel=l.TextEntry(Settings.managerModel).Replace("\n","").Trim();
            l.GapLine();
            l.Label("Minimum review interval: "+Settings.reviewSeconds+" seconds"); Settings.reviewSeconds=(int)l.Slider(Settings.reviewSeconds,15,300);
            if(Settings.managerProvider==LLMProviderType.Local) {
                l.Label("Local requests per hour: "+(Settings.localRequestsPerHour==0?"Unlimited":Settings.localRequestsPerHour.ToString())); Settings.localRequestsPerHour=(int)l.Slider(Settings.localRequestsPerHour,0,300);
            } else {
                l.Label("Paid API requests per hour: "+Settings.requestsPerHour); Settings.requestsPerHour=(int)l.Slider(Settings.requestsPerHour,1,120);
            }
            if(Settings.managerProvider!=LLMProviderType.Local) { l.Label("Maximum output tokens per daily request: "+Settings.maxTokens); Settings.maxTokens=(int)l.Slider(Settings.maxTokens,128,2048); l.Label("Strategic paid requests allow 8192 output tokens with medium reasoning."); }
            l.Label("Local reviews have no turn or action cap. Paid APIs retain 3 steps / 4 actions. Unchanged summaries are skipped. No cloud fallback.");
            settingsHeight=l.CurHeight+10; l.End(); Widgets.EndScrollView();
        }
    }
}
