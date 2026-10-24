#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Bridge.Contracts;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The typed read behind PresentationReads/Notifications: the letter stack,
    /// the live transient messages and the active alerts as three independently
    /// available sections. Notifications are game-global, so any loaded map
    /// identity resolves; nothing here opens, dismisses or acknowledges.
    /// </summary>
    internal static class NativeNotificationReadTools
    {
        private static readonly FieldInfo? ActiveAlertsField = BridgeCommon.PrivateInstanceField(typeof(AlertsReadout), "activeAlerts");
        private static readonly FieldInfo? LiveMessagesField = BridgeCommon.PrivateStaticField(typeof(Messages), "liveMessages");

        internal static Presentation.NotificationsSnapshot HandleNotifications(Presentation.NotificationsRequest request)
        {
            if (!Validate(request, out var failure0)) throw NativeFaults.Refused(failure0);
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var context, out var error)) throw NativeFaults.Refused(error);
            try
            {
                var snapshot = new Presentation.NotificationsSnapshot { Context = context };
                if (request.IncludeLetters)
                    snapshot = snapshot with { Letters = Section(() => ReadLetters(Limit(request.LetterLimit)),
                        (Presentation.Letters v) => new Presentation.LetterSection { Outcome = v }, u => new Presentation.LetterSection { Outcome = u }) };
                if (request.IncludeMessages)
                    snapshot = snapshot with { Messages = Section(() => ReadMessages(Limit(request.MessageLimit)),
                        (Presentation.Messages v) => new Presentation.MessageSection { Outcome = v }, u => new Presentation.MessageSection { Outcome = u }) };
                if (request.IncludeAlerts)
                    snapshot = snapshot with { Alerts = Section(() => ReadAlerts(Limit(request.AlertLimit)),
                        (Presentation.Alerts v) => new Presentation.AlertSection { Outcome = v }, u => new Presentation.AlertSection { Outcome = u }) };
                return snapshot;
            }
            catch (Exception)
            {
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.Unavailable,
                    "Native notifications could not be read completely."));
            }
        }

        internal static bool Validate(Presentation.NotificationsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required.");
            if (request?.Identity == null) return false;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Notification limits must be at least 1.");
            if (request.LetterLimit < 1 || request.MessageLimit < 1 || request.AlertLimit < 1) return false;
            failure = null!;
            return true;
        }

        private static int Limit(uint value) => (int)Math.Min(value, (uint)int.MaxValue);

        // A section whose native read throws is unavailable on its own; the
        // other sections still answer.
        private static TSection Section<TValue, TSection>(Func<TValue> read, Func<TValue, TSection> observed, Func<Common.Unavailable, TSection> unavailable)
        {
            try { return observed(read()); }
            catch (Exception error) when (error is MissingMemberException || error is NativeComponentMissing)
            { return unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing, Detail = Diagnostic(error.Message) }); }
            catch (Exception)
            { return unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "The native notification section threw while being read." }); }
        }

        // ============================================================== letters

        private static Presentation.Letters ReadLetters(int limit)
        {
            var stack = Find.LetterStack ?? throw new NativeComponentMissing("Find.LetterStack is unavailable.");
            var letters = (stack.LettersListForReading ?? new List<Verse.Letter>()).Where(l => l != null).ToList();
            letters.Reverse(); // newest first, the order the game draws the stack
            var result = new Presentation.Letters
            {
                Listing = Listing(letters.Count, Math.Min(letters.Count, limit)),
                Items = [.. letters.Take(limit).Select(letter => ReadLetter(letter))],
            };
            RequireUnique(result.Items.Select(l => l.Id), "letter");
            return result;
        }

        private static Presentation.Letter ReadLetter(Verse.Letter letter) => new Presentation.Letter
        {
            Id = Id(letter.GetUniqueLoadID()),
            NativeType = Id(letter.GetType().FullName!),
            DefName = Id((letter.def ?? throw new InvalidOperationException("Native letter has no def.")).defName),
            Label = Diagnostic(letter.Label.ToString()),
            ArrivalTick = letter.arrivalTick,
        };

        // ============================================================= messages

        private static Presentation.Messages ReadMessages(int limit)
        {
            var field = LiveMessagesField ?? throw new NativeComponentMissing("Verse.Messages.liveMessages was not found on this build.");
            var live = (field.GetValue(null) as List<Verse.Message> ?? throw new NativeComponentMissing("Verse.Messages.liveMessages is not a message list.")).Where(m => m != null).ToList();
            return new Presentation.Messages { Listing = Listing(live.Count, Math.Min(live.Count, limit)) };
        }

        // =============================================================== alerts

        // AlertsReadout.activeAlerts is the list the game's own readout keeps
        // current; nothing recalculates activity.
        private static Presentation.Alerts ReadAlerts(int limit)
        {
            var field = ActiveAlertsField ?? throw new NativeComponentMissing("RimWorld.AlertsReadout.activeAlerts was not found on this build.");
            var readout = Find.Alerts ?? throw new NativeComponentMissing("Find.Alerts is unavailable.");
            var active = field.GetValue(readout) as List<RimWorld.Alert> ?? throw new NativeComponentMissing("AlertsReadout.activeAlerts is not an alert list.");
            var ordered = active.Select((alert, ordinal) => (alert, ordinal)).Where(a => a.alert != null)
                .OrderByDescending(a => (int)a.alert.Priority).ThenBy(a => a.ordinal).Select(a => a.alert).ToList();
            return new Presentation.Alerts { Listing = Listing(ordered.Count, Math.Min(ordered.Count, limit)),
                SnapshotFingerprint = Fingerprint(ordered.Select(a => a.GetType().FullName!)) };
        }

        private static string Fingerprint(IEnumerable<string> ids)
        {
            var hash = 1469598103934665603UL;
            foreach (var value in ids) { hash ^= (ulong)value.Length; foreach (var c in value) hash = (hash ^ c) * 1099511628211UL; hash = (hash ^ '\n') * 1099511628211UL; }
            return hash.ToString("x16");
        }

        // ============================================================== helpers

        private static Presentation.Listing Listing(int total, int returned) => new Presentation.Listing { TotalCount = checked((uint)total),
            ReturnedCount = checked((uint)returned), Complete = returned == total };
        private static void RequireUnique(IEnumerable<string> ids, string kind)
        {
            var list = ids.ToList();
            if (list.Distinct(StringComparer.Ordinal).Count() != list.Count) throw new InvalidOperationException("Native " + kind + " IDs are not unique.");
        }
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value) => PlacementPreviewOperation.Diagnostic(value);
        private sealed class NativeComponentMissing : Exception { internal NativeComponentMissing(string message) : base(message) {} }
    }
}
