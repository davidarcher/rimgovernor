using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using RimGovernor.Contracts.PlacementPreview;

internal static class Program
{
    private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        DateParseHandling = DateParseHandling.None,
        FloatParseHandling = FloatParseHandling.Decimal,
        NullValueHandling = NullValueHandling.Include,
        Culture = System.Globalization.CultureInfo.InvariantCulture
    };

    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass placement-request-cases.json");
        var fixture = JsonConvert.DeserializeObject<RequestFixture>(
            File.ReadAllText(args[0], new UTF8Encoding(false, true)), Settings)
            ?? throw new FormatException("Missing fixture");
        if (fixture.Version != 1 || fixture.Cases.Count == 0) throw new FormatException("Invalid fixture version/count");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in fixture.Cases)
        {
            if (item.ID.Length == 0 || !ids.Add(item.ID)) throw new FormatException("Duplicate/empty case ID");
            string raw = Expand(item.Input);
            bool accepted;
            try
            {
                switch (item.Target)
                {
                    case "outer_arguments": PlacementPreviewArguments.Decode(raw); break;
                    case "placements": PlacementBatch.Decode(raw); break;
                    default: throw new InvalidOperationException("Unknown fixture target " + item.Target);
                }
                accepted = true;
            }
            catch (FormatException) { accepted = false; }
            if (accepted != item.Canonical.Accepted) throw new InvalidOperationException(item.ID + ": accepted=" + accepted);
            if (accepted) RoundTrip(item.Target, raw);
        }
        foreach (string invalid in new[] { "{\"placements\":\"" + '\uD800' + "\"}", new string(' ', (1 << 20) + 1) })
        {
            bool rejected = false;
            try { PlacementPreviewArguments.Decode(invalid); }
            catch (FormatException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Invalid scalar or oversized input accepted");
        }
        PlacementPreviewArgumentsJsonBoundary.Parse(new string('[', 64) + new string(']', 64));
        bool depthRejected = false;
        try { PlacementPreviewArgumentsJsonBoundary.Parse(new string('[', 65) + new string(']', 65)); }
        catch (FormatException) { depthRejected = true; }
        if (!depthRejected) throw new InvalidOperationException("Empty 65th container accepted");
        Console.WriteLine("C# request contract: " + fixture.Cases.Count + " shared cases and accepted-value serializer round trips passed.");
        return 0;
    }

    private static void RoundTrip(string target, string raw)
    {
        switch (target)
        {
            case "outer_arguments":
                var arguments = PlacementPreviewArguments.Decode(raw);
                var argumentCopy = PlacementPreviewArguments.Decode(JsonConvert.SerializeObject(arguments, Settings));
                if (argumentCopy.Placements != arguments.Placements) throw new InvalidOperationException("Argument serialization changed text");
                break;
            case "placements":
                var batch = PlacementBatch.Decode(raw);
                var batchCopy = PlacementBatch.Decode(JsonConvert.SerializeObject(batch.Values, Settings));
                if (batchCopy.Values.Count != batch.Values.Count) throw new InvalidOperationException("Batch serialization changed count");
                for (int i = 0; i < batch.Values.Count; i++)
                {
                    var original = batch.Values[i];
                    var copy = batchCopy.Values[i];
                    if (original.DefName != copy.DefName || original.X != copy.X || original.Z != copy.Z ||
                        original.Rotation != copy.Rotation || original.Stuff != copy.Stuff)
                        throw new InvalidOperationException("Candidate serialization changed values");
                }
                break;
            default: throw new InvalidOperationException("Unknown fixture target " + target);
        }
    }

    private static string Expand(Input input)
    {
        string raw = input.JSON;
        if (input.Kind == "raw")
        {
            if (input.Replacements.Count != 0) throw new FormatException("Unexpected replacements");
            return raw;
        }
        if (input.Kind != "template") throw new FormatException("Unknown input kind");
        foreach (var replacement in input.Replacements)
        {
            int index = raw.IndexOf(replacement.Token, StringComparison.Ordinal);
            if (replacement.Token.Length == 0 || index < 0 || index != raw.LastIndexOf(replacement.Token, StringComparison.Ordinal)
                || replacement.Count < 0 || replacement.Count > 65536) throw new FormatException("Invalid literal expansion");
            var text = new StringBuilder();
            for (int i = 0; i < replacement.Count; i++) text.Append(replacement.Text);
            raw = raw.Substring(0, index) + text + raw.Substring(index + replacement.Token.Length);
        }
        return raw;
    }

    private sealed class RequestFixture
    {
        [JsonProperty("version", Required = Required.Always)] public int Version { get; set; }
        [JsonProperty("cases", Required = Required.Always)] public List<Case> Cases { get; set; } = new List<Case>();
    }
    private sealed class Case
    {
        [JsonProperty("id", Required = Required.Always)] public string ID { get; set; } = "";
        [JsonProperty("target", Required = Required.Always)] public string Target { get; set; } = "";
        [JsonProperty("input", Required = Required.Always)] public Input Input { get; set; } = new Input();
        [JsonProperty("canonical", Required = Required.Always)] public Expected Canonical { get; set; } = new Expected();
    }
    private sealed class Input
    {
        [JsonProperty("kind", Required = Required.Always)] public string Kind { get; set; } = "";
        [JsonProperty("json", Required = Required.Always)] public string JSON { get; set; } = "";
        [JsonProperty("replacements")] public List<Replacement> Replacements { get; set; } = new List<Replacement>();
    }
    private sealed class Replacement
    {
        [JsonProperty("token", Required = Required.Always)] public string Token { get; set; } = "";
        [JsonProperty("text", Required = Required.Always)] public string Text { get; set; } = "";
        [JsonProperty("count", Required = Required.Always)] public int Count { get; set; }
    }
    private sealed class Expected
    {
        [JsonProperty("accepted", Required = Required.Always)] public bool Accepted { get; set; }
    }
}
