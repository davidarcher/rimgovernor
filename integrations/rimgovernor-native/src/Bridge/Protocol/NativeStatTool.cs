#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // One generic stat query: the game's own evaluation of a StatDef for a
    // definition (with optional stuff and quality), a thing or a pawn. The
    // definition path is GetStatValueAbstract and ShouldShowFor, so it is the
    // live oracle for the Go stat evaluator. Read-only; an unknown stat or subject is a typed failure.
    public sealed class NativeStatTool
    {
        internal const string ToolName = "rimgovernor/observations_evaluate_stat";

        [Tool(ToolName, Title = "Evaluate a stat", Description = "Official EvaluateStatRequest ProtoJSON. Read-only: the game's final value, per-part explanation lines and ShouldShowFor for one stat of a definition (optional stuff and quality), a thing or a pawn. An unknown stat or subject is a NOT_FOUND failure.")]
        [ToolResponse("payload", "string", "Official EvaluateStatReply ProtoJSON.", Always = true)]
        public async Task<object> EvaluateStat(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be an EvaluateStatRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.EvaluateStatRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.EvaluateStatReply { Failure = failure });
            if (parsed.Scope?.ExpectedIdentity == null || string.IsNullOrEmpty(parsed.Stat) || parsed.Subject == null || parsed.Subject.SubjectCase == Obs.StatSubject.SubjectOneofCase.None)
                return ProtoBoundary.Encode(new Obs.EvaluateStatReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, stat and subject are required.") });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.EvaluateStatReply { Failure = failure });
                try { return ProtoBoundary.Encode(Evaluate(map, context, parsed)); }
                catch (Exception ex)
                {
                    ObservationWork.Failed("evaluateStat", ex);
                    return ProtoBoundary.Encode(new Obs.EvaluateStatReply { Unavailable = new Common.Unavailable
                        { Reason = Common.UnavailableReason.ReadFailed, Detail = $"Stat {parsed.Stat} could not be evaluated: {ex.Message}" } });
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Obs.EvaluateStatReply Refuse(Common.FailureCode code, string detail) =>
            new Obs.EvaluateStatReply { Failure = ProtoBoundary.Fail(code, detail) };

        private static Obs.EvaluateStatReply Evaluate(Map map, Common.ObservationContext context, Obs.EvaluateStatRequest request)
        {
            var stat = DefDatabase<StatDef>.GetNamedSilentFail(request.Stat);
            if (stat == null) return Refuse(Common.FailureCode.NotFound, $"Unknown stat {request.Stat}.");
            StatRequest req;
            float? exact = null;
            var subject = request.Subject;
            switch (subject.SubjectCase)
            {
                case Obs.StatSubject.SubjectOneofCase.Definition:
                {
                    var row = subject.Definition;
                    BuildableDef? def = (BuildableDef?)DefDatabase<ThingDef>.GetNamedSilentFail(row.DefName) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(row.DefName);
                    if (def == null) return Refuse(Common.FailureCode.NotFound, $"Unknown definition {row.DefName}.");
                    ThingDef? stuff = null;
                    if (row.HasStuff)
                    {
                        stuff = DefDatabase<ThingDef>.GetNamedSilentFail(row.Stuff);
                        if (stuff == null) return Refuse(Common.FailureCode.NotFound, $"Unknown stuff {row.Stuff}.");
                    }
                    if (row.HasQuality && (row.Quality < 0 || row.Quality > (int)QualityCategory.Legendary))
                        return Refuse(Common.FailureCode.InvalidRequest, $"Quality {row.Quality} is not a quality category.");
                    var quality = row.HasQuality ? (QualityCategory)row.Quality : QualityCategory.Normal;
                    req = StatRequest.For(def, stuff, quality);
                    // The definition catalog's stat table value, exactly.
                    if (!row.HasQuality) exact = def.GetStatValueAbstract(stat, stuff);
                    break;
                }
                case Obs.StatSubject.SubjectOneofCase.ThingId:
                {
                    var thing = map.listerThings.AllThings.ById(subject.ThingId);
                    if (thing == null) return Refuse(Common.FailureCode.NotFound, $"Unknown thing {subject.ThingId}.");
                    req = StatRequest.For(thing);
                    break;
                }
                default:
                {
                    var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).ById(subject.PawnId);
                    if (pawn == null) return Refuse(Common.FailureCode.NotFound, $"Unknown pawn {subject.PawnId}.");
                    req = StatRequest.For(pawn);
                    break;
                }
            }
            var value = exact ?? stat.Worker.GetValue(req);
            if (float.IsNaN(value) || float.IsInfinity(value))
                return new Obs.EvaluateStatReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = $"Stat {stat.defName} is {value}." } };
            var observed = new Obs.StatEvaluation { Context = context, Stat = stat.defName, Value = value, Shown = stat.Worker.ShouldShowFor(req) };
            var text = stat.Worker.GetExplanationUnfinalized(req, stat.toStringNumberSense) ?? "";
            observed.ExplanationLines.AddRange(text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));
            return new Obs.EvaluateStatReply { Observed = observed };
        }
    }
}
