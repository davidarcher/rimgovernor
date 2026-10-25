using System.Text.Json;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store.Codec;

internal sealed class PawnBiotechStatusConverter : ObjectCodec<PawnBiotechStatus>
{
    protected override PawnBiotechStatus Read(JsonElement e, Codecs o) => e.Str("Kind") switch
    {
        "inactive" => new PawnBiotechStatus.Inactive(),
        "active" => new PawnBiotechStatus.Active(e.Get<PawnBiotech>("Facts", o)),
        var kind => throw new JsonException($"Unknown pawn Biotech state: {kind}."),
    };

    protected override void WriteFields(Utf8JsonWriter w, PawnBiotechStatus v, Codecs o)
    {
        if (v is PawnBiotechStatus.Inactive)
        {
            w.WriteString("Kind", "inactive");
        }
        else if (v is PawnBiotechStatus.Active active)
        {
            w.WriteString("Kind", "active");
            w.Put("Facts", active.Facts, o);
        }
        else
        {
            throw new JsonException($"Unknown pawn Biotech state: {v.GetType().Name}.");
        }
    }
}
