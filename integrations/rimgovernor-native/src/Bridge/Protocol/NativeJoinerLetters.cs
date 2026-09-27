#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Only the Core walk-in offer is supported: its signal admits exactly one
    // pawn immediately. Other AcceptJoiner quest families remain player choices.
    internal static class NativeJoinerLetters
    {
        private static Pawn? Joiner(ChoiceLetter_AcceptJoiner letter) => letter.quest?.PartsListForReading
            .OfType<QuestPart_PawnsArrive>().SelectMany(p => p.pawns).Distinct().SingleOrDefault();

        private static IEnumerable<ChoiceLetter_AcceptJoiner> Pending() => Find.LetterStack.LettersListForReading
            .OfType<ChoiceLetter_AcceptJoiner>().Where(l => l.quest?.root?.defName == "WandererJoins"
                && l.CanShowInLetterStack && !l.TimeoutPassed && l.MapToUse == Find.CurrentMap);

        private static string Token(ChoiceLetter_AcceptJoiner letter, Pawn pawn)
        {
            var text = string.Join("|", letter.GetUniqueLoadID(), letter.quest.GetUniqueLoadID(), letter.quest.State,
                pawn.GetUniqueLoadID(), pawn.Dead, pawn.Faction?.GetUniqueLoadID(), letter.MapToUse?.uniqueID,
                letter.disappearAtTick, letter.signalAccept, letter.signalReject,
                string.Join("|", letter.Choices.Take(2).Select(o => ChoiceDialogTools.Label(o) + ":" + o.disabled)));
            using (var hash = SHA256.Create())
                return "joiner-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        internal static List<Obs.JoinerLetter> Snapshot()
        {
            var rows = new List<Obs.JoinerLetter>();
            foreach (var letter in Pending().OrderBy(l => l.ID))
            {
                var pawn = Joiner(letter);
                if (pawn == null || pawn.Dead) continue;
                var accept = letter.Choices.First();
                rows.Add(new Obs.JoinerLetter { LetterId = letter.ID, SnapshotToken = Token(letter, pawn),
                    PawnId = pawn.GetUniqueLoadID(), ExpiresTick = letter.disappearAtTick,
                    AcceptLabel = ChoiceDialogTools.Label(accept), CanAccept = !accept.disabled && accept.action != null });
            }
            return rows;
        }

        // Resolves the pending letter the intent names and its accept option.
        private static Common.Failure? Resolve(Operations.DialogIntent c, out ChoiceLetter_AcceptJoiner letter, out Pawn pawn, out DiaOption option)
        {
            letter = null!; pawn = null!; option = null!;
            if (!c.HasWindowId || !c.HasOptionIndex || c.OptionIndex != 0 || !c.HasOptionLabel || string.IsNullOrEmpty(c.JoinerLetterToken))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A joiner answer needs the letter, option 0, its label and the letter token.");
            var found = Pending().SingleOrDefault(l => l.ID == c.WindowId);
            var joiner = found == null ? null : Joiner(found);
            if (found == null || joiner == null || joiner.Dead || joiner.Faction == Faction.OfPlayer || Token(found, joiner) != c.JoinerLetterToken)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Joiner letter changed or expired; inspect again.");
            var accept = found.Choices.First();
            if (accept.disabled || accept.action == null || ChoiceDialogTools.Label(accept) != c.OptionLabel)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The joiner letter's accept option changed or is disabled.");
            letter = found; pawn = joiner; option = accept;
            return null;
        }

        // Accepted is the letter the intent names already archived with its
        // joiner a living spawned free colonist on the offered map: a resent
        // intent applies again as it stands.
        private static bool Accepted(Operations.DialogIntent c, out ChoiceLetter_AcceptJoiner letter, out Pawn pawn)
        {
            letter = Find.Archive.ArchivablesListForReading.OfType<ChoiceLetter_AcceptJoiner>().FirstOrDefault(l => l.ID == c.WindowId)!;
            pawn = letter == null ? null! : Joiner(letter)!;
            return letter != null && pawn != null && letter.ArchivedOnly && Joined(letter, pawn);
        }

        private static bool Joined(ChoiceLetter_AcceptJoiner letter, Pawn pawn) =>
            !pawn.Dead && pawn.Spawned && pawn.Map == letter.MapToUse && pawn.IsFreeColonist;

        internal static Common.Failure? Validate(Operations.DialogIntent c) => Accepted(c, out _, out _) ? null : Resolve(c, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.DialogIntent c)
        {
            if (!Accepted(c, out var letter, out var pawn))
            {
                var failure = Resolve(c, out letter, out pawn, out var option);
                if (failure != null) throw new InvalidOperationException("Joiner letter changed before apply: " + failure.Detail);
                // Letter choices have no owning Dialog_NodeTree until opened.
                // Run the native button action, as the headless fixture does;
                // DiaOption.Activate would dereference that absent window.
                option.action();
                if (!letter.ArchivedOnly || !Joined(letter, pawn)) throw new InvalidOperationException("Joiner did not arrive.");
            }
            return new Receipts.EffectEvidence { Dialog = new Receipts.DialogEffect { WindowId = c.WindowId, OptionIndex = 0,
                OptionLabel = c.OptionLabel, JoinerLetterToken = c.JoinerLetterToken, JoinerPawnId = pawn.GetUniqueLoadID(),
                Activated = true, Closed = true, Advanced = false, Joined = true } };
        }
    }
}
