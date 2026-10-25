using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal sealed class IdeoligionReformActionHandler : IActionHandler
    {
        internal static RimGovernor.Brain.GameFacts.Common.IdeoligionDesign ReadDesign(Ideo ideo)
        {
            var design = new RimGovernor.Brain.GameFacts.Common.IdeoligionDesign { Fluid = ideo.Fluid };
            design = design with { Memes = [.. design.Memes, .. ideo.memes.Select(m => m.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            design = design with { Precepts = [.. design.Precepts, .. ideo.PreceptsListForReading.Where(p => p.def.preceptClass == typeof(Precept)).Select(p => p.def.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            design = design with { VeneratedAnimals = [.. design.VeneratedAnimals, .. ideo.VeneratedAnimals.Select(t => t.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            design = design with { StyleCategories = [.. design.StyleCategories, .. ideo.thingStyleCategories.Where(c => c?.category != null).Select(c => c.category.defName).OrderBy(n => n, StringComparer.Ordinal)] };
            return design;
        }

        private static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
            a.Count == b.Count && a.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(b.OrderBy(n => n, StringComparer.Ordinal));

        private static bool Matches(RimGovernor.Brain.GameFacts.Common.IdeoligionDesign? a, RimGovernor.Brain.GameFacts.Common.IdeoligionDesign? b) => a != null && b != null && a.Fluid == b.Fluid &&
            SameNames(a.Memes, b.Memes) && SameNames(a.Precepts, b.Precepts) &&
            SameNames(a.VeneratedAnimals, b.VeneratedAnimals) && SameNames(a.StyleCategories, b.StyleCategories);

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.IdeoligionReformIntent? command, out Ideo? source, out Ideo? candidate)
        {
            source = ModsConfig.IdeologyActive ? Faction.OfPlayer.ideos?.PrimaryIdeo : null;
            candidate = null;
            if (source == null || command == null || command.IdeoId is null || (command.IdeoId ?? "") != source.GetUniqueLoadID() || command.ExpectedReformCount < 0 || command.ExpectedReformCount == int.MaxValue || command.Expected == null || command.Design == null || Matches(command.Expected, command.Design))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Invalid reform identity, count or design.");
            if (!source.Fluid || source.development == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "A fixed ideoligion cannot reform.");
            var actual = ReadDesign(source);
            if (source.development.reformCount == command.ExpectedReformCount + 1 && Matches(actual, command.Design)) return null;
            if (source.development.reformCount != command.ExpectedReformCount || !Matches(actual, command.Expected))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Stale ideoligion design or reform count.");
            return NativeIdeoligionDesign.Prepare(source, command.Design, true, out candidate);
        }

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(action.Intent?.IdeoligionReform, out _, out _);

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(action.Intent?.IdeoligionReform, out var source, out var candidate);
            if (failure != null) throw new ApplyRefusedException(failure);
            if (candidate != null) IdeoDevelopmentUtility.ApplyChangesToIdeo(source, candidate);
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.IdeoligionEffect {
                IdeoId = (source ?? throw new System.InvalidOperationException("Required native value is unavailable: source.")).GetUniqueLoadID(), Design = ReadDesign(source) } };
        }
    }
}
