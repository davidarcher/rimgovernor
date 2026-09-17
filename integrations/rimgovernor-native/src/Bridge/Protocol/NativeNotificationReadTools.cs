#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Presentation = RimGovernor.Protocol.Presentation;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The typed read behind PresentationReads/Notifications: the letter stack,
    /// the live transient messages and the active alerts as three independently
    /// available sections. Notifications are game-global, so any loaded map
    /// identity resolves; nothing here opens, dismisses or acknowledges.
    /// </summary>
    public sealed class NativeNotificationReadTools
    {
        private const int DefaultLetterLimit = 40;
        private const int DefaultMessageLimit = 12;
        private const int DefaultAlertLimit = 40;
        private const int LimitCeiling = 256;
        private const int MaxChoices = 8;
        private const int MaxTargets = 12;

        private static readonly FieldInfo? ActiveAlertsField = BridgeCommon.PrivateInstanceField(typeof(AlertsReadout), "activeAlerts");
        private static readonly FieldInfo? LiveMessagesField = BridgeCommon.PrivateStaticField(typeof(Messages), "liveMessages");
        private static readonly FieldInfo? MessageStartingTimeField = BridgeCommon.PrivateInstanceField(typeof(Message), "startingTime");
        private static readonly FieldInfo? DiaOptionTextField = BridgeCommon.PrivateInstanceField(typeof(DiaOption), "text");

        [Tool("rimgovernor/presentation_notifications", Title = "Read native notifications", Description = "Letter stack, live transient messages and active alerts as independently available sections, newest/loudest first with exact counts. Read-only: no open, dismiss, choice or acknowledgement.")]
        [ToolResponse("payload", "string", "Official ProtoJSON NotificationsReply; usable in headless and graphical games.", Always = true)]
        public async Task<object> Notifications(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON NotificationsRequest string in raw transport value.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/presentation_notifications", request, Presentation.NotificationsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.NotificationsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.NotificationsReply { Failure = error });
                try
                {
                    var snapshot = new Presentation.NotificationsSnapshot { Context = context };
                    var tick = Find.TickManager.TicksGame;
                    if (!parsed.HasIncludeLetters || parsed.IncludeLetters)
                        snapshot.Letters = Section(() => ReadLetters(Limit(parsed.HasLetterLimit, parsed.LetterLimit, DefaultLetterLimit), tick),
                            (Presentation.Letters v) => new Presentation.LetterSection { Observed = v }, u => new Presentation.LetterSection { Unavailable = u });
                    if (!parsed.HasIncludeMessages || parsed.IncludeMessages)
                        snapshot.Messages = Section(() => ReadMessages(Limit(parsed.HasMessageLimit, parsed.MessageLimit, DefaultMessageLimit), tick),
                            (Presentation.Messages v) => new Presentation.MessageSection { Observed = v }, u => new Presentation.MessageSection { Unavailable = u });
                    if (!parsed.HasIncludeAlerts || parsed.IncludeAlerts)
                        snapshot.Alerts = Section(() => ReadAlerts(Limit(parsed.HasAlertLimit, parsed.AlertLimit, DefaultAlertLimit)),
                            (Presentation.Alerts v) => new Presentation.AlertSection { Observed = v }, u => new Presentation.AlertSection { Unavailable = u });
                    return NativePresentationReadTools.Encode(new Presentation.NotificationsReply { Notifications = snapshot });
                }
                catch (Exception errorRead)
                {
                    return ProtoBoundary.Encode(new Presentation.NotificationsReply { Failure = ProtoBoundary.Fail(
                        errorRead is NativePresentationReadTools.ReadLimit ? Common.FailureCode.CapacityExhausted : Common.FailureCode.Unavailable,
                        errorRead is NativePresentationReadTools.ReadLimit ? errorRead.Message : "Native notifications could not be read completely.") });
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Presentation.NotificationsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Exact identity is required.");
            if (request?.Identity == null) return false;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Notification limits must be within 1..256.");
            foreach (var limit in new[] { (request.HasLetterLimit, request.LetterLimit), (request.HasMessageLimit, request.MessageLimit), (request.HasAlertLimit, request.AlertLimit) })
                if (limit.Item1 && (limit.Item2 < 1 || limit.Item2 > LimitCeiling)) return false;
            failure = null!;
            return true;
        }

        private static int Limit(bool present, uint value, int fallback) => present ? checked((int)value) : fallback;

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

        private static Presentation.Letters ReadLetters(int limit, int tick)
        {
            var stack = Find.LetterStack ?? throw new NativeComponentMissing("Find.LetterStack is unavailable.");
            var letters = (stack.LettersListForReading ?? new List<Verse.Letter>()).Where(l => l != null).ToList();
            letters.Reverse(); // newest first, the order the game draws the stack
            var result = new Presentation.Letters { Listing = Listing(letters.Count, Math.Min(letters.Count, limit)) };
            foreach (var letter in letters.Take(limit)) result.Letters_.Add(ReadLetter(letter, tick));
            RequireUnique(result.Letters_.Select(l => l.Id), "letter");
            return result;
        }

        private static Presentation.Letter ReadLetter(Verse.Letter letter, int tick)
        {
            var row = new Presentation.Letter { Id = Id(letter.GetUniqueLoadID()), NativeType = Id(letter.GetType().FullName!),
                Label = Diagnostic(letter.Label.ToString()), ArrivalTick = letter.arrivalTick, AgeTicks = Math.Max(0, tick - letter.arrivalTick),
                Dismissible = letter.CanDismissWithRightClick, AutomaticallyOpens = letter.ShouldAutomaticallyOpenLetter };
            if (letter.def != null && ProtoBoundary.IsIdentifier(letter.def.defName)) row.DefName = letter.def.defName;
            if (letter.relatedFaction != null) row.RelatedFactionLabel = Diagnostic(letter.relatedFaction.Name);
            var targets = ReadLookTargets(letter.lookTargets);
            if (targets != null) row.LookTargets = targets;
            if (letter is ChoiceLetter choiceLetter)
            {
                row.Text = Diagnostic(choiceLetter.Text.ToString());
                // Verse.StandardLetter derives from ChoiceLetter: an announcement's
                // Close / Jump buttons are choices too. The caller judges whether
                // any of them is a decision.
                var options = (choiceLetter.Choices ?? Enumerable.Empty<DiaOption>()).ToList();
                row.ChoicesListing = Listing(options.Count, Math.Min(options.Count, MaxChoices));
                var index = 0u;
                foreach (var option in options.Take(MaxChoices))
                {
                    index++;
                    var choice = new Presentation.LetterChoice { Index = index, Disabled = option.disabled, ClosesDialog = option.resolveTree,
                        HasAction = option.action != null, HasLink = option.link != null || option.linkLateBind != null };
                    if (DiaOptionTextField?.GetValue(option) is string text) choice.Text = Diagnostic(text.Trim());
                    if (option.disabledReason != null) choice.DisabledReason = Diagnostic(option.disabledReason);
                    row.Choices.Add(choice);
                }
            }
            return row;
        }

        // ============================================================= messages

        private static Presentation.Messages ReadMessages(int limit, int tick)
        {
            var field = LiveMessagesField ?? throw new NativeComponentMissing("Verse.Messages.liveMessages was not found on this build.");
            var live = (field.GetValue(null) as List<Verse.Message> ?? throw new NativeComponentMissing("Verse.Messages.liveMessages is not a message list.")).Where(m => m != null).ToList();
            live.Reverse(); // newest first
            var now = RealTime.LastRealTime;
            var result = new Presentation.Messages { Listing = Listing(live.Count, Math.Min(live.Count, limit)) };
            foreach (var message in live.Take(limit))
            {
                var row = new Presentation.TransientMessage { Id = Id(message.GetUniqueLoadID()), Text = Diagnostic(message.text ?? string.Empty),
                    Alpha = NativePresentationReadTools.Finite(message.Alpha), Expired = message.Expired, StartingTick = message.startingTick,
                    StartingFrame = checked((ulong)Math.Max(0, message.startingFrame)), AgeTicks = Math.Max(0, tick - message.startingTick), HasQuest = message.quest != null };
                if (message.def != null && ProtoBoundary.IsIdentifier(message.def.defName)) row.MessageDefName = message.def.defName;
                // The 13-second lifespan is real time; ticks stand still while paused.
                if (MessageStartingTimeField?.GetValue(message) is float startingTime)
                    row.AgeSeconds = NativePresentationReadTools.Finite(Math.Round(now - startingTime, 2));
                var targets = ReadLookTargets(message.lookTargets);
                if (targets != null) row.LookTargets = targets;
                result.Messages_.Add(row);
            }
            RequireUnique(result.Messages_.Select(m => m.Id), "message");
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
                .OrderByDescending(a => (int)a.alert.Priority).ThenBy(a => a.ordinal).ToList();
            var result = new Presentation.Alerts { Listing = Listing(ordered.Count, Math.Min(ordered.Count, limit)),
                SnapshotFingerprint = Fingerprint(ordered.Select(a => a.alert.GetType().FullName!)) };
            foreach (var (alert, ordinal) in ordered.Take(limit))
            {
                // Alerts are singletons per type in the readout; the type is the identity.
                var row = new Presentation.Alert { Id = Id(alert.GetType().FullName!), NativeType = Id(alert.GetType().FullName!), Ordinal = checked((uint)ordinal),
                    NativePriority = Id(alert.Priority.ToString()), Active = alert.Active, EnabledWithActiveExpansions = alert.EnabledWithActiveExpansions };
                try { row.Label = Diagnostic(alert.Label ?? string.Empty); }
                catch (Exception) { row.ReadIssue = ReadIssue("The alert label threw while being read."); }
                try { row.Explanation = Diagnostic(alert.GetExplanation().ToString()); }
                catch (Exception) { row.ReadIssue = ReadIssue("The alert explanation threw while being read."); }
                try
                {
                    var report = alert.GetReport();
                    row.AnyCulpritValid = report.AnyCulpritValid;
                    var culprits = (report.AllCulprits ?? Enumerable.Empty<GlobalTargetInfo>()).ToList();
                    row.Listing = Listing(culprits.Count, Math.Min(culprits.Count, MaxTargets));
                    foreach (var culprit in culprits.Take(MaxTargets)) row.Targets.Add(Target(culprit));
                }
                catch (Exception)
                {
                    // An alert that cannot name its culprits is still an alert; a
                    // map-wide alert has a complete empty target list instead.
                    row.Targets.Clear(); row.Listing = null;
                    row.ReadIssue = ReadIssue("The alert report threw while being read.");
                }
                result.Alerts_.Add(row);
            }
            RequireUnique(result.Alerts_.Select(a => a.Id), "alert");
            return result;
        }

        private static Common.Unavailable ReadIssue(string detail) => new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = detail };

        private static string Fingerprint(IEnumerable<string> ids)
        {
            var hash = 1469598103934665603UL;
            foreach (var value in ids) { hash ^= (ulong)value.Length; foreach (var c in value) hash = (hash ^ c) * 1099511628211UL; hash = (hash ^ '\n') * 1099511628211UL; }
            return hash.ToString("x16");
        }

        // ============================================================== targets

        private static Presentation.LookTargets? ReadLookTargets(Verse.LookTargets? value)
        {
            if (value == null || value.targets == null) return null;
            var targets = value.targets.Where(t => t.IsValid).ToList();
            var result = new Presentation.LookTargets { Valid = value.IsValid, Listing = Listing(targets.Count, Math.Min(targets.Count, MaxTargets)) };
            if (targets.Count > 0) result.Primary = Target(targets[0]);
            foreach (var target in targets.Take(MaxTargets)) result.Targets.Add(Target(target));
            return result;
        }

        private static Presentation.LookTarget Target(GlobalTargetInfo target)
        {
            var row = new Presentation.LookTarget();
            if (target.HasThing && target.Thing != null)
            {
                var thing = target.Thing;
                row.NativeKind = thing is Pawn ? "pawn" : "thing"; row.Id = Id(thing.GetUniqueLoadID()); row.NativeType = Id(thing.GetType().FullName!);
                row.Label = Diagnostic(thing is Pawn pawn ? pawn.Name?.ToStringShort ?? pawn.LabelShort : thing.LabelCap);
                if (thing.def != null && ProtoBoundary.IsIdentifier(thing.def.defName)) row.DefName = thing.def.defName;
                if (thing.Map != null) { row.MapId = thing.Map.uniqueID; if (thing.Spawned && thing.Position.InBounds(thing.Map)) row.Position = Cell(thing.Position); }
                return row;
            }
            if (target.HasWorldObject && target.WorldObject != null)
            {
                var worldObject = target.WorldObject;
                row.NativeKind = "worldObject"; row.Id = Id(worldObject.GetUniqueLoadID()); row.NativeType = Id(worldObject.GetType().FullName!);
                row.Label = Diagnostic(target.Label ?? string.Empty);
                if (worldObject.def != null && ProtoBoundary.IsIdentifier(worldObject.def.defName)) row.DefName = worldObject.def.defName;
                if (worldObject.Tile.Valid) row.WorldTileId = Id(worldObject.Tile.ToString());
                return row;
            }
            if (target.IsMapTarget && target.Map != null && target.Cell.IsValid && target.Cell.InBounds(target.Map))
            {
                row.NativeKind = "cell"; row.MapId = target.Map.uniqueID; row.Position = Cell(target.Cell);
                return row;
            }
            if (target.IsWorldTarget && target.Tile.Valid)
            {
                row.NativeKind = "worldTile"; row.WorldTileId = Id(target.Tile.ToString());
                return row;
            }
            row.NativeKind = "target"; row.Label = Diagnostic(target.Label ?? string.Empty);
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
