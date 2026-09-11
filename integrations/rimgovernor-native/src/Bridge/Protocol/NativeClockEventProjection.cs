using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Common = RimGovernor.Protocol.Common;
using Clock = RimGovernor.Protocol.Clock;

namespace HomeBridge.BridgeTools
{
    // Strict projection at observation time. Legacy payloads never escape as arbitrary wire data.
    internal static class NativeClockEventProjection
    {
        internal static string Text(string value)
        {
            new UTF8Encoding(false, true).GetByteCount(value);
            int end = 0;
            for (int count = 0; end < value.Length && count < 4096; count++, end++) if (char.IsHighSurrogate(value[end])) end++;
            return value.Substring(0, end);
        }
        private static object Required(Dictionary<string, object> row, string key)
        { object value; if (row == null || !row.TryGetValue(key, out value) || value == null) throw new InvalidOperationException("Missing clock evidence " + key); return value; }
        private static string String(Dictionary<string, object> row, string key) => Text((string)Required(row, key));
        private static long Number(Dictionary<string, object> row, string key) => Convert.ToInt64(Required(row, key));
        private static bool Bool(Dictionary<string, object> row, string key) => (bool)Required(row, key);
        private static float Real(Dictionary<string, object> row, string key)
        { var value = Convert.ToSingle(Required(row, key)); if (float.IsInfinity(value) || float.IsNaN(value)) throw new InvalidOperationException("Nonfinite clock evidence"); return value; }
        private static string Id(string id)
        { if (!ProtoBoundary.IsIdentifier(id)) throw new InvalidOperationException("Invalid clock evidence identity"); return id; }
        private static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> row, string key)
        { var values = ((IEnumerable)Required(row, key)).Cast<Dictionary<string, object>>().ToList(); if (values.Count > 256) throw new InvalidOperationException("Clock event collection exceeds256"); return values; }
        internal static Clock.Alert Alert(Dictionary<string, object> row)
        {
            var result = new Clock.Alert { Key = Id(String(row, "alertKey")), Label = String(row, "label") };
            object priority; if (row.TryGetValue("priority", out priority) && priority != null) result.Priority = Text((string)priority);
            return result;
        }
        private static Clock.Letter Letter(Dictionary<string, object> row)
        {
            var result = new Clock.Letter { Id = Id(String(row, "id")), Label = String(row, "label") };
            object value; if (row.TryGetValue("letterDef", out value) && value != null) result.DefName = Id((string)value);
            if (row.TryGetValue("negative", out value) && value != null) result.Negative = (bool)value;
            return result;
        }
        private static Clock.TransientMessage Message(Dictionary<string, object> row)
        {
            var result = new Clock.TransientMessage { Id = Id(String(row, "id")), Text = String(row, "text"), StartingTick = Number(row, "startingTick") };
            object value; if (row.TryGetValue("messageType", out value) && value != null) result.TypeDef = Id((string)value);
            if (row.TryGetValue("negative", out value) && value != null) result.Negative = (bool)value;
            return result;
        }
        private static Clock.PawnEvent Pawn(Dictionary<string, object> row, Func<int, string> resolve, bool compact = false)
        {
            var result = new Clock.PawnEvent { PawnId = Id(resolve(checked((int)Number(row, compact ? "thingId" : "pawnId")))), Name = String(row, compact ? "name" : "pawnName") };
            object position;
            if (compact && row.ContainsKey("x")) result.Position = new Common.Cell { X = checked((int)Number(row, "x")), Z = checked((int)Number(row, "z")) };
            else if (row.TryGetValue("position", out position))
            { var cell = (Dictionary<string, object>)position; result.Position = new Common.Cell { X = checked((int)Number(cell, "x")), Z = checked((int)Number(cell, "z")) }; }
            object reason; if (row.TryGetValue("reason", out reason) && reason != null) result.Reason = Text((string)reason);
            return result;
        }
        private static Clock.Health Health(Dictionary<string, object> row, string suffix) => new Clock.Health
        { InjuryCount = checked((int)Number(row, "injuryCount" + suffix)), Severity = Real(row, "severity" + suffix), BleedRate = Real(row, "bleedRate" + suffix), BloodLoss = Real(row, "bloodLoss" + suffix), SummaryHealth = Real(row, suffix == "Before" ? "healthAtStart" : "healthNow") };
        private static Clock.Injury Injury(Dictionary<string, object> row, Func<int, string> resolve)
        {
            var result = new Clock.Injury { Pawn = Pawn(row, resolve), Before = Health(row, "Before"), After = Health(row, "After"),
                Suppression = Clock.InjurySuppression.None, CooldownRemainingMs = checked((uint)Math.Max(0, Number(row, "cooldownRemainingMs"))), NewWound = Bool(row, "newWound") };
            object value; if (row.TryGetValue("suppressedBy", out value) && value != null)
                result.Suppression = (string)value == "acknowledged" ? Clock.InjurySuppression.Acknowledged : (string)value == "cooldown" ? Clock.InjurySuppression.Cooldown : throw new InvalidOperationException("Unknown injury suppression");
            return result;
        }
        internal static Clock.StopReason StopReason(string kind)
        {
            switch (kind)
            {
                case "requested_pause": return Clock.StopReason.RequestedPause;
                case "tick_budget": return Clock.StopReason.TickBudget;
                case "session_changed": return Clock.StopReason.SessionChanged;
                case "unavailable": return Clock.StopReason.Unavailable;
                case "external_pause": return Clock.StopReason.ExternalPause;
                case "letter_pause": return Clock.StopReason.LetterPause;
                case "external_speed_changed": return Clock.StopReason.ExternalSpeedChanged;
                case "lease_expired": return Clock.StopReason.LeaseExpired;
                case "watcher_error": return Clock.StopReason.WatcherError;
                case "event_journal_error": return Clock.StopReason.EventJournalError;
                case "force_paused": return Clock.StopReason.ForcePaused;
                case "colony_naming": return Clock.StopReason.ColonyNaming;
                case "start_refused": return Clock.StopReason.StartRefused;
                case "notification_batch": return Clock.StopReason.NotificationBatch;
                case "medical_rest_changed": return Clock.StopReason.MedicalRestChanged;
                case "hostile": return Clock.StopReason.Hostile;
                case "colonist_downed": return Clock.StopReason.ColonistDowned;
                case "predator_hunt": return Clock.StopReason.PredatorHunt;
                case "hunting_route_unsafe": return Clock.StopReason.HuntingRouteUnsafe;
                case "colonist_health": return Clock.StopReason.ColonistHealth;
                case "colonist_injury": return Clock.StopReason.ColonistInjury;
                default: throw new InvalidOperationException("Unknown native clock stop kind: " + kind);
            }
        }
        internal static Clock.Event Event(string kind, string detail, Dictionary<string, object> payload, Common.ObservationContext context,
            Clock.EpochOwner owner, long cursor, long observedAt, Clock.Epoch started, Func<int, string> resolvePawn)
        {
            var result = new Clock.Event { Cursor = cursor, Owner = owner.Clone(), Context = context.Clone(), ObservedAtUnixMs = observedAt, Detail = Text(detail) };
            switch (kind)
            {
                case "started": result.Started = new Clock.EpochStarted { Epoch = started.Clone() }; break;
                case "speed_changed": result.SpeedChanged = new Clock.SpeedChanged { Speed = ParseSpeed(String(payload, "speed")) }; break;
                case "notification_new": result.Notification = payload.ContainsKey("label") ? new Clock.Notification { Letter = Letter(payload) } : new Clock.Notification { Message = Message(payload) }; break;
                case "alert_new": result.Alert = Alert(payload); break;
                case "injury_observed": result.InjuryObserved = Injury(payload, resolvePawn); break;
                case "hostiles_cleared":
                    result.HostilesCleared = new Clock.HostilesCleared { ConsciousHostilesBefore = checked((int)Number(payload, "consciousHostilesBefore")), AcrossEpochRestart = Bool(payload, "acrossRestart"), Completeness = new Common.PageInfo { Complete = true } };
                    result.HostilesCleared.DownedHostiles.Add(Rows(payload, "downedHostiles").Select(row => Pawn(row, resolvePawn, true)));
                    result.HostilesCleared.DraftedColonists.Add(Rows(payload, "draftedColonists").Select(row => Pawn(row, resolvePawn, true))); break;
                case "long_event": case "transient_force_pause":
                    result.ForcePauseWaiting = new Clock.ForcePauseWaiting { Pause = new Clock.PauseEvidence { LongEventPending = Bool(payload, "longEvent"), RequestedSpeed = ParseSpeed(String(payload, "requestedSpeed")) }, WaitedMs = 0, GraceMs = checked((uint)Number(payload, "graceMs")) }; break;
                case "force_pause_cleared": result.ForcePauseCleared = new Clock.ForcePauseCleared { WaitedMs = checked((ulong)Number(payload, "waitedMs")), ForcePauseKind = String(payload, "forcePauseKind"), SpeedRestored = Bool(payload, "speedRestored") }; break;
                case "pause_failed": result.PauseFailed = new Clock.PauseFailed { Pending = new Clock.StopEvent { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native pause did not take; epoch remains armed." } } }; break;
                default:
                    var stop = new Clock.StopEvent { Reason = StopReason(kind) };
                    if (kind == "notification_batch")
                    {
                        stop.Notifications = new Clock.NotificationBatch { Completeness = new Common.PageInfo { Complete = true } };
                        stop.Notifications.Letters.Add(Rows(payload, "letters").Select(Letter)); stop.Notifications.Messages.Add(Rows(payload, "messages").Select(Message));
                    }
                    else if (kind == "colonist_injury") stop.Injury = Injury(payload, resolvePawn);
                    else if (kind == "colonist_health") stop.Health = new Clock.HealthThreshold { Pawn = Pawn(payload, resolvePawn), HealthAtStart = Real(payload, "healthAtStart"), HealthNow = Real(payload, "healthNow"), MinHealthFraction = Real(payload, "minHealthFraction"), HealthDropFraction = Real(payload, "healthDropFraction") };
                    else if (payload != null && payload.ContainsKey("pawnId")) stop.Pawn = Pawn(payload, resolvePawn);
                    else stop.Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotObserved, Detail = "Stop reason and diagnostic were observed; additional structured evidence was not captured." };
                    result.Stopped = stop; break;
            }
            return result;
        }
        private static Clock.Speed ParseSpeed(string value) => value == "Normal" ? Clock.Speed.Normal : value == "Fast" ? Clock.Speed.Fast : value == "Superfast" ? Clock.Speed.Superfast : throw new InvalidOperationException("Nonordinary clock speed");
    }
}
