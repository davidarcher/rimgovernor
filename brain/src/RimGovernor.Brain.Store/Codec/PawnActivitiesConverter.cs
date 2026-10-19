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
            r.GetProperty("Steps").EnumerateArray().Select(ReadStep).ToArray())
            { OutputDelivery = ReadOutput(r.GetProperty("OutputDelivery")), Task = r.Str("Task"), Preparing = r.GetProperty("Preparing").GetBoolean() }).ToArray();
        var value = new PawnActivities(e.GetProperty("AuthorityGeneration").GetUInt64(), orders)
        { Tasks = EquatableArray.From(e.GetProperty("Tasks").EnumerateArray().Select(t => new SelectedActivityTask(t.Str("Id"), t.Str("Pawn"),
            EquatableArray.From(t.GetProperty("Prerequisites").EnumerateArray().Select(p => p.GetString()!)),
            ReadStep(t.GetProperty("Step")),
            new(t.GetProperty("X").GetInt32(), t.GetProperty("Z").GetInt32()), t.GetProperty("DepartTick").GetInt64())
            { Resource = t.Str("Resource"), SupportDemand = t.Str("SupportDemand"), ProductStuff = t.Str("ProductStuff"), SiteDefinition = t.Str("SiteDefinition"), CompletionMustBeObserved = t.GetProperty("CompletionMustBeObserved").GetBoolean(), AttemptsLeft = t.GetProperty("AttemptsLeft").GetInt32(),
                ProductiveWorkTicks = t.GetProperty("ProductiveWorkTicks") is var work && work.ValueKind != JsonValueKind.Null ? Fact.Known(work.GetDouble()) : default,
                Inputs = EquatableArray.From(t.GetProperty("Inputs").EnumerateArray().Select(i => new SelectedActivityInput(
                    i.Str("Resource"), i.Str("Lot"), i.GetProperty("Count").GetInt32(),
                    new(i.GetProperty("X").GetInt32(), i.GetProperty("Z").GetInt32()), i.Str("Provenance"))
                    { Observed = i.GetProperty("Observed").GetBoolean(),
                        Producers = EquatableArray.From(i.GetProperty("Producers").EnumerateArray().Select(p => p.GetString()!)) })),
                Source = t.GetProperty("Source") is var source && source.ValueKind != JsonValueKind.Null
                    ? new(source.GetProperty("X").GetInt32(), source.GetProperty("Z").GetInt32()) : null })) };
        value.Validate(); return value;
    }
    private static PawnActivityStep ReadStep(JsonElement s) => new(
        (PawnActivityKind)s.GetProperty("Kind").GetInt32(), s.Str("Target"), s.Str("WorkGiver"), s.Str("Job"),
        s.GetProperty("Count").GetInt32(), s.GetProperty("Deadline").GetInt64(), s.Str("WaitForFilth"))
        { Destination = s.GetProperty("Destination") is var d && d.ValueKind != JsonValueKind.Null
            ? new Cell(d.GetProperty("X").GetInt32(), d.GetProperty("Z").GetInt32()) : null,
          Practice = ReadPractice(s.GetProperty("Practice")), Recipe = ReadRecipe(s.GetProperty("Recipe")), NotBefore = s.GetProperty("NotBefore").GetInt64(), Container = s.Str("Container"),
          SupportDemand = s.Str("SupportDemand"), Medicine = s.Str("Medicine"),
          Targets = EquatableArray.From(s.GetProperty("Targets").EnumerateArray().Select(t => t.GetString()!)),
          Cells = EquatableArray.From(s.GetProperty("Cells").EnumerateArray().Select(c => new Cell(c.GetProperty("X").GetInt32(), c.GetProperty("Z").GetInt32()))) };
    private static PawnActivityPractice? ReadPractice(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(e.Str("Skill"), e.Str("Role"), e.GetProperty("RequirementIndex").GetInt32(), e.Str("Partner"), e.Str("PartnerTarget"), e.GetProperty("DurationTicks").GetInt32()) { Beneficiary = e.Str("Beneficiary") };
    private static PawnActivityRecipe? ReadRecipe(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(e.Str("Bill"), e.Str("Recipe"), EquatableArray.From(e.GetProperty("Ingredients").EnumerateArray()
            .Select(i => new PawnActivityIngredient(i.Str("Thing"), i.GetProperty("Count").GetInt32())))) { Resource = e.Str("Resource"), Stuff = e.Str("Stuff"), Unfinished = e.Str("Unfinished") };
    private static ActivityOutputDelivery? ReadOutput(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(e.Str("Resource"), new(e.GetProperty("Origin").GetProperty("X").GetInt32(), e.GetProperty("Origin").GetProperty("Z").GetInt32()),
            new(e.GetProperty("Destination").GetProperty("X").GetInt32(), e.GetProperty("Destination").GetProperty("Z").GetInt32()),
            e.GetProperty("Count").GetInt32(), e.GetProperty("Deadline").GetInt64(), e.GetProperty("AttemptsLeft").GetInt32())
        { Recipe = e.GetProperty("Recipe") is var c && c.ValueKind != JsonValueKind.Null
            ? new(c.Str("Bench"), c.Str("Bill"), c.Str("Recipe"), c.Str("WorkGiver"),
                new(c.GetProperty("Origin").GetProperty("X").GetInt32(), c.GetProperty("Origin").GetProperty("Z").GetInt32()),
                c.Str("Product"), c.GetProperty("ProductCount").GetInt32(),
                new(c.GetProperty("Destination").GetProperty("X").GetInt32(), c.GetProperty("Destination").GetProperty("Z").GetInt32())) : null };
    protected override void WriteFields(Utf8JsonWriter w, PawnActivities v, Codecs o)
    {
        v.Validate(); w.WriteNum("AuthorityGeneration", v.AuthorityGeneration);
        w.WriteStartArray("Tasks");
        foreach (var task in v.Tasks)
        {
            w.WriteStartObject(); w.WriteString("Id", task.Id); w.WriteString("Pawn", task.Pawn);
            w.WriteNum("X", task.Location.X); w.WriteNum("Z", task.Location.Z); w.WriteNum("DepartTick", task.DepartTick);
            w.WriteString("Resource", task.Resource); w.WriteString("SiteDefinition", task.SiteDefinition); w.WriteNum("AttemptsLeft", task.AttemptsLeft);
            w.WriteString("ProductStuff", task.ProductStuff);
            if (task.ProductiveWorkTicks.TryGetValue(out var work)) w.WriteNum("ProductiveWorkTicks", work); else w.WriteNull("ProductiveWorkTicks");
            w.WriteString("SupportDemand", task.SupportDemand);
            w.WriteBoolean("CompletionMustBeObserved", task.CompletionMustBeObserved);
            w.WriteStartArray("Inputs");
            foreach (var input in task.Inputs)
            {
                w.WriteStartObject(); w.WriteString("Resource", input.Resource); w.WriteString("Lot", input.Lot);
                w.WriteNum("Count", input.Count); w.WriteNum("X", input.Location.X); w.WriteNum("Z", input.Location.Z);
                w.WriteString("Provenance", input.Provenance); w.WriteBoolean("Observed", input.Observed);
                w.WritePropertyName("Producers"); w.WriteStartArray(); foreach (var producer in input.Producers) w.WriteStringValue(producer); w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("Prerequisites"); foreach (var dependency in task.Prerequisites) w.WriteStringValue(dependency); w.WriteEndArray();
            if (task.Source is { } source)
            { w.WriteStartObject("Source"); w.WriteNum("X", source.X); w.WriteNum("Z", source.Z); w.WriteEndObject(); }
            else w.WriteNull("Source");
            w.WritePropertyName("Step"); WriteStep(w, task.Step); w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("Orders");
        foreach (var r in v.Orders)
        {
            w.WriteStartObject(); w.WriteString("Pawn", r.Pawn); w.WriteString("Assignment", r.Assignment); w.WriteString("Method", r.Method);
            w.WriteNum("ExpectedRevision", r.ExpectedRevision); w.WriteNum("Revision", r.Revision); w.WriteBoolean("Release", r.Release);
            w.WriteString("Task", r.Task); w.WriteBoolean("Preparing", r.Preparing);
            if (r.OutputDelivery is { } output)
            {
                w.WriteStartObject("OutputDelivery"); w.WriteString("Resource", output.Resource); w.WriteNum("Count", output.Count);
                w.WriteNum("Deadline", output.Deadline); w.WriteNum("AttemptsLeft", output.AttemptsLeft);
                w.WriteStartObject("Origin"); w.WriteNum("X", output.Origin.X); w.WriteNum("Z", output.Origin.Z); w.WriteEndObject();
                w.WriteStartObject("Destination"); w.WriteNum("X", output.Destination.X); w.WriteNum("Z", output.Destination.Z); w.WriteEndObject();
                if (output.Recipe is { } next)
                {
                    w.WriteStartObject("Recipe"); w.WriteString("Bench", next.Bench); w.WriteString("Bill", next.Bill);
                    w.WriteString("Recipe", next.Recipe); w.WriteString("WorkGiver", next.WorkGiver); w.WriteString("Product", next.Product);
                    w.WriteNum("ProductCount", next.ProductCount);
                    w.WriteStartObject("Origin"); w.WriteNum("X", next.Origin.X); w.WriteNum("Z", next.Origin.Z); w.WriteEndObject();
                    w.WriteStartObject("Destination"); w.WriteNum("X", next.Destination.X); w.WriteNum("Z", next.Destination.Z); w.WriteEndObject();
                    w.WriteEndObject();
                }
                else w.WriteNull("Recipe");
                w.WriteEndObject();
            }
            else w.WriteNull("OutputDelivery");
            w.WriteStartArray("Steps");
            foreach (var s in r.Steps) WriteStep(w, s);
            w.WriteEndArray(); w.WriteEndObject();
        }
        w.WriteEndArray();
    }
    private static void WriteStep(Utf8JsonWriter w, PawnActivityStep s)
    {
        w.WriteStartObject(); w.WriteNum("Kind", (int)s.Kind); w.WriteString("Target", s.Target); w.WriteString("WorkGiver", s.WorkGiver);
        w.WriteString("Job", s.Job); w.WriteNum("Count", s.Count); w.WriteNum("Deadline", s.Deadline); w.WriteString("WaitForFilth", s.WaitForFilth);
        w.WriteNum("NotBefore", s.NotBefore); w.WriteString("Container", s.Container);
        w.WriteString("SupportDemand", s.SupportDemand); w.WriteString("Medicine", s.Medicine);
        w.WriteStartArray("Targets"); foreach (var target in s.Targets) w.WriteStringValue(target); w.WriteEndArray();
        w.WriteStartArray("Cells");
        foreach (var cell in s.Cells) { w.WriteStartObject(); w.WriteNum("X", cell.X); w.WriteNum("Z", cell.Z); w.WriteEndObject(); }
        w.WriteEndArray();
        if (s.Practice is { } practice)
        {
            w.WriteStartObject("Practice"); w.WriteString("Beneficiary", practice.Beneficiary); w.WriteString("Skill", practice.Skill); w.WriteString("Role", practice.Role);
            w.WriteNum("RequirementIndex", practice.RequirementIndex); w.WriteString("Partner", practice.Partner);
            w.WriteString("PartnerTarget", practice.PartnerTarget); w.WriteNum("DurationTicks", practice.DurationTicks); w.WriteEndObject();
        }
        else w.WriteNull("Practice");
        if (s.Destination is { } d)
        { w.WriteStartObject("Destination"); w.WriteNum("X", d.X); w.WriteNum("Z", d.Z); w.WriteEndObject(); }
        else w.WriteNull("Destination");
        if (s.Recipe is { } cooking)
        {
            w.WriteStartObject("Recipe"); w.WriteString("Bill", cooking.Bill); w.WriteString("Recipe", cooking.Recipe);
            w.WriteString("Stuff", cooking.Stuff); w.WriteString("Unfinished", cooking.Unfinished);
            w.WriteString("Resource", cooking.Resource); w.WriteStartArray("Ingredients");
            foreach (var ingredient in cooking.Ingredients)
            { w.WriteStartObject(); w.WriteString("Thing", ingredient.Thing); w.WriteNum("Count", ingredient.Count); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteEndObject();
        }
        else w.WriteNull("Recipe");
        w.WriteEndObject();
    }
}
