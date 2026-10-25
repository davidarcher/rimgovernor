using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class IdeoligionReformActionHandler : IActionHandler
    {
        internal static Common.IdeoligionDesign ReadDesign(Ideo ideo)
        {
            var design = new Common.IdeoligionDesign { Fluid = ideo.Fluid };
            design = design with { Memes = [.. design.Memes, .. ideo.memes.Select(m => m.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            design = design with { Precepts = [.. design.Precepts, .. ideo.PreceptsListForReading.Where(p => p.def.preceptClass == typeof(Precept)).Select(p => p.def.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            design = design with { VeneratedAnimals = [.. design.VeneratedAnimals, .. ideo.VeneratedAnimals.Select(t => t.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            design = design with { StyleCategories = [.. design.StyleCategories, .. ideo.thingStyleCategories.Where(c => c?.category != null).Select(c => c.category.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            return design;
        }

        private static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
            a.Count == b.Count && a.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(b.OrderBy(n => n, StringComparer.Ordinal));

        private static bool Matches(Common.IdeoligionDesign? a, Common.IdeoligionDesign? b) => a != null && b != null && a.Fluid == b.Fluid &&
            SameNames(a.Memes, b.Memes) && SameNames(a.Precepts, b.Precepts) &&
            SameNames(a.VeneratedAnimals, b.VeneratedAnimals) && SameNames(a.StyleCategories, b.StyleCategories);

        private static Common.Failure? Resolve(Operations.IdeoligionReformIntent? command, out Ideo? source, out Ideo? candidate)
        {
            source = ModsConfig.IdeologyActive ? Faction.OfPlayer.ideos?.PrimaryIdeo : null;
            candidate = null;
            if (source == null || command == null || command.IdeoId is null || (command.IdeoId ?? "") != source.GetUniqueLoadID() || command.ExpectedReformCount < 0 || command.ExpectedReformCount == int.MaxValue || command.Expected == null || command.Design == null || Matches(command.Expected, command.Design))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Invalid reform identity, count or design.");
            if (!source.Fluid || source.development == null)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A fixed ideoligion cannot reform.");
            var actual = ReadDesign(source);
            if (source.development.reformCount == command.ExpectedReformCount + 1 && Matches(actual, command.Design)) return null;
            if (source.development.reformCount != command.ExpectedReformCount || !Matches(actual, command.Expected))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Stale ideoligion design or reform count.");
            return NativeIdeoligionDesign.Prepare(source, command.Design, true, out candidate);
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => Resolve(action.Intent?.IdeoligionReform, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var failure = Resolve(action.Intent?.IdeoligionReform, out var source, out var candidate);
            if (failure != null) throw new ApplyRefusedException(failure);
            if (candidate != null) IdeoDevelopmentUtility.ApplyChangesToIdeo(source, candidate);
            return new Receipts.EffectEvidence { Effect = new Receipts.IdeoligionEffect {
                IdeoId = (source ?? throw new System.InvalidOperationException("Required native value is unavailable: source.")).GetUniqueLoadID(), Design = ReadDesign(source) } };
        }
    }
}
