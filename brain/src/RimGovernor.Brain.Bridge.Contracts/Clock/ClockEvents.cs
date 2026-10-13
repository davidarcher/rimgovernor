using RimGovernor.Protocol.Clock;
using RimGovernor.Protocol.Common;
using RimGovernor.Protocol.Receipts;

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
        if (!request.HasAfterCursor || request.AfterCursor < 0 || !request.HasLimit || request.Limit < 1 || request.Limit > 128)
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
        if (!SameIdentity(page.Context.Identity, request.Identity))
        {
            throw Contract("clock events page identity mismatch");
        }
        if (!page.HasNewestCursor
            || !page.HasNextCursor
            || !page.HasGap
            || !page.HasLostCount
            || (page.HasOldestCursor && page.OldestCursor < 0)
            || page.NewestCursor < 0
            || page.NextCursor < request.AfterCursor
            || page.NextCursor > page.NewestCursor
            || page.NewestCursor < request.AfterCursor
            || page.Events.Count > (int)request.Limit)
        {
            throw Contract("clock events cursor presence or bounds");
        }
        if (page.HasOldestCursor && (page.OldestCursor > page.NewestCursor + 1 || (page.OldestCursor == 0 && page.NewestCursor != 0)))
        {
            throw Contract("clock journal retained range");
        }
        // oldest_cursor is the native process's first cursor; those below it predate the process, so a read from
        // below starts just before it with no loss. From there native scans cursor positions, counting evicted rows
        // as lost. Subtract validated nonnegative ordered cursors before converting to avoid overflow.
        var start = Math.Max(request.AfterCursor, page.OldestCursor - 1);
        if (page.NextCursor < start)
        {
            throw Contract("clock events cursor presence or bounds");
        }
        var span = (ulong)(page.NextCursor - start);
        if (span > request.Limit || (ulong)page.Events.Count > span || page.LostCount != span - (ulong)page.Events.Count)
        {
            throw Contract("clock events scanned span/loss mismatch");
        }
        var previous = request.AfterCursor;
        foreach (var @event in page.Events)
        {
            ClockEvent(@event);
            if (@event.Cursor <= previous
                || (page.HasOldestCursor && @event.Cursor < page.OldestCursor)
                || @event.Cursor > page.NextCursor)
            {
                throw Contract("clock events order/range");
            }
            previous = @event.Cursor;
        }
        if (page.Gap != page.LostCount > 0)
        {
            throw Contract("clock events next cursor or loss mismatch");
        }
    }

    private static void ClockEvent(Event @event)
    {
        // Only an authority change observed outside any epoch and the native rule events (rules run with or
        // without an epoch) have no owner.
        var ownerless = @event.EventCase is Event.EventOneofCase.AuthorityChanged or Event.EventOneofCase.RuleFired or Event.EventOneofCase.RuleLeaseExpired;
        if (@event.Owner is not null || !ownerless)
        {
            ClockOwner(@event.Owner);
        }
        ValidateContext(@event.Context);
        if (!@event.HasCursor
            || @event.Cursor <= 0
            || !@event.HasObservedAtUnixMs
            || @event.ObservedAtUnixMs < 0
            || !Diagnostic(@event.HasDetail, @event.Detail))
        {
            throw Contract("clock event header");
        }
        switch (@event.EventCase)
        {
            case Event.EventOneofCase.Started:
                var started = @event.Started;
                ClockEpoch(started.Epoch);
                if (!Equals(started.Epoch.Owner, @event.Owner) || !Equals(started.Epoch.Origin, @event.Context))
                {
                    throw Contract("clock started origin mismatch");
                }
                break;
            case Event.EventOneofCase.SpeedChanged:
                var changed = @event.SpeedChanged;
                if (!changed.HasSpeed || changed.MaxTicksPerSecond > 60000 || changed.RegulatedTicksPerSecond > 60000 || changed.BlindTicks < 0)
                {
                    throw Contract("clock speed evidence required");
                }
                break;
            case Event.EventOneofCase.Stopped:
                ClockStopEvent(@event.Stopped);
                break;
            case Event.EventOneofCase.Notification:
                ClockNotification(@event.Notification);
                break;
            case Event.EventOneofCase.Alert:
                ClockAlert(@event.Alert);
                break;
            case Event.EventOneofCase.InjuryObserved:
                ClockInjury(@event.InjuryObserved);
                break;
            case Event.EventOneofCase.HostilesCleared:
                var cleared = @event.HostilesCleared;
                if (cleared.HasConsciousHostilesBefore && cleared.ConsciousHostilesBefore < 0)
                {
                    throw Contract("clock hostile count");
                }
                foreach (var rows in new[] { cleared.DownedHostiles, cleared.DraftedColonists })
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var pawn in rows)
                    {
                        ClockPawn(pawn);
                        if (!seen.Add(pawn.PawnId))
                        {
                            throw Contract("duplicate clock pawn evidence");
                        }
                    }
                }
                break;
            case Event.EventOneofCase.PauseFailed:
                ClockStopEvent(@event.PauseFailed.Pending);
                break;
            case Event.EventOneofCase.ForcePauseWaiting:
                ClockPauseEvidence(@event.ForcePauseWaiting.Pause);
                break;
            case Event.EventOneofCase.ForcePauseCleared:
                break;
            case Event.EventOneofCase.OperationOutcome:
                ClockOperationOutcome(@event.OperationOutcome);
                break;
            case Event.EventOneofCase.AuthorityChanged:
                var authority = @event.AuthorityChanged;
                if (!authority.HasGeneration
                    || authority.Generation == 0
                    || (authority.HasPreviousGeneration && authority.PreviousGeneration >= authority.Generation)
                    || !Diagnostic(authority.HasReason, authority.Reason))
                {
                    throw Contract("clock authority change evidence");
                }
                break;
            case Event.EventOneofCase.RuleFired:
                var rule = @event.RuleFired;
                if (!IsValidId(rule.RuleId)
                    || !IsValidId(rule.Job)
                    || !IsValidId(rule.ActorId)
                    || !IsValidId(rule.TargetId)
                    || !rule.HasTick
                    || rule.Tick < 0)
                {
                    throw Contract("clock rule fired evidence");
                }
                break;
            case Event.EventOneofCase.RuleLeaseExpired:
                var lease = @event.RuleLeaseExpired;
                if (!lease.HasExpiresAtTick || !lease.HasDeactivated)
                {
                    throw Contract("clock rule lease evidence");
                }
                break;
            case Event.EventOneofCase.ObservationInvalidated:
                var invalidated = @event.ObservationInvalidated;
                if (invalidated.Families.Count == 0
                    || invalidated.Families.Count > FactFamilies().Count
                    || !Diagnostic(invalidated.HasReason, invalidated.Reason))
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
            if (cells.Minimum.X > cells.Maximum.X || cells.Minimum.Z > cells.Maximum.Z)
            {
                throw Contract("clock observation invalidation rectangle bounds");
            }
        }
    }

    // Go's validCell (observation geometry): a cell carries both coordinates and neither is negative.
    private static void ValidCell(Cell? cell)
    {
        if (cell is null || !cell.HasX || !cell.HasZ || cell.X < 0 || cell.Z < 0)
        {
            throw Contract("invalid cell");
        }
    }

    // Go's clockAttemptKey: a complete attempt key; unknown fields were already rejected by the enclosing walk.
    private static void ClockAttemptKey(AttemptKey? attempt)
    {
        if (attempt is null
            || !attempt.HasControllerSessionId
            || !attempt.HasActionId
            || !attempt.HasAttemptId
            || attempt.AttemptId == 0
            || !IsValidId(attempt.ControllerSessionId)
            || !IsValidId(attempt.ActionId))
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
        if (!v.HasLatchedTick || v.LatchedTick < 0)
        {
            throw Contract("clock operation outcome tick");
        }
        switch (v.OutcomeCase)
        {
            case OperationOutcome.OutcomeOneofCase.Completed:
                if (v.Completed.Evidence is null || v.Completed.Evidence.EffectCase == EffectEvidence.EffectOneofCase.None)
                {
                    throw Contract("clock completed outcome evidence");
                }
                break;
            case OperationOutcome.OutcomeOneofCase.Unsuccessful:
                var unsuccessful = v.Unsuccessful;
                if (!unsuccessful.HasReason
                    || (int)unsuccessful.Reason < 1
                    || unsuccessful.Reason > UnsuccessfulReason.OutcomeNotAchieved
                    || !Diagnostic(unsuccessful.HasDetail, unsuccessful.Detail))
                {
                    throw Contract("clock unsuccessful outcome evidence");
                }
                break;
            case OperationOutcome.OutcomeOneofCase.Absent:
                if (!v.Absent.HasInspectionToken || !IsValidId(v.Absent.InspectionToken))
                {
                    throw Contract("clock absent outcome evidence");
                }
                break;
            case OperationOutcome.OutcomeOneofCase.Unknown:
                if (!Diagnostic(v.Unknown.HasReason, v.Unknown.Reason))
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
        if (pawn is null || !IsValidId(pawn.PawnId) || !Diagnostic(pawn.HasName, pawn.Name) || !Diagnostic(pawn.HasReason, pawn.Reason))
        {
            throw Contract("clock pawn evidence");
        }
        if (pawn.Position is { } p && (!p.HasX || !p.HasZ || p.X < 0 || p.Z < 0))
        {
            throw Contract("clock pawn position");
        }
    }

    private static void ClockAlert(Alert? v)
    {
        if (v is null || !IsValidId(v.Key) || !Diagnostic(v.HasLabel, v.Label) || !Diagnostic(v.HasPriority, v.Priority))
        {
            throw Contract("clock alert evidence");
        }
    }

    private static void ClockLetter(Letter? v)
    {
        if (v is null || !IsValidId(v.Id) || !Diagnostic(v.HasLabel, v.Label) || (v.HasDefName && !IsValidId(v.DefName)))
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
        switch (v.SourceCase)
        {
            case Notification.SourceOneofCase.Letter:
                ClockLetter(v.Letter);
                break;
            case Notification.SourceOneofCase.Message:
                ClockMessage(v.Message);
                break;
            default:
                throw Contract("clock notification source required");
        }
    }

    private static void ClockMessage(TransientMessage? v)
    {
        if (v is null
            || !IsValidId(v.Id)
            || !Diagnostic(v.HasText, v.Text)
            || (v.HasTypeDef && !IsValidId(v.TypeDef))
            || (v.HasStartingTick && v.StartingTick < 0))
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
        if (v is null || !v.HasReason)
        {
            throw Contract("clock stop reason required");
        }
        switch (v.EvidenceCase)
        {
            case StopEvent.EvidenceOneofCase.Notifications:
                foreach (var letter in v.Notifications.Letters)
                {
                    ClockLetter(letter);
                }
                foreach (var message in v.Notifications.Messages)
                {
                    ClockMessage(message);
                }
                break;
            case StopEvent.EvidenceOneofCase.Pawn:
                ClockPawn(v.Pawn);
                break;
            case StopEvent.EvidenceOneofCase.Injury:
                ClockInjury(v.Injury);
                break;
            case StopEvent.EvidenceOneofCase.Health:
                var health = v.Health;
                ClockPawn(health.Pawn);
                foreach (var value in new float?[]
                {
                    health.HasHealthAtStart ? health.HealthAtStart : null,
                    health.HasHealthNow ? health.HealthNow : null,
                    health.HasMinHealthFraction ? health.MinHealthFraction : null,
                    health.HasHealthDropFraction ? health.HealthDropFraction : null,
                })
                {
                    if (value is < 0 or > 1)
                    {
                        throw Contract("clock health fraction bounds");
                    }
                }
                break;
            case StopEvent.EvidenceOneofCase.Budget:
                var b = v.Budget;
                if (!b.HasStartTick
                    || !b.HasTickDeadline
                    || !b.HasActualTick
                    || b.StartTick < 0
                    || b.TickDeadline <= b.StartTick
                    || b.TickDeadline - b.StartTick > 1800000
                    || b.ActualTick < b.TickDeadline)
                {
                    throw Contract("clock tick budget evidence");
                }
                break;
            case StopEvent.EvidenceOneofCase.Pause:
                ClockPauseEvidence(v.Pause);
                break;
            case StopEvent.EvidenceOneofCase.Unavailable:
                ValidateUnavailable(v.Unavailable);
                break;
            case StopEvent.EvidenceOneofCase.Watch:
                if (!v.Watch.HasTickDeadline || v.Watch.TickDeadline <= 0)
                {
                    throw Contract("clock watch stop evidence");
                }
                ClockOperationOutcome(v.Watch.Outcome);
                break;
            case StopEvent.EvidenceOneofCase.Combat:
                // An armed combat event: the event is required, the thing and reason are descriptive.
                if (v.Combat.Event == CombatEvent.Unspecified)
                {
                    throw Contract("clock combat stop evidence");
                }
                break;
            default:
                throw Contract("clock stop evidence required");
        }
    }
}
