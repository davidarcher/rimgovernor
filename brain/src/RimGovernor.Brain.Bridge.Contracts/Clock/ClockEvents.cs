using RimGovernor.Contracts.Wire.Clock;
using RimGovernor.Contracts.Wire.Common;
using ClockHoldEvidence = RimGovernor.Contracts.Wire.Clock.HoldEvidence;

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
        ValidateContext(page.Context);
        if (!SameIdentity(page.Context?.Identity, request.Identity))
        {
            throw Contract("clock events page identity mismatch");
        }
        if (page.NewestCursor is null
            || page.NextCursor is null
            || page.Gap is null
            || page.LostCount is null
            || page.NewestCursor.GetValueOrDefault() < 0
            || page.NextCursor.GetValueOrDefault() < request.AfterCursor.GetValueOrDefault()
            || page.NextCursor.GetValueOrDefault() > page.NewestCursor.GetValueOrDefault()
            || page.NewestCursor.GetValueOrDefault() < request.AfterCursor.GetValueOrDefault()
            || page.Events.Count > (int)request.Limit.GetValueOrDefault())
        {
            throw Contract("clock events cursor presence or bounds");
        }
        // Native scans cursor positions after the request's cursor, counting evicted events as lost. The cursors are
        // validated nonnegative and ordered, so the subtraction cannot overflow.
        var span = (ulong)(page.NextCursor.GetValueOrDefault() - request.AfterCursor.GetValueOrDefault());
        if (span > request.Limit.GetValueOrDefault() || (ulong)page.Events.Count > span || page.LostCount.GetValueOrDefault() != span - (ulong)page.Events.Count)
        {
            throw Contract("clock events scanned span/loss mismatch");
        }
        var previous = request.AfterCursor.GetValueOrDefault();
        foreach (var @event in page.Events)
        {
            ClockJournalEvent(@event);
            if (@event.Cursor.GetValueOrDefault() <= previous
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
        ValidateContext(@event.Context);
        if (@event.Cursor is null
            || @event.Cursor <= 0
            || @event.ObservedAtUnixMs is null
            || @event.ObservedAtUnixMs < 0)
        {
            throw Contract("clock event header");
        }
        switch (@event.Event)
        {
            case ClockEventEvent.ResumedCase { Value: var resumed }:
                if (resumed.Speed is null || (int)resumed.Speed.GetValueOrDefault() < 1 || (int)resumed.Speed.GetValueOrDefault() > 4 || resumed.From is null)
                {
                    throw Contract("clock resumed evidence required");
                }
                break;
            case ClockEventEvent.HoldEnteredCase { Value: var held }:
                ClockHoldEntered(held);
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
                ClockHoldEntered(pauseFailed.Pending);
                break;
            case ClockEventEvent.ForcePauseWaitingCase { Value: var waiting }:
                ClockPauseEvidence(waiting.Pause);
                break;
            case ClockEventEvent.ForcePauseClearedCase:
                break;
            case ClockEventEvent.AuthorityChangedCase { Value: var authority }:
                if (authority.Generation is null or 0
                    || (authority.PreviousGeneration is not null && authority.PreviousGeneration >= authority.Generation))
                {
                    throw Contract("clock authority change evidence");
                }
                break;
            case ClockEventEvent.ObservationInvalidatedCase { Value: var invalidated }:
                if (invalidated.Families.Count == 0
                    || invalidated.Families.Count > FactFamilies().Count)
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

    private static void ClockPawn(PawnEvent? pawn)
    {
        if (pawn is null || !IsValidId((pawn.PawnId ?? "")))
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
        if (v is null || !IsValidId((v.Key ?? "")))
        {
            throw Contract("clock alert evidence");
        }
    }

    private static void ClockLetter(Letter? v)
    {
        if (v is null || !IsValidId((v.Id ?? "")) || (v.DefName is not null && !IsValidId((v.DefName ?? ""))))
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

    private static void ClockHoldEntered(HoldEntered? v)
    {
        if (v is null || v.Reason is null)
        {
            throw Contract("clock hold reason required");
        }
        switch (v.Evidence)
        {
            case ClockHoldEvidence.NotificationsCase { Value: var notifications }:
                foreach (var letter in notifications.Letters)
                {
                    ClockLetter(letter);
                }
                foreach (var message in notifications.Messages)
                {
                    ClockMessage(message);
                }
                break;
            case ClockHoldEvidence.PawnCase { Value: var pawn }:
                ClockPawn(pawn);
                break;
            case ClockHoldEvidence.InjuryCase { Value: var injury }:
                ClockInjury(injury);
                break;
            case ClockHoldEvidence.HealthCase { Value: var health }:
                ClockPawn(health.Pawn);
                foreach (var value in new[] { health.HealthAtStart, health.HealthNow, health.MinHealthFraction, health.HealthDropFraction })
                {
                    if (value is < 0 or > 1)
                    {
                        throw Contract("clock health fraction bounds");
                    }
                }
                break;
            case ClockHoldEvidence.PauseCase { Value: var pause }:
                ClockPauseEvidence(pause);
                break;
            case ClockHoldEvidence.UnavailableCase { Value: var unavailable }:
                ValidateUnavailable(unavailable);
                break;
            case ClockHoldEvidence.CombatCase { Value: var combat }:
                // An armed combat event: the event is required, the thing and reason are descriptive.
                if ((combat.Event ?? CombatEvent.Unspecified) == CombatEvent.Unspecified)
                {
                    throw Contract("clock combat stop evidence");
                }
                break;
            default:
                throw Contract("clock hold evidence required");
        }
    }
}
