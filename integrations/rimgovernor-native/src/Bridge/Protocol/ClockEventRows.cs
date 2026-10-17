#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The one OTLP row native emits per journalled clock or colony event (otel-signals.md#clock-and-colony-events),
    /// so holds, resumes and the other events reach telemetry from one source. Pure: it reads only the event, never
    /// the game, and the brain's tests compile it in.
    /// </summary>
    internal static class ClockEventRows
    {
        internal static void Write(TelemetryEmitter emitter, Clock.ClockEvent value, long? stopAtMs)
        {
            var (kind, payload) = Row(value, stopAtMs);
            emitter.EmitRow(kind.Name, new RowContext { Component = ClockEventTelemetry.Component, Tick = value.Context?.Tick }, payload);
        }

        /// <summary>The event's kind and payload. <paramref name="stopAtMs"/> is the hold's stamp, read only for a hold.</summary>
        internal static (TelemetryKind Kind, Dictionary<string, object?> Payload) Row(Clock.ClockEvent value, long? stopAtMs)
        {
            var p = new Dictionary<string, object?>(StringComparer.Ordinal) { ["msg"] = value.Detail, [ClockEventTelemetry.ObservedAtKey] = value.ObservedAtUnixMs };
            if (value.Context is { } context)
            {
                p[ClockEventTelemetry.TickKey] = context.Tick;
                p["colony"] = context.Identity.ColonyId;
                p["load"] = context.Identity.LoadToken;
                p["map"] = context.Identity.MapId;
                if (context.NativeGeneration is { } generation) p["native_generation"] = checked((long)generation);
            }
            switch (value.Event)
            {
                case Clock.ClockEventEvent.HoldEnteredCase { Value: var held }:
                    Hold(p, held, value.Context?.Tick);
                    p[ClockEventTelemetry.StopAtMsKey] = stopAtMs;
                    return (TelemetryKinds.ClockHold, p);
                case Clock.ClockEventEvent.ResumedCase { Value: var resumed }:
                    p[ClockEventTelemetry.FromKey] = resumed.From?.ToString();
                    p["speed"] = resumed.Speed?.ToString();
                    return (TelemetryKinds.ClockResume, p);
                case Clock.ClockEventEvent.AuthorityChangedCase { Value: var changed }:
                    p["generation"] = Long(changed.Generation);
                    p[ClockEventTelemetry.ReasonKey] = changed.Reason;
                    p["active"] = changed.Active;
                    return (TelemetryKinds.ClockAuthority, p);
                case Clock.ClockEventEvent.AlertCase { Value: var alert }:
                    p["key"] = alert.Key;
                    p["label"] = alert.Label;
                    p["priority"] = alert.Priority;
                    return (TelemetryKinds.ClockAlert, p);
                case Clock.ClockEventEvent.NotificationCase { Value: var notification }:
                    switch (notification.Source)
                    {
                        case Clock.NotificationSource.LetterCase { Value: var letter }:
                            p["source"] = "letter";
                            Letter(p, letter, "");
                            break;
                        case Clock.NotificationSource.MessageCase { Value: var message }:
                            p["source"] = "message";
                            foreach (var (key, field) in Message(message)) p[key] = field;
                            break;
                    }
                    return (TelemetryKinds.ClockNotification, p);
                case Clock.ClockEventEvent.InjuryObservedCase { Value: var injury }:
                    Injury(p, injury);
                    return (TelemetryKinds.ClockInjury, p);
                case Clock.ClockEventEvent.HostilesClearedCase:
                    return (TelemetryKinds.ClockHostilesCleared, p);
                case Clock.ClockEventEvent.ObservationInvalidatedCase { Value: var invalidated }:
                    p["families"] = invalidated.Families.Select(family => (object?)family.ToString()).ToList();
                    p[ClockEventTelemetry.ReasonKey] = invalidated.Reason;
                    if (invalidated.EntityIds.Count > 0) p["entity_ids"] = invalidated.EntityIds.Select(id => (object?)id).ToList();
                    if (invalidated.Cells is { } cells)
                        p["cells"] = new Dictionary<string, object?> { ["min_x"] = cells.Minimum?.X, ["min_z"] = cells.Minimum?.Z, ["max_x"] = cells.Maximum?.X, ["max_z"] = cells.Maximum?.Z };
                    return (TelemetryKinds.ClockInvalidation, p);
                case Clock.ClockEventEvent.ForcePauseWaitingCase { Value: var waiting }:
                    p["state"] = "waiting";
                    if (waiting.Pause is { } pause) Pause(p, pause);
                    return (TelemetryKinds.ClockLongEvent, p);
                case Clock.ClockEventEvent.ForcePauseClearedCase:
                    p["state"] = "cleared";
                    return (TelemetryKinds.ClockLongEvent, p);
                case Clock.ClockEventEvent.PauseFailedCase { Value: var failed }:
                    if (failed.Pending is { } pending) Hold(p, pending, value.Context?.Tick);
                    return (TelemetryKinds.ClockPauseFailed, p);
                default:
                    throw new InvalidOperationException("Clock event has no variant");
            }
        }

        // A hold's reason, evidence and latency legs: the tick it was raised at and, where the hazard carries one, the tick it arose.
        private static void Hold(Dictionary<string, object?> p, Clock.HoldEntered held, long? tick)
        {
            p[ClockEventTelemetry.ReasonKey] = held.Reason?.ToString();
            if (held.DetectedTick is { } detected)
            {
                p[ClockEventTelemetry.DetectedTickKey] = detected;
                if (tick is { } at) p["hold_ticks"] = at - detected;
                if (held.OccurrenceTick is { } occurrence) p["detect_ticks"] = detected - occurrence;
            }
            p[ClockEventTelemetry.OccurrenceTickKey] = held.OccurrenceTick;
            switch (held.Evidence)
            {
                case Clock.HoldEvidence.NotificationsCase { Value: var batch }:
                    p["evidence"] = "notifications";
                    p["letters"] = batch.Letters.Select(letter => (object?)LetterMap(letter)).ToList();
                    p["messages"] = batch.Messages.Select(message => (object?)Message(message)).ToList();
                    break;
                case Clock.HoldEvidence.PawnCase { Value: var pawn }:
                    p["evidence"] = "pawn";
                    Pawn(p, pawn);
                    break;
                case Clock.HoldEvidence.InjuryCase { Value: var injury }:
                    p["evidence"] = "injury";
                    Injury(p, injury);
                    break;
                case Clock.HoldEvidence.HealthCase { Value: var health }:
                    p["evidence"] = "health";
                    if (health.Pawn is { } who) Pawn(p, who);
                    p["min_health_fraction"] = health.MinHealthFraction;
                    p["health_drop_fraction"] = health.HealthDropFraction;
                    break;
                case Clock.HoldEvidence.PauseCase { Value: var pause }:
                    p["evidence"] = "pause";
                    Pause(p, pause);
                    break;
                case Clock.HoldEvidence.UnavailableCase { Value: var unavailable }:
                    p["evidence"] = "unavailable";
                    p["unavailable_reason"] = unavailable.Reason?.ToString();
                    p["unavailable_detail"] = unavailable.Detail;
                    break;
                case Clock.HoldEvidence.CombatCase { Value: var combat }:
                    p["evidence"] = "combat";
                    p["combat_event"] = combat.Event?.ToString();
                    p["thing"] = combat.ThingId;
                    p["combat_reason"] = combat.Reason;
                    break;
            }
        }

        private static void Pause(Dictionary<string, object?> p, Clock.PauseEvidence pause)
        {
            p["actual_paused"] = pause.ActualPaused;
            p["pause_verified"] = pause.PauseVerified;
            if (pause.Letter is { } letter) Letter(p, letter, "letter_");
            if (pause.Dialog is { } dialog)
            {
                p["dialog_window_id"] = dialog.WindowId;
                p["dialog_window_type"] = dialog.WindowType;
                p["dialog_title"] = dialog.Title;
            }
        }

        private static void Letter(Dictionary<string, object?> p, Clock.Letter letter, string prefix)
        {
            foreach (var (key, field) in LetterMap(letter)) p[prefix + key] = field;
        }

        private static Dictionary<string, object?> LetterMap(Clock.Letter letter) =>
            new() { ["id"] = letter.Id, ["label"] = letter.Label, ["def"] = letter.DefName };

        private static Dictionary<string, object?> Message(Clock.TransientMessage message) =>
            new() { ["id"] = message.Id, ["text"] = message.Text };

        private static void Pawn(Dictionary<string, object?> p, Clock.PawnEvent pawn)
        {
            p["pawn"] = pawn.PawnId;
            p["pawn_name"] = pawn.Name;
            if (pawn.Position is { } cell) { p["x"] = cell.X; p["z"] = cell.Z; }
            if (pawn.Reason is { } reason) p["pawn_reason"] = reason;
        }

        private static void Injury(Dictionary<string, object?> p, Clock.Injury injury)
        {
            if (injury.Pawn is { } pawn) Pawn(p, pawn);
            if (injury.Before is { } before) p["before"] = Health(before);
            if (injury.After is { } after) p["after"] = Health(after);
        }

        private static Dictionary<string, object?> Health(Clock.Health health) => new()
        {
            ["severity"] = health.Severity,
        };

        private static long? Long(ulong? value) => value is { } v ? checked((long)v) : null;
    }
}
