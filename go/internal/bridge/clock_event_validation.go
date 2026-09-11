package bridge

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// ValidateClockEventsPage validates a profile-wide journal page without I/O or
// mutation. The page context matches the requested current world; individual
// events retain their original worlds and epochs. Only explicit native loss
// evidence establishes a gap, not noncontiguous cursor values.
func ValidateClockEventsPage(page *k.EventsPage, request *k.EventsRequest) error {
	if err := clockWire(request); err != nil {
		return err
	}
	if err := ValidateIdentity(request.Identity); err != nil {
		return err
	}
	if request.AfterCursor == nil || request.GetAfterCursor() < 0 || request.Limit == nil || request.GetLimit() < 1 || request.GetLimit() > 128 {
		return contract("clock events cursor/limit")
	}
	return clockEventsPage(page, request)
}

// ValidateClockEvent validates retained typed evidence without acknowledging it
// or granting permission to resume. It does not require the event's original
// world or epoch to be current.
func ValidateClockEvent(event *k.Event) error {
	if err := clockWire(event); err != nil {
		return err
	}
	return clockEvent(event)
}
