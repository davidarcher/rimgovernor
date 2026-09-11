package bridge

import (
	"context"
	"errors"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// ReadClockEvents preserves journal loss, partial evidence and original event
// contexts. Reading does not acknowledge an interruption or authorize resuming.
// Unix timestamps are diagnostic wall time and need not increase with cursors.
func (client *Client) ReadClockEvents(ctx context.Context, request *k.EventsRequest) (*k.EventsReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("clock events request required")
	}
	request = proto.Clone(request).(*k.EventsRequest)
	if err := errors.Join(clockWire(request), ValidateIdentity(request.Identity)); err != nil {
		return nil, Result{}, err
	}
	if request.AfterCursor == nil || request.GetAfterCursor() < 0 || request.Limit == nil || request.GetLimit() < 1 || request.GetLimit() > 128 {
		return nil, Result{}, contract("clock events cursor/limit")
	}
	reply := &k.EventsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/clock_read_events", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *k.EventsReply_Failure:
		err = failure(v.Failure, raw)
	case *k.EventsReply_Page:
		err = clockEventsPage(v.Page, request)
	default:
		err = contract("clock events outcome missing")
	}
	return reply, raw, err
}

func clockEventsPage(page *k.EventsPage, request *k.EventsRequest) error {
	if page == nil {
		return contract("clock events page required")
	}
	if err := errors.Join(clockWire(page), ValidateContext(page.Context)); err != nil {
		return err
	}
	if !sameIdentity(page.Context.Identity, request.Identity) {
		return contract("clock events page identity mismatch")
	}
	if page.NewestCursor == nil || page.NextCursor == nil || page.Gap == nil || page.LostCount == nil || (page.OldestCursor != nil && page.GetOldestCursor() < 0) || page.GetNewestCursor() < 0 || page.GetNextCursor() < request.GetAfterCursor() || page.GetNextCursor() > page.GetNewestCursor() || page.GetNewestCursor() < request.GetAfterCursor() || len(page.Events) > int(request.GetLimit()) {
		return contract("clock events cursor presence or bounds")
	}
	if page.OldestCursor != nil && (page.GetOldestCursor() > page.GetNewestCursor() || (page.GetOldestCursor() == 0 && page.GetNewestCursor() != 0)) {
		return contract("clock journal retained range")
	}
	// Native scans cursor positions, including missing event files. Subtract
	// validated nonnegative ordered cursors before converting to avoid overflow.
	span := uint64(page.GetNextCursor() - request.GetAfterCursor())
	if span > uint64(request.GetLimit()) || uint64(len(page.Events)) > span || page.GetLostCount() != span-uint64(len(page.Events)) {
		return contract("clock events scanned span/loss mismatch")
	}
	previous := request.GetAfterCursor()
	for _, event := range page.Events {
		if err := clockEvent(event); err != nil {
			return err
		}
		if event.GetCursor() <= previous || (page.OldestCursor != nil && event.GetCursor() < page.GetOldestCursor()) || event.GetCursor() > page.GetNextCursor() {
			return contract("clock events order/range")
		}
		previous = event.GetCursor()
	}
	if page.GetGap() != (page.GetLostCount() > 0) {
		return contract("clock events next cursor or loss mismatch")
	}
	return nil
}

func clockEvent(event *k.Event) error {
	if event == nil {
		return contract("clock event required")
	}
	if err := errors.Join(clockOwner(event.Owner), ValidateContext(event.Context)); err != nil {
		return err
	}
	if event.Cursor == nil || event.GetCursor() <= 0 || event.ObservedAtUnixMs == nil || event.GetObservedAtUnixMs() < 0 || !diagnostic(event.Detail) {
		return contract("clock event header")
	}
	switch v := event.Event.(type) {
	case *k.Event_Started:
		if v.Started == nil {
			return contract("clock started required")
		}
		if err := clockEpoch(v.Started.Epoch); err != nil {
			return err
		}
		if !proto.Equal(v.Started.Epoch.Owner, event.Owner) || !proto.Equal(v.Started.Epoch.Origin, event.Context) {
			return contract("clock started origin mismatch")
		}
	case *k.Event_SpeedChanged:
		if v.SpeedChanged == nil || v.SpeedChanged.Speed == nil {
			return contract("clock speed evidence required")
		}
	case *k.Event_Stopped:
		return clockStopEvent(v.Stopped)
	case *k.Event_Notification:
		return clockNotification(v.Notification)
	case *k.Event_Alert:
		return clockAlert(v.Alert)
	case *k.Event_InjuryObserved:
		return clockInjury(v.InjuryObserved)
	case *k.Event_HostilesCleared:
		if v.HostilesCleared == nil {
			return contract("clock hostiles-cleared required")
		}
		h := v.HostilesCleared
		if h.ConsciousHostilesBefore != nil && h.GetConsciousHostilesBefore() < 0 {
			return contract("clock hostile count")
		}
		if err := clockEventCompleteness(h.Completeness); err != nil {
			return err
		}
		for _, rows := range [][]*k.PawnEvent{h.DownedHostiles, h.DraftedColonists} {
			seen := map[string]bool{}
			for _, pawn := range rows {
				if err := clockPawn(pawn); err != nil {
					return err
				}
				if seen[pawn.GetPawnId()] {
					return contract("duplicate clock pawn evidence")
				}
				seen[pawn.GetPawnId()] = true
			}
		}
	case *k.Event_PauseFailed:
		if v.PauseFailed == nil {
			return contract("clock pause failure required")
		}
		return clockStopEvent(v.PauseFailed.Pending)
	case *k.Event_ForcePauseWaiting:
		if v.ForcePauseWaiting == nil {
			return contract("clock force pause required")
		}
		return clockPauseEvidence(v.ForcePauseWaiting.Pause)
	case *k.Event_ForcePauseCleared:
		if v.ForcePauseCleared == nil || !diagnostic(v.ForcePauseCleared.ForcePauseKind) {
			return contract("clock force pause clear evidence")
		}
	default:
		return contract("clock event variant missing")
	}
	return nil
}
func clockEventCompleteness(page *c.PageInfo) error {
	if page == nil || page.Complete == nil || (page.GetComplete() && page.NextCursor != nil) || (page.NextCursor != nil && validID(page.GetNextCursor()) != nil) {
		return contract("clock collection completeness required")
	}
	return nil
}
func clockPawn(pawn *k.PawnEvent) error {
	if pawn == nil || validID(pawn.GetPawnId()) != nil || !diagnostic(pawn.Name) || !diagnostic(pawn.Reason) {
		return contract("clock pawn evidence")
	}
	if p := pawn.Position; p != nil && (p.X == nil || p.Z == nil || p.GetX() < 0 || p.GetZ() < 0) {
		return contract("clock pawn position")
	}
	return nil
}
func clockAlert(v *k.Alert) error {
	if v == nil || validID(v.GetKey()) != nil || !diagnostic(v.Label) || !diagnostic(v.Priority) {
		return contract("clock alert evidence")
	}
	return nil
}
func clockLetter(v *k.Letter) error {
	if v == nil || validID(v.GetId()) != nil || !diagnostic(v.Label) || (v.DefName != nil && validID(v.GetDefName()) != nil) {
		return contract("clock letter evidence")
	}
	return nil
}
func clockNotification(v *k.Notification) error {
	if v == nil {
		return contract("clock notification required")
	}
	switch n := v.Source.(type) {
	case *k.Notification_Letter:
		return clockLetter(n.Letter)
	case *k.Notification_Message:
		return clockMessage(n.Message)
	default:
		return contract("clock notification source required")
	}
}
func clockMessage(v *k.TransientMessage) error {
	if v == nil || validID(v.GetId()) != nil || !diagnostic(v.Text) || (v.TypeDef != nil && validID(v.GetTypeDef()) != nil) || (v.StartingTick != nil && v.GetStartingTick() < 0) {
		return contract("clock message evidence")
	}
	return nil
}
func clockHealth(h *k.Health) error {
	if h == nil {
		return nil
	}
	if (h.InjuryCount != nil && h.GetInjuryCount() < 0) || (h.Severity != nil && h.GetSeverity() < 0) || (h.BleedRate != nil && h.GetBleedRate() < 0) || (h.BloodLoss != nil && (h.GetBloodLoss() < 0 || h.GetBloodLoss() > 1)) || (h.SummaryHealth != nil && (h.GetSummaryHealth() < 0 || h.GetSummaryHealth() > 1)) {
		return contract("clock health evidence bounds")
	}
	return nil
}
func clockInjury(v *k.Injury) error {
	if v == nil {
		return contract("clock injury required")
	}
	return errors.Join(clockPawn(v.Pawn), clockHealth(v.Before), clockHealth(v.After))
}
func clockPauseEvidence(v *k.PauseEvidence) error {
	if v == nil {
		return contract("clock pause evidence required")
	}
	seen := map[string]bool{}
	for _, id := range v.ForcePausingWindowIds {
		if validID(id) != nil || seen[id] {
			return contract("clock pause window IDs")
		}
		seen[id] = true
	}
	if v.Letter != nil {
		return clockLetter(v.Letter)
	}
	return nil
}
func clockStopEvent(v *k.StopEvent) error {
	if v == nil || v.Reason == nil {
		return contract("clock stop reason required")
	}
	switch e := v.Evidence.(type) {
	case *k.StopEvent_Notifications:
		if e.Notifications == nil {
			return contract("clock notifications required")
		}
		if err := clockEventCompleteness(e.Notifications.Completeness); err != nil {
			return err
		}
		for _, letter := range e.Notifications.Letters {
			if err := clockLetter(letter); err != nil {
				return err
			}
		}
		for _, message := range e.Notifications.Messages {
			if err := clockMessage(message); err != nil {
				return err
			}
		}
	case *k.StopEvent_Pawn:
		return clockPawn(e.Pawn)
	case *k.StopEvent_Injury:
		return clockInjury(e.Injury)
	case *k.StopEvent_Health:
		if e.Health == nil {
			return contract("clock health threshold required")
		}
		if err := clockPawn(e.Health.Pawn); err != nil {
			return err
		}
		for _, value := range []*float32{e.Health.HealthAtStart, e.Health.HealthNow, e.Health.MinHealthFraction, e.Health.HealthDropFraction} {
			if value != nil && (*value < 0 || *value > 1) {
				return contract("clock health fraction bounds")
			}
		}
	case *k.StopEvent_Budget:
		b := e.Budget
		if b == nil || b.StartTick == nil || b.TickDeadline == nil || b.ActualTick == nil || b.GetStartTick() < 0 || b.GetTickDeadline() <= b.GetStartTick() || b.GetTickDeadline()-b.GetStartTick() > 1800000 || b.GetActualTick() < b.GetTickDeadline() {
			return contract("clock tick budget evidence")
		}
	case *k.StopEvent_Pause:
		return clockPauseEvidence(e.Pause)
	case *k.StopEvent_Unavailable:
		return validateUnavailable(e.Unavailable)
	default:
		return contract("clock stop evidence required")
	}
	return nil
}
