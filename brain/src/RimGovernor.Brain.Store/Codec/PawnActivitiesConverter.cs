using System.Text.Json;
using RimGovernor.Brain.Domain;
namespace RimGovernor.Brain.Store.Codec;
public sealed class PawnActivitiesConverter : ObjectCodec<PawnActivities>
{
    protected override PawnActivities Read(JsonElement e, Codecs o)
    {
        var orders = e.GetProperty("Orders").EnumerateArray().Select(r => new PawnActivityOrder(
            r.Str("Pawn"), r.Str("Assignment"), r.Str("Method"), r.GetProperty("ExpectedRevision").GetInt64(),
            r.GetProperty("Revision").GetInt64(), r.GetProperty("Release").GetBoolean(),
            r.GetProperty("Steps").EnumerateArray().Select(s => new PawnActivityStep(
                (PawnActivityKind)s.GetProperty("Kind").GetInt32(), s.Str("Target"), s.Str("WorkGiver"), s.Str("Job"),
                s.GetProperty("Count").GetInt32(), s.GetProperty("Deadline").GetInt64(), s.Str("WaitForFilth"))
                { Destination = s.GetProperty("Destination") is var d && d.ValueKind != JsonValueKind.Null
                    ? new Cell(d.GetProperty("X").GetInt32(), d.GetProperty("Z").GetInt32()) : null,
                  Cooking = ReadCooking(s.GetProperty("Cooking")) }).ToArray())
            { OutputDelivery = ReadOutput(r.GetProperty("OutputDelivery")) }).ToArray();
        var value = new PawnActivities(e.GetProperty("AuthorityGeneration").GetUInt64(), orders);
        value.Validate(); return value;
    }
    private static PawnActivityCooking? ReadCooking(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(e.Str("Bill"), e.Str("Recipe"), EquatableArray.From(e.GetProperty("Ingredients").EnumerateArray()
            .Select(i => new PawnActivityIngredient(i.Str("Thing"), i.GetProperty("Count").GetInt32()))));
    private static ActivityOutputDelivery? ReadOutput(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(e.Str("Resource"), new(e.GetProperty("Origin").GetProperty("X").GetInt32(), e.GetProperty("Origin").GetProperty("Z").GetInt32()),
            new(e.GetProperty("Destination").GetProperty("X").GetInt32(), e.GetProperty("Destination").GetProperty("Z").GetInt32()),
            e.GetProperty("Count").GetInt32(), e.GetProperty("Deadline").GetInt64(), e.GetProperty("AttemptsLeft").GetInt32());
    protected override void WriteFields(Utf8JsonWriter w, PawnActivities v, Codecs o)
    {
        v.Validate(); w.WriteNumber("AuthorityGeneration", v.AuthorityGeneration);
        w.WriteStartArray("Orders");
        foreach (var r in v.Orders)
        {
            w.WriteStartObject(); w.WriteString("Pawn", r.Pawn); w.WriteString("Assignment", r.Assignment); w.WriteString("Method", r.Method);
            w.WriteNumber("ExpectedRevision", r.ExpectedRevision); w.WriteNumber("Revision", r.Revision); w.WriteBoolean("Release", r.Release);
            if (r.OutputDelivery is { } output)
            {
                w.WriteStartObject("OutputDelivery"); w.WriteString("Resource", output.Resource); w.WriteNumber("Count", output.Count);
                w.WriteNumber("Deadline", output.Deadline); w.WriteNumber("AttemptsLeft", output.AttemptsLeft);
                w.WriteStartObject("Origin"); w.WriteNumber("X", output.Origin.X); w.WriteNumber("Z", output.Origin.Z); w.WriteEndObject();
                w.WriteStartObject("Destination"); w.WriteNumber("X", output.Destination.X); w.WriteNumber("Z", output.Destination.Z); w.WriteEndObject();
                w.WriteEndObject();
            }
            else w.WriteNull("OutputDelivery");
            w.WriteStartArray("Steps");
            foreach (var s in r.Steps)
            {
                w.WriteStartObject(); w.WriteNumber("Kind", (int)s.Kind); w.WriteString("Target", s.Target); w.WriteString("WorkGiver", s.WorkGiver);
                w.WriteString("Job", s.Job); w.WriteNumber("Count", s.Count); w.WriteNumber("Deadline", s.Deadline); w.WriteString("WaitForFilth", s.WaitForFilth);
                if (s.Destination is { } d)
                { w.WriteStartObject("Destination"); w.WriteNumber("X", d.X); w.WriteNumber("Z", d.Z); w.WriteEndObject(); }
                else w.WriteNull("Destination");
                if (s.Cooking is { } cooking)
                {
                    w.WriteStartObject("Cooking"); w.WriteString("Bill", cooking.Bill); w.WriteString("Recipe", cooking.Recipe);
                    w.WriteStartArray("Ingredients");
                    foreach (var ingredient in cooking.Ingredients)
                    { w.WriteStartObject(); w.WriteString("Thing", ingredient.Thing); w.WriteNumber("Count", ingredient.Count); w.WriteEndObject(); }
                    w.WriteEndArray(); w.WriteEndObject();
                }
                else w.WriteNull("Cooking");
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
