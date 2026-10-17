#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        private const int DefaultLetterLimit = 40;
        private const int DefaultMessageLimit = 12;
        private const int DefaultAlertLimit = 40;

        private static readonly FieldInfo? ActiveAlertsField = BridgeCommon.PrivateInstanceField(typeof(AlertsReadout), "activeAlerts");
        private static readonly FieldInfo? LiveMessagesField = BridgeCommon.PrivateStaticField(typeof(Messages), "liveMessages");

        internal static Presentation.NotificationsReply HandleNotifications(Presentation.NotificationsRequest request)
        {
            if (!Validate(request, out var failure0)) return new Presentation.NotificationsReply { Outcome = new Presentation.NotificationsReplyOutcome.FailureCase(failure0) };
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var context, out var error))
                return new Presentation.NotificationsReply { Outcome = new Presentation.NotificationsReplyOutcome.FailureCase(error) };
            try
            {
                var snapshot = new Presentation.NotificationsSnapshot { Context = context };
                if (request.IncludeLetters ?? true)
                    snapshot = snapshot with { Letters = Section(() => ReadLetters(Limit(request.LetterLimit, DefaultLetterLimit)),
                        (Presentation.Letters v) => new Presentation.LetterSection { Outcome = v }, u => new Presentation.LetterSection { Outcome = u }) };
                if (request.IncludeMessages ?? true)
                    snapshot = snapshot with { Messages = Section(() => ReadMessages(Limit(request.MessageLimit, DefaultMessageLimit)),
                        (Presentation.Messages v) => new Presentation.MessageSection { Outcome = v }, u => new Presentation.MessageSection { Outcome = u }) };
                if (request.IncludeAlerts ?? true)
                    snapshot = snapshot with { Alerts = Section(() => ReadAlerts(Limit(request.AlertLimit, DefaultAlertLimit)),
                        (Presentation.Alerts v) => new Presentation.AlertSection { Outcome = v }, u => new Presentation.AlertSection { Outcome = u }) };
                return new Presentation.NotificationsReply { Outcome = snapshot };
            }
            catch (Exception)
            {
                return new Presentation.NotificationsReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.Unavailable,
                    "Native notifications could not be read completely.") };
            }
        }

        internal static bool Validate(Presentation.NotificationsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required.");
            if (request?.Identity == null) return false;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Notification limits must be at least 1.");
            foreach (var limit in new[] { (request.LetterLimit is not null, request.LetterLimit.GetValueOrDefault()), (request.MessageLimit is not null, request.MessageLimit.GetValueOrDefault()), (request.AlertLimit is not null, request.AlertLimit.GetValueOrDefault()) })
                if (limit.Item1 && (limit.Item2 < 1)) return false;
            failure = null!;
            return true;
        }

        private static int Limit(uint? value, int fallback) => value is { } v ? (int)Math.Min(v, (uint)int.MaxValue) : fallback;

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
            var result = new Presentation.Letters { Listing = Listing(letters.Count, Math.Min(letters.Count, limit)) };
            result = result with { Items = [.. letters.Take(limit).Select(letter => ReadLetter(letter))] };
            RequireUnique(result.Items.Select(l => l.Id ?? ""), "letter");
            return result;
        }

        private static Presentation.Letter ReadLetter(Verse.Letter letter)
        {
            var row = new Presentation.Letter { Id = Id(letter.GetUniqueLoadID()), NativeType = Id(letter.GetType().FullName!),
                Label = Diagnostic(letter.Label.ToString()), ArrivalTick = letter.arrivalTick };
            if (letter.def != null && ProtoBoundary.IsIdentifier(letter.def.defName)) row = row with { DefName = letter.def.defName };
            if (letter is ChoiceLetter choiceLetter) row = row with { Text = Diagnostic(choiceLetter.Text.ToString()) };
            return row;
        }

        // ============================================================= messages

        private static Presentation.Messages ReadMessages(int limit)
        {
            var field = LiveMessagesField ?? throw new NativeComponentMissing("Verse.Messages.liveMessages was not found on this build.");
            var live = (field.GetValue(null) as List<Verse.Message> ?? throw new NativeComponentMissing("Verse.Messages.liveMessages is not a message list.")).Where(m => m != null).ToList();
            live.Reverse(); // newest first
            var result = new Presentation.Messages { Listing = Listing(live.Count, Math.Min(live.Count, limit)) };
            result = result with { Items = [.. live.Take(limit).Select(message =>
                new Presentation.TransientMessage { Id = Id(message.GetUniqueLoadID()), Text = Diagnostic(message.text ?? string.Empty) })] };
            RequireUnique(result.Items.Select(m => m.Id ?? ""), "message");
            return result;
        }

        // =============================================================== alerts

        // AlertsReadout.activeAlerts is the list the game's own readout keeps
        // current; nothing recalculates activity. GetReport() runs once per
        // active alert for the culprit targets, GetExplanation() for the prose.
        private static Presentation.Alerts ReadAlerts(int limit)
        {
            var field = ActiveAlertsField ?? throw new NativeComponentMissing("RimWorld.AlertsReadout.activeAlerts was not found on this build.");
            var readout = Find.Alerts ?? throw new NativeComponentMissing("Find.Alerts is unavailable.");
            var active = field.GetValue(readout) as List<RimWorld.Alert> ?? throw new NativeComponentMissing("AlertsReadout.activeAlerts is not an alert list.");
            var ordered = active.Select((alert, ordinal) => (alert, ordinal)).Where(a => a.alert != null)
                .OrderByDescending(a => (int)a.alert.Priority).ThenBy(a => a.ordinal).Select(a => a.alert).ToList();
            var result = new Presentation.Alerts { Listing = Listing(ordered.Count, Math.Min(ordered.Count, limit)),
                SnapshotFingerprint = Fingerprint(ordered.Select(a => a.GetType().FullName!)) };
            var alertRows = new List<Presentation.Alert>();
            foreach (var alert in ordered.Take(limit))
            {
                // Alerts are singletons per type in the readout; the type is the identity.
                var row = new Presentation.Alert { Id = Id(alert.GetType().FullName!) };
                try { row = row with { Label = Diagnostic(alert.Label ?? string.Empty) }; }
                catch (Exception) { /* a label that throws leaves the alert unlabelled */ }
                try
                {
                    var report = alert.GetReport();
                    var culprits = (report.AllCulprits ?? Enumerable.Empty<GlobalTargetInfo>()).ToList();
                    row = row with { Listing = Listing(culprits.Count, culprits.Count) };
                    foreach (var culprit in culprits) row = row with { Targets = [.. row.Targets, Target(culprit)] };
                }
                catch (Exception)
                {
                    // An alert that cannot name its culprits is still an alert; a
                    // map-wide alert has a complete empty target list instead.
                    row = row with { Targets = default, Listing = null };
                }
                alertRows.Add(row);
            }
            result = result with { Items = [.. alertRows] };
            RequireUnique(result.Items.Select(a => a.Id ?? ""), "alert");
            return result;
        }

        private static string Fingerprint(IEnumerable<string> ids)
        {
            var hash = 1469598103934665603UL;
            foreach (var value in ids) { hash ^= (ulong)value.Length; foreach (var c in value) hash = (hash ^ c) * 1099511628211UL; hash = (hash ^ '\n') * 1099511628211UL; }
            return hash.ToString("x16");
        }

        // ============================================================== targets

        private static Presentation.LookTarget Target(GlobalTargetInfo target)
        {
            var row = new Presentation.LookTarget();
            if (target.HasThing && target.Thing != null)
            {
                var thing = target.Thing;
                row = row with { Id = Id(thing.GetUniqueLoadID()) };
                row = row with { Label = Diagnostic(thing is Pawn pawn ? pawn.Name?.ToStringShort ?? pawn.LabelShort : thing.LabelCap) };
                if (thing.Map != null) { row = row with { MapId = thing.Map.uniqueID }; if (thing.Spawned && thing.Position.InBounds(thing.Map)) row = row with { Position = Cell(thing.Position) }; }
                return row;
            }
            if (target.HasWorldObject && target.WorldObject != null)
            {
                var worldObject = target.WorldObject;
                row = row with { Id = Id(worldObject.GetUniqueLoadID()) };
                row = row with { Label = Diagnostic(target.Label ?? string.Empty) };
                return row;
            }
            if (target.IsMapTarget && target.Map != null && target.Cell.IsValid && target.Cell.InBounds(target.Map))
            {
                row = row with { MapId = target.Map.uniqueID }; row = row with { Position = Cell(target.Cell) };
                return row;
            }
            row = row with { Label = Diagnostic(target.Label ?? string.Empty) };
            return row;
        }

        // ============================================================== helpers

        private static Presentation.Listing Listing(int total, int returned) => new Presentation.Listing { TotalCount = checked((uint)total),
            ReturnedCount = checked((uint)returned), Complete = returned == total, Truncated = returned < total };
        private static void RequireUnique(IEnumerable<string> ids, string kind)
        {
            var list = ids.ToList();
            if (list.Distinct(StringComparer.Ordinal).Count() != list.Count) throw new InvalidOperationException("Native " + kind + " IDs are not unique.");
        }
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static string Diagnostic(string value) => PlacementPreviewOperation.Diagnostic(value);
        private sealed class NativeComponentMissing : Exception { internal NativeComponentMissing(string message) : base(message) {} }
    }
}
