#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Telemetry;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The one OTLP row native emits per colony event (otel-signals.md#clock-and-colony-events), so holds, resumes and
    /// the other events reach telemetry from one source. Pure: it reads only the event, never the game, and the brain's
    /// tests compile it in.
    /// </summary>
    internal static class ColonyEventRows
    {
        internal static void Write(TelemetryEmitter emitter, ColonyEvent value)
        {
            var (kind, payload) = Row(value);
            emitter.EmitRow(kind.Name, new RowContext { Component = ColonyEventTelemetry.Component, Tick = value.Origin.Tick }, payload);
        }

        /// <summary>The event's kind and payload.</summary>
        internal static (TelemetryKind Kind, Dictionary<string, object?> Payload) Row(ColonyEvent value)
        {
            var origin = value.Origin;
            var p = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["msg"] = value.Detail,
                [ColonyEventTelemetry.ObservedAtKey] = origin.ObservedAtUnixMs,
                [ColonyEventTelemetry.TickKey] = origin.Tick,
                ["colony"] = origin.ColonyId,
                ["load"] = origin.LoadToken,
                ["map"] = origin.MapId,
            };
            switch (value)
            {
                case HoldEntered held:
                    Hold(p, held);
                    return (TelemetryKinds.ClockHold, p);
                case Resumed resumed:
                    p[ColonyEventTelemetry.FromKey] = resumed.From.ToString();
                    p["speed"] = resumed.Speed.ToString();
                    return (TelemetryKinds.ClockResume, p);
                case AuthorityChanged changed:
                    p["generation"] = checked((long)changed.Generation);
                    p[ColonyEventTelemetry.ReasonKey] = changed.Reason;
                    p["active"] = changed.Active;
                    return (TelemetryKinds.ClockAuthority, p);
                case AlertRaised alert:
                    p["key"] = alert.Key;
                    p["label"] = alert.Label;
                    p["priority"] = alert.Priority;
                    return (TelemetryKinds.ClockAlert, p);
                case LetterNotified letter:
                    p["source"] = "letter";
                    Letter(p, letter.Letter, "");
                    return (TelemetryKinds.ClockNotification, p);
                case MessageNotified message:
                    p["source"] = "message";
                    p["id"] = message.Id;
                    p["text"] = message.Text;
                    return (TelemetryKinds.ClockNotification, p);
                case InjuryObserved injury:
                    Injury(p, injury.Injury);
                    return (TelemetryKinds.ClockInjury, p);
                case HostilesCleared:
                    return (TelemetryKinds.ClockHostilesCleared, p);
                case ObservationInvalidated invalidated:
                    p["families"] = invalidated.Families.Select(family => (object?)family.ToString()).ToList();
                    p[ColonyEventTelemetry.ReasonKey] = invalidated.Reason;
                    if (invalidated.EntityIds.Count > 0) p["entity_ids"] = invalidated.EntityIds.Select(id => (object?)id).ToList();
                    if (invalidated.Cells is { } cells)
                        p["cells"] = new Dictionary<string, object?> { ["min_x"] = cells.MinX, ["min_z"] = cells.MinZ, ["max_x"] = cells.MaxX, ["max_z"] = cells.MaxZ };
                    return (TelemetryKinds.ClockInvalidation, p);
                case ForcePauseWaiting:
                    p["state"] = "waiting";
                    return (TelemetryKinds.ClockLongEvent, p);
                case ForcePauseCleared:
                    p["state"] = "cleared";
                    return (TelemetryKinds.ClockLongEvent, p);
                case PauseFailed failed:
                    p[ColonyEventTelemetry.ReasonKey] = failed.Pending.ToString();
                    return (TelemetryKinds.ClockPauseFailed, p);
                default:
                    throw new InvalidOperationException("Unknown colony event " + value.GetType().Name);
            }
        }

        // A hold's reason, evidence, stamp and latency legs: the tick it was raised at and, where the hazard carries one, the tick it arose.
        private static void Hold(Dictionary<string, object?> p, HoldEntered held)
        {
            p[ColonyEventTelemetry.ReasonKey] = held.Reason.ToString();
            p[ColonyEventTelemetry.StopAtMsKey] = held.StopAtUnixMs;
            p[ColonyEventTelemetry.DetectedTickKey] = held.DetectedTick;
            p["hold_ticks"] = held.Origin.Tick - held.DetectedTick;
            if (held.OccurrenceTick is { } occurrence) p["detect_ticks"] = held.DetectedTick - occurrence;
            p[ColonyEventTelemetry.OccurrenceTickKey] = held.OccurrenceTick;
            switch (held.Evidence)
            {
                case NotificationEvidence batch:
                    p["evidence"] = "notifications";
                    p["letters"] = batch.Letters.Select(letter => (object?)LetterMap(letter)).ToList();
                    break;
                case PawnEvidence pawn:
                    p["evidence"] = "pawn";
                    Pawn(p, pawn.Pawn);
                    break;
                case InjuryEvidence injury:
                    p["evidence"] = "injury";
                    Injury(p, injury.Injury);
                    break;
                case HealthEvidence health:
                    p["evidence"] = "health";
                    Pawn(p, health.Pawn);
                    p["min_health_fraction"] = health.MinHealthFraction;
                    p["health_drop_fraction"] = health.HealthDropFraction;
                    break;
                case PauseEvidence pause:
                    p["evidence"] = "pause";
                    p["actual_paused"] = pause.ActualPaused;
                    p["pause_verified"] = pause.PauseVerified;
                    if (pause.Letter is { } letter) Letter(p, letter, "letter_");
                    if (pause.DialogTitle is { } title) p["dialog_title"] = title;
                    break;
                case UnobservedEvidence:
                    p["evidence"] = "unavailable";
                    break;
                case CombatEvidence combat:
                    p["evidence"] = "combat";
                    p["combat_event"] = combat.Event.ToString();
                    p["thing"] = combat.ThingId;
                    p["combat_reason"] = combat.Reason;
                    break;
            }
        }

        private static void Letter(Dictionary<string, object?> p, EventLetter letter, string prefix)
        {
            foreach (var (key, field) in LetterMap(letter)) p[prefix + key] = field;
        }

        private static Dictionary<string, object?> LetterMap(EventLetter letter) =>
            new() { ["id"] = letter.Id, ["label"] = letter.Label, ["def"] = letter.DefName };

        private static void Pawn(Dictionary<string, object?> p, EventPawn pawn)
        {
            p["pawn"] = pawn.PawnId;
            p["pawn_name"] = pawn.Name;
            p["x"] = pawn.X;
            p["z"] = pawn.Z;
            if (pawn.Reason is { } reason) p["pawn_reason"] = reason;
        }

        private static void Injury(Dictionary<string, object?> p, EventInjury injury)
        {
            Pawn(p, injury.Pawn);
            p["before"] = new Dictionary<string, object?> { ["severity"] = injury.SeverityBefore };
            p["after"] = new Dictionary<string, object?> { ["severity"] = injury.SeverityAfter };
        }
    }
}
