#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimGovernor.Brain.Domain;
using Common = RimGovernor.Contracts.Wire.Common;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    // Strict projection at observation time.
    internal static class NativeClockEventProjection
    {
        internal static string Text(string? value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            new UTF8Encoding(false, true).GetByteCount(value);
            int end = 0;
            for (int count = 0; end < value.Length && count < 4096; count++, end++) if (char.IsHighSurrogate(value[end])) end++;
            return value.Substring(0, end);
        }
        private static object Required(Dictionary<string, object?>? row, string key)
        { object? value; if (row == null || !row.TryGetValue(key, out value) || value == null) throw new InvalidOperationException("Missing clock evidence " + key); return value; }
        private static string String(Dictionary<string, object?> row, string key) => Text((string)Required(row, key));
        private static long Number(Dictionary<string, object?> row, string key) => Convert.ToInt64(Required(row, key));
        private static bool Bool(Dictionary<string, object?> row, string key) => (bool)Required(row, key);
        private static float Real(Dictionary<string, object?> row, string key)
        { var value = Convert.ToSingle(Required(row, key)); if (float.IsInfinity(value) || float.IsNaN(value)) throw new InvalidOperationException("Nonfinite clock evidence"); return value; }
        private static string Id(string id)
        { if (!ProtoBoundary.IsIdentifier(id)) throw new InvalidOperationException("Invalid clock evidence identity"); return id; }
        private static IEnumerable<Dictionary<string, object?>> Rows(Dictionary<string, object?> row, string key)
        { var values = ((IEnumerable)Required(row, key)).Cast<Dictionary<string, object?>>().ToList(); return values; }
        internal static Clock.Alert Alert(Dictionary<string, object?> row)
        {
            var result = new Clock.Alert { Key = Id(String(row, "alertKey")), Label = String(row, "label") };
            object? priority; if (row.TryGetValue("priority", out priority) && priority != null) result = result with { Priority = Text((string)priority) };
            return result;
        }
        private static Clock.Letter Letter(Dictionary<string, object?> row)
        {
            var result = new Clock.Letter { Id = Id(String(row, "id")), Label = String(row, "label") };
            object? value; if (row.TryGetValue("letterDef", out value) && value != null) result = result with { DefName = Id((string)value) };
            return result;
        }
        private static Clock.TransientMessage Message(Dictionary<string, object?> row)
        {
            return new Clock.TransientMessage { Id = Id(String(row, "id")), Text = String(row, "text") };
        }
        private static Clock.PawnEvent Pawn(Dictionary<string, object?> row, Func<int, string> resolve)
        {
            var result = new Clock.PawnEvent { PawnId = Id(resolve(checked((int)Number(row, "pawnId")))), Name = String(row, "pawnName") };
            object? position;
            if (row.TryGetValue("position", out position))
            { var cell = (Dictionary<string, object?>)(position ?? throw new InvalidOperationException("Missing clock evidence position")); result = result with { Position = new Common.Cell { X = checked((int)Number(cell, "x")), Z = checked((int)Number(cell, "z")) } }; }
            object? reason; if (row.TryGetValue("reason", out reason) && reason != null) result = result with { Reason = Text((string)reason) };
            return result;
        }
        private static Clock.Health Health(Dictionary<string, object?> row, string suffix) => new Clock.Health
        { Severity = Real(row, "severity" + suffix) };
        private static Clock.Injury Injury(Dictionary<string, object?> row, Func<int, string> resolve) =>
            new Clock.Injury { Pawn = Pawn(row, resolve), Before = Health(row, "Before"), After = Health(row, "After") };
        internal static Clock.HoldReason HoldReason(string? kind)
        {
            switch (kind)
            {
                case "never_run": return Clock.HoldReason.NeverRun;
                case "coupled_order": return Clock.HoldReason.CoupledOrder;
                case "review": return Clock.HoldReason.Review;
                case "autosave": return Clock.HoldReason.Autosave;
                case "session_changed": return Clock.HoldReason.SessionChanged;
                case "unavailable": return Clock.HoldReason.Unavailable;
                case "external_pause": return Clock.HoldReason.ExternalPause;
                case "letter_pause": return Clock.HoldReason.LetterPause;
                case "external_speed_changed": return Clock.HoldReason.ExternalSpeedChanged;
                case "watcher_error": return Clock.HoldReason.WatcherError;
                case "event_journal_error": return Clock.HoldReason.EventJournalError;
                case "force_paused": return Clock.HoldReason.GameForced;
                case "colony_naming": return Clock.HoldReason.ColonyNaming;
                case "dialog_pause": return Clock.HoldReason.DialogPause;
                case "run_refused": return Clock.HoldReason.RunRefused;
                case "notification_batch": return Clock.HoldReason.NotificationBatch;
                case "medical_rest_changed": return Clock.HoldReason.MedicalRestChanged;
                case "hostile": return Clock.HoldReason.Hostile;
                case "colonist_downed": return Clock.HoldReason.ColonistDowned;
                case "predator_hunt": return Clock.HoldReason.PredatorHunt;
                case "hunting_route_unsafe": return Clock.HoldReason.HuntingRouteUnsafe;
                case "colonist_health": return Clock.HoldReason.ColonistHealth;
                case "colonist_injury": return Clock.HoldReason.ColonistInjury;
                case "combat_event": return Clock.HoldReason.CombatEvent;
                default: throw new InvalidOperationException("Unknown native clock hold kind: " + kind);
            }
        }
        internal static Clock.ClockEvent Event(string kind, string? detail, Dictionary<string, object?>? payload, Common.ObservationContext context,
            long cursor, long observedAt, Func<int, string> resolvePawn)
        {
            Dictionary<string, object?> P() => payload ?? throw new InvalidOperationException("Missing clock evidence for " + kind);
            var result = new Clock.ClockEvent { Cursor = cursor, Context = context, ObservedAtUnixMs = observedAt, Detail = Text(detail) };
            switch (kind)
            {
                case "authority_changed":
                {
                    var changed = new Clock.AuthorityChanged { Generation = checked((ulong)Number(P(), "generation")), Active = Bool(P(), "active") };
                    object? reason;
                    if (P().TryGetValue("reason", out reason) && reason != null) changed = changed with { Reason = Text((string)reason) };
                    result = result with { Event = changed };
                    break;
                }
                case "observation_invalidated":
                {
                    var invalidated = new Clock.ObservationInvalidated { Reason = Text(String(P(), "reason")), Families = EquatableArray.From(FamilyNames(P())) };
                    // Narrowing: the changed rows' ids and the one rectangle
                    // the change touched; a probe that cannot attribute a change
                    // sends the families alone.
                    object? ids, cells;
                    if (P().TryGetValue("entityIds", out ids) && ids != null)
                        invalidated = invalidated with { EntityIds = EquatableArray.From(((IEnumerable)ids).Cast<object?>().Select(id => Id(id as string ?? throw new InvalidOperationException("Entity id is not a string"))).Distinct().ToList()) };
                    if (P().TryGetValue("cells", out cells) && cells != null)
                    {
                        var rect = (Dictionary<string, object?>)cells;
                        invalidated = invalidated with { Cells = new Clock.Rectangle {
                            Minimum = new Common.Cell { X = checked((int)Number(rect, "minX")), Z = checked((int)Number(rect, "minZ")) },
                            Maximum = new Common.Cell { X = checked((int)Number(rect, "maxX")), Z = checked((int)Number(rect, "maxZ")) } } };
                    }
                    result = result with { Event = invalidated };
                    break;
                }
                case "resumed":
                    result = result with { Event = new Clock.Resumed { Speed = ParseSpeed(String(P(), "speed")), From = HoldReason(String(P(), "from")) } };
                    break;
                case "notification_new":
                    result = result with { Event = P().ContainsKey("label")
                        ? new Clock.Notification { Source = new Clock.NotificationSource.LetterCase(Letter(P())) }
                        : new Clock.Notification { Source = new Clock.NotificationSource.MessageCase(Message(P())) } };
                    break;
                case "alert_new": result = result with { Event = Alert(P()) }; break;
                case "injury_observed": result = result with { Event = Injury(P(), resolvePawn) }; break;
                case "hostiles_cleared":
                    result = result with { Event = new Clock.HostilesCleared() };
                    break;
                case "long_event": case "transient_force_pause":
                    result = result with { Event = new Clock.ForcePauseWaiting { Pause = new Clock.PauseEvidence() } }; break;
                case "force_pause_cleared": result = result with { Event = new Clock.ForcePauseCleared() }; break;
                case "pause_failed": result = result with { Event = new Clock.PauseFailed { Pending = new Clock.HoldEntered { Evidence = new Clock.HoldEvidence.UnavailableCase(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native pause did not take; the hold stays armed." }) } } }; break;
                default:
                    var stop = new Clock.HoldEntered { Reason = HoldReason(kind) };
                    if (kind == "notification_batch")
                        stop = stop with { Evidence = new Clock.HoldEvidence.NotificationsCase(new Clock.NotificationBatch {
                            Letters = EquatableArray.From(Rows(P(), "letters").Select(Letter).ToList()) }) };
                    else if (kind == "combat_event")
                    {
                        var combat = new Clock.CombatEventHold { Event = (Clock.CombatEvent)checked((int)Number(P(), "combatEvent")), Reason = Text(String(P(), "reason")) };
                        object? thing; if (P().TryGetValue("thingId", out thing) && thing != null) combat = combat with { ThingId = Id((string)thing) };
                        stop = stop with { Evidence = new Clock.HoldEvidence.CombatCase(combat) };
                    }
                    else if (kind == "colonist_injury") stop = stop with { Evidence = new Clock.HoldEvidence.InjuryCase(Injury(P(), resolvePawn)) };
                    else if (kind == "colonist_health") stop = stop with { Evidence = new Clock.HoldEvidence.HealthCase(new Clock.HealthThreshold { Pawn = Pawn(P(), resolvePawn), MinHealthFraction = Real(P(), "minHealthFraction"), HealthDropFraction = Real(P(), "healthDropFraction") }) };
                    else if (payload != null && payload.ContainsKey("pawnId")) stop = stop with { Evidence = new Clock.HoldEvidence.PawnCase(Pawn(P(), resolvePawn)) };
                    else stop = stop with { Evidence = new Clock.HoldEvidence.UnavailableCase(new Common.Unavailable { Reason = Common.UnavailableReason.NotObserved, Detail = "Stop reason and diagnostic were observed; additional structured evidence was not captured." }) };
                    // Latency split: the tick the stop was raised at and,
                    // where the hazard carries one, the tick it arose.
                    object? detected, occurrence;
                    if (payload != null && payload.TryGetValue("detectedTick", out detected) && detected != null) stop = stop with { DetectedTick = Convert.ToInt64(detected) };
                    if (payload != null && payload.TryGetValue("occurrenceTick", out occurrence) && occurrence != null) stop = stop with { OccurrenceTick = Convert.ToInt64(occurrence) };
                    result = result with { Event = stop }; break;
            }
            return result;
        }
        // Fact family names as Go spells them (bridge.FactFamily).
        internal static Clock.FactFamily Family(string name)
        {
            switch (name)
            {
                case "definitions": return Clock.FactFamily.Definitions;
                case "world": return Clock.FactFamily.World;
                case "identity": return Clock.FactFamily.Identity;
                case "colony": return Clock.FactFamily.Colony;
                case "pawns": return Clock.FactFamily.Pawns;
                case "emergency": return Clock.FactFamily.Emergency;
                case "rooms": return Clock.FactFamily.Rooms;
                case "research": return Clock.FactFamily.Research;
                default: throw new InvalidOperationException("Unknown fact family: " + name);
            }
        }
        private static IEnumerable<Clock.FactFamily> FamilyNames(Dictionary<string, object?> row)
        {
            var names = ((IEnumerable)Required(row, "families")).Cast<object?>();
            var families = names.Select(name => Family(name as string ?? throw new InvalidOperationException("Fact family is not a name"))).Distinct().ToList();
            if (families.Count == 0) throw new InvalidOperationException("Observation invalidation names no family");
            return families;
        }
        private static Clock.Speed ParseSpeed(string value) => value == "Normal" ? Clock.Speed.Normal : value == "Fast" ? Clock.Speed.Fast : value == "Superfast" ? Clock.Speed.Superfast : value == "Ultrafast" ? Clock.Speed.Ultrafast : throw new InvalidOperationException("Nonordinary clock speed");
    }
}
