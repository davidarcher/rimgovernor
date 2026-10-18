#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // One generic stat query: the game's own evaluation of a StatDef for a
    // definition (with optional stuff and quality), a thing or a pawn. The
    // definition path is GetStatValueAbstract and ShouldShowFor, so it is the
    // live oracle for the stat evaluator. Read-only; an unknown stat or subject is a typed failure.
    internal static class NativeStatTool
    {

        internal static Obs.EvaluateStatReply HandleEvaluateStat(Obs.EvaluateStatRequest request)
        {
            if (request.Scope?.ExpectedIdentity == null || string.IsNullOrEmpty(request.Stat) || request.Subject?.Subject == null)
                return new Obs.EvaluateStatReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, stat and subject are required.") };
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.EvaluateStatReply { Outcome = new Obs.EvaluateStatReplyOutcome.FailureCase(failure) };
            try { return Evaluate(map, context, request); }
            catch (Exception ex)
            {
                ObservationWork.Failed("evaluateStat", ex);
                return new Obs.EvaluateStatReply { Outcome = new Common.Unavailable
                    { Reason = Common.UnavailableReason.ReadFailed, Detail = $"Stat {request.Stat} could not be evaluated: {ex.Message}" } };
            }
        }

        private static Obs.EvaluateStatReply Refuse(Common.FailureCode code, string detail) =>
            new Obs.EvaluateStatReply { Outcome = ProtoBoundary.Fail(code, detail) };

        private static Obs.EvaluateStatReply Evaluate(Map map, Common.ObservationContext context, Obs.EvaluateStatRequest request)
        {
            var stat = DefDatabase<StatDef>.GetNamedSilentFail(request.Stat);
            if (stat == null) return Refuse(Common.FailureCode.NotFound, $"Unknown stat {request.Stat}.");
            StatRequest req;
            float? exact = null;
            var subject = request.Subject?.Subject;
            switch (subject)
            {
                case Obs.StatSubjectSubject.DefinitionCase { Value: var row }:
                {
                    BuildableDef? def = (BuildableDef?)DefDatabase<ThingDef>.GetNamedSilentFail(row.DefName) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(row.DefName);
                    if (def == null) return Refuse(Common.FailureCode.NotFound, $"Unknown definition {row.DefName}.");
                    ThingDef? stuff = null;
                    if (row.Stuff is not null)
                    {
                        stuff = DefDatabase<ThingDef>.GetNamedSilentFail(row.Stuff ?? "");
                        if (stuff == null) return Refuse(Common.FailureCode.NotFound, $"Unknown stuff {row.Stuff}.");
                    }
                    if (row.Quality is { } rawQuality && (rawQuality < 0 || rawQuality > (int)QualityCategory.Legendary))
                        return Refuse(Common.FailureCode.InvalidRequest, $"Quality {row.Quality} is not a quality category.");
                    var quality = row.Quality is { } chosenQuality ? (QualityCategory)chosenQuality : QualityCategory.Normal;
                    req = StatRequest.For(def, stuff, quality);
                    // The definition catalog's stat table value, exactly.
                    if (row.Quality is null) exact = def.GetStatValueAbstract(stat, stuff);
                    break;
                }
                case Obs.StatSubjectSubject.ThingIdCase { Value: var thingId }:
                {
                    var thing = map.listerThings.AllThings.ById(thingId);
                    if (thing == null) return Refuse(Common.FailureCode.NotFound, $"Unknown thing {thingId}.");
                    req = StatRequest.For(thing);
                    break;
                }
                default:
                {
                    var pawn = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).ById(subject is Obs.StatSubjectSubject.PawnIdCase { Value: var pawnId } ? pawnId : null);
                    if (pawn == null) return Refuse(Common.FailureCode.NotFound, $"Unknown pawn {(subject as Obs.StatSubjectSubject.PawnIdCase)?.Value}.");
                    req = StatRequest.For(pawn);
                    break;
                }
            }
            var value = exact ?? stat.Worker.GetValue(req);
            if (float.IsNaN(value) || float.IsInfinity(value))
                return new Obs.EvaluateStatReply { Outcome = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = $"Stat {stat.defName} is {value}." } };
            var observed = new Obs.StatEvaluation { Stat = stat.defName, Value = value, Shown = stat.Worker.ShouldShowFor(req) };
            return new Obs.EvaluateStatReply { Outcome = observed };
        }
    }
}
