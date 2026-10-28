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
