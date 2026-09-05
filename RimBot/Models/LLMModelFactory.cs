using System.Collections.Generic;

namespace RimBot.Models
{
    public static class LLMModelFactory
    {
        private static readonly Dictionary<LLMProviderType, ILanguageModel> Instances =
            new Dictionary<LLMProviderType, ILanguageModel>();

        public static ILanguageModel GetModel(LLMProviderType providerType)
        {
            if (!Instances.TryGetValue(providerType, out var model))
            {
                switch (providerType)
                {
                    case LLMProviderType.Anthropic:
                        model = new AnthropicModel();
                        break;
                    case LLMProviderType.OpenAI:
                        model = new OpenAIModel();
                        break;
                    case LLMProviderType.Google:
                        model = new GoogleModel();
                        break;
                    case LLMProviderType.Local:
                        return new LocalModel(RimBotMod.Settings.localUrl);
                    default:
                        throw new System.ArgumentOutOfRangeException(nameof(providerType));
                }
                Instances[providerType] = model;
            }
            return model;
        }
    }
}
