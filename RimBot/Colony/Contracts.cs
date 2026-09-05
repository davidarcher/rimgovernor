using Newtonsoft.Json.Linq;

namespace RimBot.Tools
{
    public class ToolDefinition
    {
        public string Name;
        public string Description;
        public string ParametersJson;
    }
    public class ToolCall
    {
        public string Id;
        public string Name;
        public JObject Arguments;
    }
}
