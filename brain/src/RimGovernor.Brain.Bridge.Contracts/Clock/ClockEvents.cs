using RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go clock_event_validation.go and clock_events.go: the journal page validators.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>ClockInvalidationEntitiesMax</c>: bounds the entity ids one ObservationInvalidated names; a change
    /// touching more rows than that is sent as the whole family.
    /// </summary>
    public const int ClockInvalidationEntitiesMax = 64;

    /// <summary>
    /// Go's <c>ValidateClockEventsPage</c>: validates a profile-wide journal page without I/O or mutation. The page
    /// context matches the requested current world; individual events retain their original worlds and epochs. Only
    /// explicit native loss evidence establishes a gap, not noncontiguous cursor values.
    /// </summary>
    public static void ValidateClockEventsPage(EventsPage? page, EventsRequest request)
    {
        ClockWire(request);
        ValidateIdentity(request.Identity);
        if (request.AfterCursor is null || request.AfterCursor.GetValueOrDefault() < 0 || request.Limit is null || request.Limit.GetValueOrDefault() < 1 || request.Limit.GetValueOrDefault() > 128)
        {
            throw Contract("clock events cursor/limit");
        }
        ClockEventsPage(page, request);
    }

    /// <summary>Go's <c>clockEventsPage</c>.</summary>
    internal static void ClockEventsPage(EventsPage? page, EventsRequest request)
    {
        if (page is null)
        {
            throw Contract("clock events page required");
        }
        ClockWire(page);
        ValidateContext(page.Context);
        if (!SameIdentity(page.Context?.Identity, request.Identity))
        {
            throw Contract("clock events page identity mismatch");
        }
        if (page.NewestCursor is null
            || page.NextCursor is null
            || page.Gap is null
            || page.LostCount is null
            || (page.OldestCursor is not null && page.OldestCursor.GetValueOrDefault() < 0)
            || page.NewestCursor.GetValueOrDefault() < 0
            || page.NextCursor.GetValueOrDefault() < request.AfterCursor.GetValueOrDefault()
            || page.NextCursor.GetValueOrDefault() > page.NewestCursor.GetValueOrDefault()
            || page.NewestCursor.GetValueOrDefault() < request.AfterCursor.GetValueOrDefault()
            || page.Events.Count > (int)request.Limit.GetValueOrDefault())
        {
            throw Contract("clock events cursor presence or bounds");
        }
        if (page.OldestCursor is not null && (page.OldestCursor.GetValueOrDefault() > page.NewestCursor.GetValueOrDefault() + 1 || (page.OldestCursor.GetValueOrDefault() == 0 && page.NewestCursor.GetValueOrDefault() != 0)))
        {
            throw Contract("clock journal retained range");
        }
        // oldest_cursor is the native process's first cursor; those below it predate the process, so a read from
        // below starts just before it with no loss. From there native scans cursor positions, counting evicted rows
        // as lost. Subtract validated nonnegative ordered cursors before converting to avoid overflow.
        var start = Math.Max(request.AfterCursor.GetValueOrDefault(), page.OldestCursor.GetValueOrDefault() - 1);
        if (page.NextCursor.GetValueOrDefault() < start)
        {
            throw Contract("clock events cursor presence or bounds");
        }
        var span = (ulong)(page.NextCursor.GetValueOrDefault() - start);
        if (span > request.Limit.GetValueOrDefault() || (ulong)page.Events.Count > span || page.LostCount.GetValueOrDefault() != span - (ulong)page.Events.Count)
        {
            throw Contract("clock events scanned span/loss mismatch");
        }
        var previous = request.AfterCursor.GetValueOrDefault();
        foreach (var @event in page.Events)
        {
            ClockJournalEvent(@event);
            if (@event.Cursor.GetValueOrDefault() <= previous
                || (page.OldestCursor is not null && @event.Cursor.GetValueOrDefault() < page.OldestCursor.GetValueOrDefault())
                || @event.Cursor.GetValueOrDefault() > page.NextCursor.GetValueOrDefault())
            {
                throw Contract("clock events order/range");
            }
            previous = @event.Cursor.GetValueOrDefault();
        }
        if (page.Gap.GetValueOrDefault() != page.LostCount.GetValueOrDefault() > 0)
        {
            throw Contract("clock events next cursor or loss mismatch");
        }
    }

    private static void ClockJournalEvent(ClockEvent @event)
    {
        // Only an authority change observed outside any epoch and the native rule events (rules run with or
        // without an epoch) have no owner.
        var ownerless = @event.Event is ClockEventEvent.AuthorityChangedCase or ClockEventEvent.RuleFiredCase or ClockEventEvent.RuleLeaseExpiredCase;
        if (@event.Owner is not null || !ownerless)
        {
            ClockOwner(@event.Owner);
        }
        ValidateContext(@event.Context);
        if (@event.Cursor is null
            || @event.Cursor <= 0
            || @event.ObservedAtUnixMs is null
            || @event.ObservedAtUnixMs < 0
            || !Diagnostic(@event.Detail is not null, @event.Detail ?? ""))
        {
            throw Contract("clock event header");
        }
        switch (@event.Event)
        {
            case ClockEventEvent.StartedCase { Value: var started }:
                ClockEpoch(started.Epoch);
                if (!Equals(started.Epoch?.Owner, @event.Owner) || !Equals(started.Epoch?.Origin, @event.Context))
                {
                    throw Contract("clock started origin mismatch");
                }
                break;
            case ClockEventEvent.SpeedChangedCase { Value: var changed }:
                if (changed.Speed is null || changed.MaxTicksPerSecond > 60000 || changed.RegulatedTicksPerSecond > 60000 || changed.BlindTicks < 0)
                {
                    throw Contract("clock speed evidence required");
                }
                break;
            case ClockEventEvent.StoppedCase { Value: var stopped }:
                ClockStopEvent(stopped);
                break;
            case ClockEventEvent.NotificationCase { Value: var notification }:
                ClockNotification(notification);
                break;
            case ClockEventEvent.AlertCase { Value: var alert }:
                ClockAlert(alert);
                break;
            case ClockEventEvent.InjuryObservedCase { Value: var injury }:
                ClockInjury(injury);
                break;
            case ClockEventEvent.HostilesClearedCase { Value: var cleared }:
                if (cleared.ConsciousHostilesBefore < 0)
                {
                    throw Contract("clock hostile count");
                }
                foreach (var rows in new[] { cleared.DownedHostiles, cleared.DraftedColonists })
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var pawn in rows)
                    {
                        ClockPawn(pawn);
                        if (!seen.Add(pawn.PawnId ?? ""))
                        {
                            throw Contract("duplicate clock pawn evidence");
                        }
                    }
                }
                break;
            case ClockEventEvent.PauseFailedCase { Value: var pauseFailed }:
                ClockStopEvent(pauseFailed.Pending);
                break;
            case ClockEventEvent.ForcePauseWaitingCase { Value: var waiting }:
                ClockPauseEvidence(waiting.Pause);
                break;
            case ClockEventEvent.ForcePauseClearedCase:
                break;
            case ClockEventEvent.OperationOutcomeCase { Value: var outcome }:
                ClockOperationOutcome(outcome);
                break;
            case ClockEventEvent.AuthorityChangedCase { Value: var authority }:
                if (authority.Generation is null or 0
                    || (authority.PreviousGeneration is not null && authority.PreviousGeneration >= authority.Generation)
                    || !Diagnostic(authority.Reason is not null, authority.Reason ?? ""))
                {
                    throw Contract("clock authority change evidence");
                }
                break;
            case ClockEventEvent.RuleFiredCase { Value: var rule }:
                if (!IsValidId(rule.RuleId ?? "")
                    || !IsValidId(rule.Job ?? "")
                    || !IsValidId(rule.ActorId ?? "")
                    || !IsValidId(rule.TargetId ?? "")
                    || rule.Tick is null
                    || rule.Tick < 0)
                {
                    throw Contract("clock rule fired evidence");
                }
                break;
            case ClockEventEvent.RuleLeaseExpiredCase { Value: var lease }:
                if (lease.ExpiresAtTick is null || lease.Deactivated is null)
                {
                    throw Contract("clock rule lease evidence");
                }
                break;
            case ClockEventEvent.ObservationInvalidatedCase { Value: var invalidated }:
                if (invalidated.Families.Count == 0
                    || invalidated.Families.Count > FactFamilies().Count
                    || !Diagnostic(invalidated.Reason is not null, invalidated.Reason ?? ""))
                {
                    throw Contract("clock observation invalidation evidence");
                }
                foreach (var family in invalidated.Families)
                {
                    if (!FactFamilyFromWire(family, out _))
                    {
                        throw Contract("clock observation invalidation family");
                    }
                }
                ClockInvalidationScope(invalidated);
                break;
            default:
                throw Contract("clock event variant missing");
        }
    }

    /// <summary>
    /// Go's <c>clockInvalidationScope</c>: accepts the optional narrowing of an ObservationInvalidated: distinct
    /// entity ids and one rectangle of inclusive cell bounds. An old native omits both.
    /// </summary>
    private static void ClockInvalidationScope(ObservationInvalidated o)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in o.EntityIds)
        {
            if (!IsValidId(id) || !seen.Add(id))
            {
                throw Contract("clock observation invalidation entity id");
            }
        }
        if (o.Cells is { } cells)
        {
            ValidCell(cells.Minimum);
            ValidCell(cells.Maximum);
            if ((cells.Minimum?.X ?? 0) > (cells.Maximum?.X ?? 0) || (cells.Minimum?.Z ?? 0) > (cells.Maximum?.Z ?? 0))
            {
                throw Contract("clock observation invalidation rectangle bounds");
            }
        }
    }

    // Go's validCell (observation geometry): a cell carries both coordinates and neither is negative.
    private static void ValidCell(Cell? cell)
    {
        if (cell is null || cell.X is null || cell.Z is null || cell.X.GetValueOrDefault() < 0 || cell.Z.GetValueOrDefault() < 0)
        {
            throw Contract("invalid cell");
        }
    }

    // Go's clockAttemptKey: a complete attempt key; unknown members are rejected by the JSON decoder.
    private static void ClockAttemptKey(AttemptKey? attempt)
    {
        if (attempt is null
            || attempt.ControllerSessionId is null
            || attempt.ActionId is null
            || attempt.AttemptId is null
            || attempt.AttemptId.GetValueOrDefault() == 0
            || !IsValidId((attempt.ControllerSessionId ?? ""))
            || !IsValidId((attempt.ActionId ?? "")))
        {
            throw Contract("clock attempt key presence");
        }
    }

    // Go's clockOperationOutcome: accepts a terminal outcome of a watched attempt. The effect evidence itself is
    // family specific, so only the shape is checked.
    private static void ClockOperationOutcome(OperationOutcome? v)
    {
        if (v is null)
        {
            throw Contract("clock operation outcome required");
        }
        ClockAttemptKey(v.Attempt);
        if (v.LatchedTick is null || v.LatchedTick.GetValueOrDefault() < 0)
        {
            throw Contract("clock operation outcome tick");
        }
        switch (v.Outcome)
        {
            case OperationOutcomeOutcome.CompletedCase { Value: var completed }:
                if (completed.Evidence?.Effect is null)
                {
                    throw Contract("clock completed outcome evidence");
                }
                break;
            case OperationOutcomeOutcome.UnsuccessfulCase { Value: var unsuccessful }:
                if (unsuccessful.Reason is null
                    || (int)unsuccessful.Reason.GetValueOrDefault() < 1
                    || unsuccessful.Reason.GetValueOrDefault() > UnsuccessfulReason.OutcomeNotAchieved
                    || !Diagnostic(unsuccessful.Detail is not null, (unsuccessful.Detail ?? "")))
                {
                    throw Contract("clock unsuccessful outcome evidence");
                }
                break;
            case OperationOutcomeOutcome.AbsentCase { Value: var absent }:
                if (absent.InspectionToken is null || !IsValidId(absent.InspectionToken))
                {
                    throw Contract("clock absent outcome evidence");
                }
                break;
            case OperationOutcomeOutcome.UnknownCase { Value: var unknown }:
                if (!Diagnostic(unknown.Reason is not null, unknown.Reason ?? ""))
                {
                    throw Contract("clock unknown outcome evidence");
                }
                break;
            default:
                throw Contract("clock operation outcome variant missing");
        }
    }

    private static void ClockPawn(PawnEvent? pawn)
    {
        if (pawn is null || !IsValidId((pawn.PawnId ?? "")) || !Diagnostic(pawn.Name is not null, (pawn.Name ?? "")) || !Diagnostic(pawn.Reason is not null, (pawn.Reason ?? "")))
        {
            throw Contract("clock pawn evidence");
        }
        if (pawn.Position is { } p && (p.X is null || p.Z is null || p.X.GetValueOrDefault() < 0 || p.Z.GetValueOrDefault() < 0))
        {
            throw Contract("clock pawn position");
        }
    }

    private static void ClockAlert(Alert? v)
    {
        if (v is null || !IsValidId((v.Key ?? "")) || !Diagnostic(v.Label is not null, (v.Label ?? "")) || !Diagnostic(v.Priority is not null, (v.Priority ?? "")))
        {
            throw Contract("clock alert evidence");
        }
    }

    private static void ClockLetter(Letter? v)
    {
        if (v is null || !IsValidId((v.Id ?? "")) || !Diagnostic(v.Label is not null, (v.Label ?? "")) || (v.DefName is not null && !IsValidId((v.DefName ?? ""))))
        {
            throw Contract("clock letter evidence");
        }
    }

    private static void ClockNotification(Notification? v)
    {
        if (v is null)
        {
            throw Contract("clock notification required");
        }
        switch (v.Source)
        {
            case NotificationSource.LetterCase { Value: var letter }:
                ClockLetter(letter);
                break;
            case NotificationSource.MessageCase { Value: var message }:
                ClockMessage(message);
                break;
            default:
                throw Contract("clock notification source required");
        }
    }

    private static void ClockMessage(TransientMessage? v)
    {
        if (v is null
            || !IsValidId((v.Id ?? ""))
            || !Diagnostic(v.Text is not null, (v.Text ?? ""))
            || (v.TypeDef is not null && !IsValidId((v.TypeDef ?? "")))
            || (v.StartingTick is not null && v.StartingTick.GetValueOrDefault() < 0))
        {
            throw Contract("clock message evidence");
        }
    }

    private static void ClockInjury(Injury? v)
    {
        if (v is null)
        {
            throw Contract("clock injury required");
        }
        ClockPawn(v.Pawn);
        foreach (var h in new[] { v.Before, v.After })
        {
            if (h is not null && !HealthInBounds(h))
            {
                throw Contract("clock health evidence bounds");
            }
        }
    }

    private static void ClockPauseEvidence(PauseEvidence? v)
    {
        if (v is null)
        {
            throw Contract("clock pause evidence required");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in v.ForcePausingWindowIds)
        {
            if (!IsValidId(id) || !seen.Add(id))
            {
                throw Contract("clock pause window IDs");
            }
        }
        if (v.Letter is not null)
        {
            ClockLetter(v.Letter);
        }
    }

    private static void ClockStopEvent(StopEvent? v)
    {
        if (v is null || v.Reason is null)
        {
            throw Contract("clock stop reason required");
        }
        switch (v.Evidence)
        {
            case StopEventEvidence.NotificationsCase { Value: var notifications }:
                foreach (var letter in notifications.Letters)
                {
                    ClockLetter(letter);
                }
                foreach (var message in notifications.Messages)
                {
                    ClockMessage(message);
                }
                break;
            case StopEventEvidence.PawnCase { Value: var pawn }:
                ClockPawn(pawn);
                break;
            case StopEventEvidence.InjuryCase { Value: var injury }:
                ClockInjury(injury);
                break;
            case StopEventEvidence.HealthCase { Value: var health }:
                ClockPawn(health.Pawn);
                foreach (var value in new[] { health.HealthAtStart, health.HealthNow, health.MinHealthFraction, health.HealthDropFraction })
                {
                    if (value is < 0 or > 1)
                    {
                        throw Contract("clock health fraction bounds");
                    }
                }
                break;
            case StopEventEvidence.BudgetCase { Value: var b }:
                if (b.StartTick is null
                    || b.TickDeadline is null
                    || b.ActualTick is null
                    || b.StartTick.GetValueOrDefault() < 0
                    || b.TickDeadline.GetValueOrDefault() <= b.StartTick.GetValueOrDefault()
                    || b.TickDeadline.GetValueOrDefault() - b.StartTick.GetValueOrDefault() > 1800000
                    || b.ActualTick.GetValueOrDefault() < b.TickDeadline.GetValueOrDefault())
                {
                    throw Contract("clock tick budget evidence");
                }
                break;
            case StopEventEvidence.PauseCase { Value: var pause }:
                ClockPauseEvidence(pause);
                break;
            case StopEventEvidence.UnavailableCase { Value: var unavailable }:
                ValidateUnavailable(unavailable);
                break;
            case StopEventEvidence.WatchCase { Value: var watch }:
                if (watch.TickDeadline is null || watch.TickDeadline <= 0)
                {
                    throw Contract("clock watch stop evidence");
                }
                ClockOperationOutcome(watch.Outcome);
                break;
            case StopEventEvidence.CombatCase { Value: var combat }:
                // An armed combat event: the event is required, the thing and reason are descriptive.
                if ((combat.Event ?? CombatEvent.Unspecified) == CombatEvent.Unspecified)
                {
                    throw Contract("clock combat stop evidence");
                }
                break;
            default:
                throw Contract("clock stop evidence required");
        }
    }
}
