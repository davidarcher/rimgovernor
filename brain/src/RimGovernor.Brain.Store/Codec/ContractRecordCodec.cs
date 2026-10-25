using System.Text.Json;
using RimGovernor.Contracts.Wire;

namespace RimGovernor.Brain.Store.Codec;

/// <summary>Retains a canonical native record using its generated contract metadata.</summary>
internal sealed class ContractRecordCodec<T> : ICodec<T>, IBoxedCodec where T : class
{
    public void Encode(Utf8JsonWriter w, T value, Codecs codecs) =>
        JsonSerializer.Serialize(w, value, WireJson.Info<T>());

    public T Decode(JsonElement e, Codecs codecs) =>
        JsonSerializer.Deserialize(e, WireJson.Info<T>())
        ?? throw new StoreCorruptException(typeof(T).Name + " record is null");

    public void EncodeBoxed(Utf8JsonWriter w, object value, Codecs codecs) =>
        Encode(w, (T)value, codecs);
}
