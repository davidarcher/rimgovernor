#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
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
    public sealed class NativeNotificationReadTools
    {
        private const int DefaultLetterLimit = 40;
        private const int DefaultMessageLimit = 12;
        private const int DefaultAlertLimit = 40;

        private static readonly FieldInfo? ActiveAlertsField = BridgeCommon.PrivateInstanceField(typeof(AlertsReadout), "activeAlerts");
        private static readonly FieldInfo? LiveMessagesField = BridgeCommon.PrivateStaticField(typeof(Messages), "liveMessages");
        private static readonly FieldInfo? MessageStartingTimeField = BridgeCommon.PrivateInstanceField(typeof(Message), "startingTime");
        private static readonly FieldInfo? DiaOptionTextField = BridgeCommon.PrivateInstanceField(typeof(DiaOption), "text");

        [Tool("rimgovernor/presentation_notifications", Title = "Read native notifications", Description = "Letter stack, live transient messages and active alerts as independently available sections, newest/loudest first with exact counts. Read-only: no open, dismiss, choice or acknowledgement.")]
        [ToolResponse("payload", "string", "Official ProtoJSON NotificationsReply; usable in headless and graphical games.", Always = true)]
        public async Task<object> Notifications(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON NotificationsRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Presentation.NotificationsRequest>(ctx, "rimgovernor/presentation_notifications", request, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Presentation.NotificationsReply { Outcome = new Presentation.NotificationsReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out var context, out var error))
                    return ProtoBoundary.Encode(new Presentation.NotificationsReply { Outcome = new Presentation.NotificationsReplyOutcome.FailureCase(error) });
                try
                {
                    var snapshot = new Presentation.NotificationsSnapshot { Context = context };
                    var tick = Find.TickManager.TicksGame;
                    if (parsed.IncludeLetters ?? true)
                        snapshot = snapshot with { Letters = Section(() => ReadLetters(Limit(parsed.LetterLimit, DefaultLetterLimit), tick),
                            (Presentation.Letters v) => new Presentation.LetterSection { Outcome = v }, u => new Presentation.LetterSection { Outcome = u }) };
                    if (parsed.IncludeMessages ?? true)
                        snapshot = snapshot with { Messages = Section(() => ReadMessages(Limit(parsed.MessageLimit, DefaultMessageLimit), tick),
                            (Presentation.Messages v) => new Presentation.MessageSection { Outcome = v }, u => new Presentation.MessageSection { Outcome = u }) };
                    if (parsed.IncludeAlerts ?? true)
                        snapshot = snapshot with { Alerts = Section(() => ReadAlerts(Limit(parsed.AlertLimit, DefaultAlertLimit)),
                            (Presentation.Alerts v) => new Presentation.AlertSection { Outcome = v }, u => new Presentation.AlertSection { Outcome = u }) };
                    return NativePresentationReadTools.Encode(new Presentation.NotificationsReply { Outcome = snapshot });
                }
                catch (Exception errorRead)
                {
                    return ProtoBoundary.Encode(new Presentation.NotificationsReply { Outcome = ProtoBoundary.Fail(
                        errorRead is NativePresentationReadTools.ReadLimit ? Common.FailureCode.CapacityExhausted : Common.FailureCode.Unavailable,
                        errorRead is NativePresentationReadTools.ReadLimit ? errorRead.Message : "Native notifications could not be read completely.") });
                }
            }, cancellationToken).ConfigureAwait(false);
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

        private static Presentation.Letters ReadLetters(int limit, int tick)
        {
            var stack = Find.LetterStack ?? throw new NativeComponentMissing("Find.LetterStack is unavailable.");
            var letters = (stack.LettersListForReading ?? new List<Verse.Letter>()).Where(l => l != null).ToList();
            letters.Reverse(); // newest first, the order the game draws the stack
            var result = new Presentation.Letters { Listing = Listing(letters.Count, Math.Min(letters.Count, limit)) };
            result = result with { Items = [.. letters.Take(limit).Select(letter => ReadLetter(letter, tick))] };
            RequireUnique(result.Items.Select(l => l.Id ?? ""), "letter");
            return result;
        }

        private static Presentation.Letter ReadLetter(Verse.Letter letter, int tick)
        {
            var row = new Presentation.Letter { Id = Id(letter.GetUniqueLoadID()), NativeType = Id(letter.GetType().FullName!),
                Label = Diagnostic(letter.Label.ToString()), ArrivalTick = letter.arrivalTick, AgeTicks = Math.Max(0, tick - letter.arrivalTick),
                Dismissible = letter.CanDismissWithRightClick, AutomaticallyOpens = letter.ShouldAutomaticallyOpenLetter };
            if (letter.def != null && ProtoBoundary.IsIdentifier(letter.def.defName)) row = row with { DefName = letter.def.defName };
            if (letter.relatedFaction != null) row = row with { RelatedFactionLabel = Diagnostic(letter.relatedFaction.Name) };
            var targets = ReadLookTargets(letter.lookTargets);
            if (targets != null) row = row with { LookTargets = targets };
            if (letter is ChoiceLetter choiceLetter)
            {
                row = row with { Text = Diagnostic(choiceLetter.Text.ToString()) };
                // Verse.StandardLetter derives from ChoiceLetter: an announcement's
                // Close / Jump buttons are choices too. The caller judges whether
                // any of them is a decision.
                var options = (choiceLetter.Choices ?? Enumerable.Empty<DiaOption>()).ToList();
                row = row with { ChoicesListing = Listing(options.Count, options.Count) };
                var index = 0u;
                foreach (var option in options)
                {
                    index++;
                    var choice = new Presentation.LetterChoice { Index = index, Disabled = option.disabled, ClosesDialog = option.resolveTree,
                        HasAction = option.action != null, HasLink = option.link != null || option.linkLateBind != null };
                    if (DiaOptionTextField?.GetValue(option) is string text) choice = choice with { Text = Diagnostic(text.Trim()) };
                    if (option.disabledReason != null) choice = choice with { DisabledReason = Diagnostic(option.disabledReason) };
                    row = row with { Choices = [.. row.Choices, choice] };
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
            var rows = new List<Presentation.TransientMessage>();
            foreach (var message in live.Take(limit))
            {
                var row = new Presentation.TransientMessage { Id = Id(message.GetUniqueLoadID()), Text = Diagnostic(message.text ?? string.Empty),
                    Alpha = NativePresentationReadTools.Finite(message.Alpha), Expired = message.Expired, StartingTick = message.startingTick,
                    StartingFrame = checked((ulong)Math.Max(0, message.startingFrame)), AgeTicks = Math.Max(0, tick - message.startingTick), HasQuest = message.quest != null };
                if (message.def != null && ProtoBoundary.IsIdentifier(message.def.defName)) row = row with { MessageDefName = message.def.defName };
                // The 13-second lifespan is real time; ticks stand still while paused.
                if (MessageStartingTimeField?.GetValue(message) is float startingTime)
                    row = row with { AgeSeconds = NativePresentationReadTools.Finite(Math.Round(now - startingTime, 2)) };
                var targets = ReadLookTargets(message.lookTargets);
                if (targets != null) row = row with { LookTargets = targets };
                rows.Add(row);
            }
            result = result with { Items = [.. rows] };
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
                .OrderByDescending(a => (int)a.alert.Priority).ThenBy(a => a.ordinal).ToList();
            var result = new Presentation.Alerts { Listing = Listing(ordered.Count, Math.Min(ordered.Count, limit)),
                SnapshotFingerprint = Fingerprint(ordered.Select(a => a.alert.GetType().FullName!)) };
            var alertRows = new List<Presentation.Alert>();
            foreach (var (alert, ordinal) in ordered.Take(limit))
            {
                // Alerts are singletons per type in the readout; the type is the identity.
                var row = new Presentation.Alert { Id = Id(alert.GetType().FullName!), NativeType = Id(alert.GetType().FullName!), Ordinal = checked((uint)ordinal),
                    NativePriority = Id(alert.Priority.ToString()), Active = alert.Active, EnabledWithActiveExpansions = alert.EnabledWithActiveExpansions };
                try { row = row with { Label = Diagnostic(alert.Label ?? string.Empty) }; }
                catch (Exception) { row = row with { ReadIssue = ReadIssue("The alert label threw while being read.") }; }
                try { row = row with { Explanation = Diagnostic(alert.GetExplanation().ToString()) }; }
                catch (Exception) { row = row with { ReadIssue = ReadIssue("The alert explanation threw while being read.") }; }
                try
                {
                    var report = alert.GetReport();
                    row = row with { AnyCulpritValid = report.AnyCulpritValid };
                    var culprits = (report.AllCulprits ?? Enumerable.Empty<GlobalTargetInfo>()).ToList();
                    row = row with { Listing = Listing(culprits.Count, culprits.Count) };
                    foreach (var culprit in culprits) row = row with { Targets = [.. row.Targets, Target(culprit)] };
                }
                catch (Exception)
                {
                    // An alert that cannot name its culprits is still an alert; a
                    // map-wide alert has a complete empty target list instead.
                    row = row with { Targets = default, Listing = null };
                    row = row with { ReadIssue = ReadIssue("The alert report threw while being read.") };
                }
                alertRows.Add(row);
            }
            result = result with { Items = [.. alertRows] };
            RequireUnique(result.Items.Select(a => a.Id ?? ""), "alert");
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
            var result = new Presentation.LookTargets { Valid = value.IsValid, Listing = Listing(targets.Count, targets.Count) };
            if (targets.Count > 0) result = result with { Primary = Target(targets[0]) };
            foreach (var target in targets) result = result with { Targets = [.. result.Targets, Target(target)] };
            return result;
        }

        private static Presentation.LookTarget Target(GlobalTargetInfo target)
        {
            var row = new Presentation.LookTarget();
            if (target.HasThing && target.Thing != null)
            {
                var thing = target.Thing;
                row = row with { NativeKind = thing is Pawn ? "pawn" : "thing" }; row = row with { Id = Id(thing.GetUniqueLoadID()) }; row = row with { NativeType = Id(thing.GetType().FullName!) };
                row = row with { Label = Diagnostic(thing is Pawn pawn ? pawn.Name?.ToStringShort ?? pawn.LabelShort : thing.LabelCap) };
                if (thing.def != null && ProtoBoundary.IsIdentifier(thing.def.defName)) row = row with { DefName = thing.def.defName };
                if (thing.Map != null) { row = row with { MapId = thing.Map.uniqueID }; if (thing.Spawned && thing.Position.InBounds(thing.Map)) row = row with { Position = Cell(thing.Position) }; }
                return row;
            }
            if (target.HasWorldObject && target.WorldObject != null)
            {
                var worldObject = target.WorldObject;
                row = row with { NativeKind = "worldObject" }; row = row with { Id = Id(worldObject.GetUniqueLoadID()) }; row = row with { NativeType = Id(worldObject.GetType().FullName!) };
                row = row with { Label = Diagnostic(target.Label ?? string.Empty) };
                if (worldObject.def != null && ProtoBoundary.IsIdentifier(worldObject.def.defName)) row = row with { DefName = worldObject.def.defName };
                if (worldObject.Tile.Valid) row = row with { WorldTileId = Id(worldObject.Tile.ToString()) };
                return row;
            }
            if (target.IsMapTarget && target.Map != null && target.Cell.IsValid && target.Cell.InBounds(target.Map))
            {
                row = row with { NativeKind = "cell" }; row = row with { MapId = target.Map.uniqueID }; row = row with { Position = Cell(target.Cell) };
                return row;
            }
            if (target.IsWorldTarget && target.Tile.Valid)
            {
                row = row with { NativeKind = "worldTile" }; row = row with { WorldTileId = Id(target.Tile.ToString()) };
                return row;
            }
            row = row with { NativeKind = "target" }; row = row with { Label = Diagnostic(target.Label ?? string.Empty) };
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
