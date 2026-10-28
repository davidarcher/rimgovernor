using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

public sealed class OrderedWorkConverter : ObjectCodec<OrderedWork>
{
    protected override OrderedWork Read(JsonElement e, Codecs o)
    {
        var value = new OrderedWork(new PawnId(e.Str("Pawn")), e.GetProperty("Sequence").GetUInt64(), e.Str("Purpose"),
            EquatableArray.From(e.GetProperty("Jobs").EnumerateArray().Select(j => new OrderedWorkStep(j.Str("Job"), j.Str("Target")))),
            e.GetProperty("Cancel").GetBoolean());
        value.Validate();
        return value;
    }

    protected override void WriteFields(Utf8JsonWriter w, OrderedWork v, Codecs o)
    {
        w.WriteString("Pawn", v.Pawn.Value);
        w.WriteNumber("Sequence", v.Sequence);
        w.WriteString("Purpose", v.Purpose);
        w.WriteBoolean("Cancel", v.Cancel);
        w.WriteStartArray("Jobs");
        foreach (var job in v.Jobs)
        {
            w.WriteStartObject();
            w.WriteString("Job", job.Job);
            w.WriteString("Target", job.Target);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

internal sealed class OrderedWorkObservationConverter : ObjectCodec<OrderedWorkObservation>
{
    protected override OrderedWorkObservation Read(JsonElement e, Codecs o) =>
        new(e.Get<OrderedWork>("Request", o), EquatableArray.From(e.GetProperty("Jobs").EnumerateArray()
            .Select(j => new OrderedWorkJob(j.I32("JobId"), Enum.Parse<OrderedWorkStatus>(j.Str("Status"))))));

    protected override void WriteFields(Utf8JsonWriter w, OrderedWorkObservation v, Codecs o)
    {
        w.Put("Request", v.Request, o);
        w.WriteStartArray("Jobs");
        foreach (var job in v.Jobs)
        {
            w.WriteStartObject(); w.WriteNum("JobId", job.JobId); w.WriteString("Status", job.Status.ToString()); w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
