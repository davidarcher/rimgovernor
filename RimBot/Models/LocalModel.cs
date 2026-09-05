using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBot.Tools;

namespace RimBot.Models
{
    // LM Studio's Chat Completions interface; no cloud fallback or inherited cloud key.
    public sealed class LocalModel : ILanguageModel
    {
        private readonly string baseUrl;
        private readonly string reasoningEffort;
        private readonly TimeSpan requestTimeout;
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(10) };
        public LocalModel(string url, string reasoningEffort = "none", TimeSpan? timeout = null)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https") ||
                !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
                throw new ArgumentException("Enter the LM Studio server URL, e.g. http://localhost:1234/v1");
            baseUrl = url.TrimEnd('/');
            this.reasoningEffort = reasoningEffort;
            requestTimeout=timeout??TimeSpan.FromMinutes(2);
        }
        public LLMProviderType ProviderType => LLMProviderType.Local;
        public bool SupportsImageOutput => false;
        public string[] GetAvailableModels() => new string[0];
        public Task<ModelResponse> SendImageRequest(List<ChatMessage> messages, string model, string key, int maxTokens)
            => Task.FromResult(ModelResponse.FromError("Local image generation is not supported."));
        public Task<ModelResponse> SendChatRequest(List<ChatMessage> messages, string model, string key, int maxTokens)
            => SendToolRequest(messages, new List<ToolDefinition>(), model, key, maxTokens, ThinkingLevel.None);

        public async Task<ModelResponse> SendToolRequest(List<ChatMessage> messages, List<ToolDefinition> tools,
            string model, string apiKey, int maxTokens, ThinkingLevel thinkingLevel)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(model)) return ModelResponse.FromError("Enter the loaded LM Studio model identifier.");
                using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions"))
                {
                    if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    var body = BuildRequest(messages, tools, model, maxTokens, reasoningEffort);
                    request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var cancellation = new CancellationTokenSource(requestTimeout))
                    using (var response = await Client.SendAsync(request,cancellation.Token).ConfigureAwait(false))
                    {
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            return ModelResponse.FromError("LM Studio HTTP " + (int)response.StatusCode + ": " + json.Substring(0, Math.Min(json.Length, 600)));
                        return ParseResponse(json);
                    }
                }
            }
            catch (Exception ex) { return ModelResponse.FromError("LM Studio request failed: " + ex.Message); }
        }

        public static JObject BuildRequest(List<ChatMessage> messages, List<ToolDefinition> tools, string model, int maxTokens, string effort = null)
        {
            var history = new JArray();
            foreach (var message in messages)
            {
                if (message.HasToolResult)
                {
                    foreach (var part in message.ToolResultParts)
                        history.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = part.ToolCallId, ["content"] = part.Text ?? "" });
                    continue;
                }
                var entry = new JObject { ["role"] = message.Role, ["content"] = message.Content ?? "" };
                if (message.HasToolUse)
                {
                    var calls = new JArray();
                    foreach (var part in message.ToolUseParts)
                        calls.Add(new JObject { ["id"] = part.ToolCallId, ["type"] = "function", ["function"] = new JObject
                        { ["name"] = part.ToolName, ["arguments"] = (part.ToolArguments ?? new JObject()).ToString(Formatting.None) } });
                    entry["tool_calls"] = calls;
                }
                history.Add(entry);
            }
            var body = new JObject { ["model"] = model, ["messages"] = history, ["stream"] = false };
            if (!string.IsNullOrWhiteSpace(effort)) body["reasoning_effort"] = effort;
            if (maxTokens > 0) body["max_tokens"] = maxTokens;
            if (tools.Count > 0)
            {
                var definitions = new JArray();
                foreach (var tool in tools) definitions.Add(new JObject { ["type"] = "function", ["function"] = new JObject
                { ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = JObject.Parse(tool.ParametersJson) } });
                body["tools"] = definitions;
                body["tool_choice"] = "auto";
            }
            return body;
        }

        public static ModelResponse ParseResponse(string json)
        {
            var root = JObject.Parse(json);
            var choice = root["choices"]?.First;
            if (choice?["message"] == null) return ModelResponse.FromError("LM Studio returned no assistant message.");
            var msg = choice["message"];
            var parts = new List<ContentPart>();
            string content = msg["content"]?.Value<string>() ?? "";
            if (content.Length > 0) parts.Add(ContentPart.FromText(content));
            var calls = new List<ToolCall>();
            var ids = new HashSet<string>();
            foreach (var call in (msg["tool_calls"] as JArray) ?? new JArray())
            {
                string id = call["id"]?.Value<string>();
                string name = call["function"]?["name"]?.Value<string>();
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !ids.Add(id))
                    return ModelResponse.FromError("LM Studio returned an invalid tool call ID or name.");
                JObject arguments;
                try { arguments = JObject.Parse(call["function"]?["arguments"]?.Value<string>() ?? "{}"); }
                catch (JsonException) { return ModelResponse.FromError("LM Studio returned malformed tool arguments; no actions were executed."); }
                calls.Add(new ToolCall { Id = id, Name = name, Arguments = arguments });
                parts.Add(ContentPart.FromToolUse(id, name, arguments));
            }
            // Never execute a partially generated action.
            if (choice["finish_reason"]?.Value<string>() == "length")
                return ModelResponse.FromError("Model reached the output limit; no actions were executed. Use a smaller response or increase the server context/output allowance.");
            int input = root["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;
            int output = root["usage"]?["completion_tokens"]?.Value<int>() ?? 0;
            return new ModelResponse { Success = true, Content = content, AssistantParts = parts, ToolCalls = calls,
                StopReason = calls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn,
                InputTokens = input, OutputTokens = output, TokensUsed = input + output, RawJson = json };
        }
    }
}
