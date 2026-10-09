#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class IdeoligionReformActionHandler : IActionHandler
    {
        internal static Common.IdeoligionDesign ReadDesign(Ideo ideo)
        {
            var design = new Common.IdeoligionDesign { Fluid = ideo.Fluid };
            design.Memes.Add(ideo.memes.Select(m => m.defName).OrderBy(n => n, StringComparer.Ordinal));
            design.Precepts.Add(ideo.PreceptsListForReading.Where(p => p.def.preceptClass == typeof(Precept)).Select(p => p.def.defName).OrderBy(n => n, StringComparer.Ordinal));
            return design;
        }

        private static bool Matches(Common.IdeoligionDesign? a, Common.IdeoligionDesign? b) => a != null && b != null && a.HasFluid && b.HasFluid && a.Fluid == b.Fluid &&
            a.Memes.Count == b.Memes.Count && a.Precepts.Count == b.Precepts.Count &&
            a.Memes.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(b.Memes.OrderBy(n => n, StringComparer.Ordinal)) &&
            a.Precepts.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(b.Precepts.OrderBy(n => n, StringComparer.Ordinal));

        private static Common.Failure? Resolve(Operations.IdeoligionReformIntent command, out Ideo? source, out Ideo? candidate)
        {
            source = ModsConfig.IdeologyActive ? Faction.OfPlayer.ideos?.PrimaryIdeo : null;
            candidate = null;
            if (source == null || command == null || !command.HasIdeoId || command.IdeoId != source.GetUniqueLoadID() || !command.HasExpectedReformCount || command.ExpectedReformCount < 0 || command.ExpectedReformCount == int.MaxValue || command.Expected == null || command.Design == null || Matches(command.Expected, command.Design))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Invalid reform identity, count or design.");
            if (!source.Fluid || source.development == null)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A fixed ideoligion cannot reform.");
            var actual = ReadDesign(source);
            if (source.development.reformCount == command.ExpectedReformCount + 1 && Matches(actual, command.Design)) return null;
            if (source.development.reformCount != command.ExpectedReformCount || !Matches(actual, command.Expected))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stale ideoligion design or reform count.");
            return NativeIdeoligionDesign.Prepare(source, command.Design, true, out candidate);
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.IdeoligionReform, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.IdeoligionReform, out var source, out var candidate);
            if (failure != null) throw new ApplyRefusedException(failure);
            if (candidate != null) IdeoDevelopmentUtility.ApplyChangesToIdeo(source!, candidate);
            return new Receipts.EffectEvidence { Ideoligion = new Receipts.IdeoligionEffect {
                IdeoId = source!.GetUniqueLoadID(), Design = ReadDesign(source), ReformCount = source.development.reformCount, DevelopmentPoints = source.development.Points } };
        }
    }
}
