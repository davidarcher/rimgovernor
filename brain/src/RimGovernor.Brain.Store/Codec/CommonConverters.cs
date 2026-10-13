using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

// Identifiers persist as their bare string or number.

public sealed class ColonyIdConverter() : StringCodec<ColonyId>(s => new ColonyId(s), v => v.Value);

public sealed class LoadIdConverter() : StringCodec<LoadId>(s => new LoadId(s), v => v.Value);

public sealed class ActionIdConverter() : StringCodec<ActionId>(s => new ActionId(s), v => v.Value);

public sealed class PlanIdConverter() : StringCodec<PlanId>(s => new PlanId(s), v => v.Value);

public sealed class IncidentIdConverter() : StringCodec<IncidentId>(s => new IncidentId(s), v => v.Value);

public sealed class PawnIdConverter() : StringCodec<PawnId>(s => new PawnId(s), v => v.Value);

public sealed class QuestIdConverter() : StringCodec<QuestId>(s => new QuestId(s), v => v.Value);

public sealed class ConcernIdConverter() : StringCodec<ConcernId>(s => new ConcernId(s), v => v.Value);

public sealed class StandardIdConverter() : StringCodec<StandardId>(s => new StandardId(s), v => v.Value);

public sealed class MethodIdConverter() : StringCodec<MethodId>(s => new MethodId(s), v => v.Value);

public sealed class DomainControllerSessionIdConverter() : StringCodec<RimGovernor.Brain.Domain.ControllerSessionId>(s => new RimGovernor.Brain.Domain.ControllerSessionId(s), v => v.Value);

public sealed class MapIdConverter() : NumberCodec<MapId>(e => new MapId(e.GetInt32()), (w, v) => w.WriteNumberValue(v.Value));

public sealed class AttemptIdConverter() : NumberCodec<AttemptId>(e => new AttemptId(e.GetUInt64()), (w, v) => w.WriteNumberValue(v.Value));

public sealed class PlanRevisionConverter() : NumberCodec<PlanRevision>(e => new PlanRevision(e.GetUInt64()), (w, v) => w.WriteNumberValue(v.Value));

public sealed class NativeGenerationConverter() : NumberCodec<NativeGeneration>(e => new NativeGeneration(e.GetUInt64()), (w, v) => w.WriteNumberValue(v.Value));

public sealed class TickConverter() : NumberCodec<Tick>(e => new Tick(e.GetInt64()), (w, v) => w.WriteNumberValue(v.Value));

public sealed class CellConverter : ObjectCodec<Cell>
{
    protected override Cell Read(JsonElement e, Codecs o) => new(e.I32("X"), e.I32("Z"));

    protected override void WriteFields(Utf8JsonWriter w, Cell v, Codecs o)
    {
        w.WriteNumber("X", v.X);
        w.WriteNumber("Z", v.Z);
    }
}

public sealed class GenerationSnapshotConverter : ObjectCodec<GenerationSnapshot>
{
    protected override GenerationSnapshot Read(JsonElement e, Codecs o) =>
        new(
            e.Get<ColonyId>("Colony", o),
            e.Get<MapId>("Map", o),
            e.Get<LoadId>("Load", o),
            e.Get<PlanId>("Plan", o),
            e.Get<PlanRevision>("Revision", o),
            e.Get<NativeGeneration>("Native", o)
        );

    protected override void WriteFields(Utf8JsonWriter w, GenerationSnapshot v, Codecs o)
    {
        w.Put("Colony", v.Colony, o);
        w.Put("Map", v.Map, o);
        w.Put("Load", v.Load, o);
        w.Put("Plan", v.Plan, o);
        w.Put("Revision", v.Revision, o);
        w.Put("Native", v.Native, o);
    }
}

public sealed class ActionDependencyConverter : ObjectCodec<ActionDependency>
{
    protected override ActionDependency Read(JsonElement e, Codecs o) =>
        new(e.Get<ActionId>("Action", o), e.Get<ActionId>("Requires", o), e.BoolOr("Coupled"));

    protected override void WriteFields(Utf8JsonWriter w, ActionDependency v, Codecs o)
    {
        w.Put("Action", v.Action, o);
        w.Put("Requires", v.Requires, o);
        w.WriteBoolean("Coupled", v.Coupled);
    }
}
