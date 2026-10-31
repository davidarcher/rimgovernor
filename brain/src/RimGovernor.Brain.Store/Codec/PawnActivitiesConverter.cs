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
            { OutputDelivery = ReadOutput(r.GetProperty("OutputDelivery")), Task = r.Str("Task"), Preparing = r.GetProperty("Preparing").GetBoolean(),
              Purpose = ReadPurpose(r.GetProperty("Purpose")) }).ToArray();
        var value = new PawnActivities(e.GetProperty("AuthorityGeneration").GetUInt64(), orders)
        { Tasks = EquatableArray.From(e.GetProperty("Tasks").EnumerateArray().Select(t => new SelectedActivityTask(t.Str("Id"), t.Str("Pawn"),
            EquatableArray.From(t.GetProperty("Prerequisites").EnumerateArray().Select(p => p.GetString()!)),
            ReadStep(t.GetProperty("Step")),
            new(t.GetProperty("X").GetInt32(), t.GetProperty("Z").GetInt32()), t.GetProperty("DepartTick").GetInt64())
            { Resource = t.Str("Resource"), SupportDemand = t.Str("SupportDemand"), SiteDefinition = t.Str("SiteDefinition"), CompletionMustBeObserved = t.GetProperty("CompletionMustBeObserved").GetBoolean(),
                Inputs = EquatableArray.From(t.GetProperty("Inputs").EnumerateArray().Select(i => new SelectedActivityInput(
                    i.Str("Resource"), i.Str("Lot"), i.GetProperty("Count").GetInt32(),
                    new(i.GetProperty("X").GetInt32(), i.GetProperty("Z").GetInt32()), i.Str("Provenance"))
                    { Observed = i.GetProperty("Observed").GetBoolean(),
                        Producers = EquatableArray.From(i.GetProperty("Producers").EnumerateArray().Select(p => p.GetString()!)) })),
                // The old flat task used null to mean its Location, including stationary work.
                Source = t.GetProperty("Source") is { ValueKind: JsonValueKind.Null }
                    ? new(t.GetProperty("X").GetInt32(), t.GetProperty("Z").GetInt32())
                    : ReadCell(t.GetProperty("Source")),
                UseUntilTick = t.GetProperty("UseUntilTick").GetInt64() })) };
        value.Validate(); return value;
    }
    private static PawnActivityStep ReadStep(JsonElement s) => StepForm(s, new PawnActivityStep(
        (PawnActivityKind)s.GetProperty("Kind").GetInt32(), s.Str("Target"), s.Str("WorkGiver"), s.Str("Job"),
        s.GetProperty("Count").GetInt32(), s.GetProperty("Deadline").GetInt64())
        { Practice = ReadPractice(s.GetProperty("Practice")), NotBefore = s.GetProperty("NotBefore").GetInt64(), Container = s.Str("Container"),
          SupportDemand = s.Str("SupportDemand"), Medicine = s.Str("Medicine"),
          FullLoad = s.GetProperty("FullLoad").GetBoolean(),
          Collect = EquatableArray.From(s.GetProperty("Collect").EnumerateArray().Select(t => t.GetString()!)) });
    private static Cell ReadCell(JsonElement e) => new(e.GetProperty("X").GetInt32(), e.GetProperty("Z").GetInt32());
    private static PawnActivityStep StepForm(JsonElement e, PawnActivityStep step)
    {
        if (step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Approach)
            return step.At(ReadCell(e.GetProperty("Destination")));
        // Old flat steps wrote Destination: null for every non-cell kind.
        if (e.TryGetProperty("Destination", out var destination) && destination.ValueKind != JsonValueKind.Null)
            throw new JsonException("A non-cell activity cannot carry a destination");
        return step;
    }
    private static PawnActivityPractice? ReadPractice(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(e.Str("Skill"), e.Str("Partner"), e.Str("PartnerTarget"), e.GetProperty("DurationTicks").GetInt32());
    private static InterventionPurpose? ReadPurpose(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(Wire.TryParse<InterventionPurposeKind>(e.Str("Kind"), out var kind) ? kind
                : throw new StoreCorruptException("unknown intervention purpose kind \"" + e.Str("Kind") + "\""),
            e.Str("Subject"), e.Str("SubjectPawn"), e.GetProperty("EvidenceTick").GetInt64());
    private static ActivityOutputDelivery? ReadOutput(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
        : new(e.Str("Resource"), new(e.GetProperty("Origin").GetProperty("X").GetInt32(), e.GetProperty("Origin").GetProperty("Z").GetInt32()),
            new(e.GetProperty("Destination").GetProperty("X").GetInt32(), e.GetProperty("Destination").GetProperty("Z").GetInt32()),
            e.GetProperty("Count").GetInt32(), e.GetProperty("Deadline").GetInt64());
    protected override void WriteFields(Utf8JsonWriter w, PawnActivities v, Codecs o)
    {
        v.Validate(); w.WriteNum("AuthorityGeneration", v.AuthorityGeneration);
        w.WriteStartArray("Tasks");
        foreach (var task in v.Tasks)
        {
            w.WriteStartObject(); w.WriteString("Id", task.Id); w.WriteString("Pawn", task.Pawn);
            w.WriteNum("X", task.Location.X); w.WriteNum("Z", task.Location.Z); w.WriteNum("DepartTick", task.DepartTick);
            w.WriteNum("UseUntilTick", task.UseUntilTick);
            w.WriteString("Resource", task.Resource); w.WriteString("SiteDefinition", task.SiteDefinition);
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
            w.WriteStartObject("Source"); w.WriteNum("X", task.Source.X); w.WriteNum("Z", task.Source.Z); w.WriteEndObject();
            w.WritePropertyName("Step"); WriteStep(w, task.Step);
            w.WriteEndObject();
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
                w.WriteNum("Deadline", output.Deadline);
                w.WriteStartObject("Origin"); w.WriteNum("X", output.Origin.X); w.WriteNum("Z", output.Origin.Z); w.WriteEndObject();
                w.WriteStartObject("Destination"); w.WriteNum("X", output.Destination.X); w.WriteNum("Z", output.Destination.Z); w.WriteEndObject();
                w.WriteEndObject();
            }
            else w.WriteNull("OutputDelivery");
            if (r.Purpose is { } purpose)
            {
                w.WriteStartObject("Purpose"); w.WriteString("Kind", Wire.Name(purpose.Kind)); w.WriteString("Subject", purpose.Subject);
                w.WriteString("SubjectPawn", purpose.SubjectPawn); w.WriteNum("EvidenceTick", purpose.EvidenceTick); w.WriteEndObject();
            }
            else w.WriteNull("Purpose");
            w.WriteStartArray("Steps");
            foreach (var s in r.Steps) WriteStep(w, s);
            w.WriteEndArray(); w.WriteEndObject();
        }
        w.WriteEndArray();
    }
    private static void WriteStep(Utf8JsonWriter w, PawnActivityStep s)
    {
        w.WriteStartObject(); w.WriteNum("Kind", (int)s.Kind); w.WriteString("Target", s.Target); w.WriteString("WorkGiver", s.WorkGiver);
        w.WriteString("Job", s.Job); w.WriteNum("Count", s.Count); w.WriteNum("Deadline", s.Deadline);
        w.WriteNum("NotBefore", s.NotBefore); w.WriteString("Container", s.Container);
        w.WriteString("SupportDemand", s.SupportDemand); w.WriteString("Medicine", s.Medicine);
        w.WriteBoolean("FullLoad", s.FullLoad);
        w.WriteStartArray("Collect"); foreach (var stack in s.Collect) w.WriteStringValue(stack); w.WriteEndArray();
        if (s.Practice is { } practice)
        {
            w.WriteStartObject("Practice"); w.WriteString("Skill", practice.Skill); w.WriteString("Partner", practice.Partner);
            w.WriteString("PartnerTarget", practice.PartnerTarget); w.WriteNum("DurationTicks", practice.DurationTicks); w.WriteEndObject();
        }
        else w.WriteNull("Practice");
        if (s is CellActivityStep { Destination: var d })
        { w.WriteStartObject("Destination"); w.WriteNum("X", d.X); w.WriteNum("Z", d.Z); w.WriteEndObject(); }
        w.WriteEndObject();
    }
}
