using RimBot.Models;
using Verse;
namespace RimBot
{
    public class RimBotSettings : ModSettings
    {
        public LLMProviderType managerProvider = LLMProviderType.Local;
        public string managerModel = "";
        public string localUrl = "http://localhost:1234/v1";
        public string localReasoningEffort = "none";
        public string strategicReasoningEffort = "medium";
        public string localApiKey = "";
        public string anthropicApiKey = "";
        public string openAIApiKey = "";
        public string googleApiKey = "";
        public int maxTokens = 768;
        public int reviewSeconds = 60;
        public int requestsPerHour = 20;
        public int localRequestsPerHour = 0;
        public string ConnectionSignature => managerProvider + "|" + managerModel + "|" + localUrl + "|" + localReasoningEffort + "|" + strategicReasoningEffort + "|" + GetApiKeyForProvider(managerProvider);
        public override void ExposeData()
        {
            Scribe_Values.Look(ref managerProvider,"managerProvider",LLMProviderType.Local);
            Scribe_Values.Look(ref managerModel,"managerModel","");
            Scribe_Values.Look(ref localUrl,"localUrl","http://localhost:1234/v1");
            Scribe_Values.Look(ref localReasoningEffort,"localReasoningEffort","none");
            Scribe_Values.Look(ref strategicReasoningEffort,"strategicReasoningEffort","medium");
            Scribe_Values.Look(ref localApiKey,"localApiKey","");
            Scribe_Values.Look(ref anthropicApiKey,"anthropicApiKey","");
            Scribe_Values.Look(ref openAIApiKey,"openAIApiKey","");
            Scribe_Values.Look(ref googleApiKey,"googleApiKey","");
            Scribe_Values.Look(ref maxTokens,"managerMaxTokens",768);
            Scribe_Values.Look(ref reviewSeconds,"managerReviewSeconds",60);
            Scribe_Values.Look(ref requestsPerHour,"managerRequestsPerHour",20);
            Scribe_Values.Look(ref localRequestsPerHour,"localRequestsPerHour",0);
            localRequestsPerHour=System.Math.Max(0,System.Math.Min(300,localRequestsPerHour));
            maxTokens=System.Math.Max(128,System.Math.Min(2048,maxTokens));
            reviewSeconds=System.Math.Max(15,System.Math.Min(300,reviewSeconds));
            requestsPerHour=System.Math.Max(1,System.Math.Min(120,requestsPerHour));
            base.ExposeData();
        }
        public string GetApiKeyForProvider(LLMProviderType provider)
        {
            switch(provider) {
                case LLMProviderType.Local: return localApiKey;
                case LLMProviderType.Anthropic: return anthropicApiKey;
                case LLMProviderType.OpenAI: return openAIApiKey;
                case LLMProviderType.Google: return googleApiKey;
                default: return "";
            }
        }
    }
}
