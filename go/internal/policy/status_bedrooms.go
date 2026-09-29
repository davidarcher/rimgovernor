package policy

import (
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BedroomRows are the status panel's bedroom rows (#1220, epic #1200):
// one headline row per standard bedroom wing ("Bedrooms 7/8, 3x4": rooms
// whose bed has an owner over planned rooms, and the wing's room size),
// then a "Bedrooms" heading and one detail row per planned suite with its
// owner, size and why it was wanted (the owner's room target reasons,
// "space" when none beyond the tier). Rows are keyed by wing and suite
// position, so several wings each get their own. Pure.
func BedroomRows(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, targets map[string]RoomTarget) []StatusRow {
	owners := map[string][]PawnID{}
	for _, b := range sleeping.Beds {
		owners[b.ID] = b.Owners
	}
	owner := func(r LayoutRoom) (string, PawnID, bool) {
		room, ok := PlannedRoomStanding(r, rooms)
		if !ok {
			return "", "", false
		}
		for _, b := range room.Beds {
			if len(owners[b]) > 0 {
				return room.ID, owners[b][0], true
			}
		}
		return room.ID, "", false
	}
	size := func(r LayoutRoom) string { return fmt.Sprintf("%dx%d", r.Interior.Width, r.Interior.Height) }
	var rows, suites []StatusRow
	wing := 0
	for _, w := range plan.Wings {
		switch w.Purpose {
		case WingBedrooms, WingBedroomsRetiring:
			if len(w.Rooms) == 0 {
				continue
			}
			retiring := ""
			if w.Purpose == WingBedroomsRetiring {
				retiring = ", retiring"
			}
			taken := 0
			for _, r := range w.Rooms {
				if _, _, ok := owner(r); ok {
					taken++
				}
			}
			rows = append(rows, StatusRow{Key: fmt.Sprintf("bedrooms.%d", wing), Text: fmt.Sprintf("Bedrooms %d/%d, %s%s", taken, len(w.Rooms), size(w.Rooms[0]), retiring), Severity: StatusInfo, Target: domain.Known(w.Corridor.From)})
			wing++
		case WingSuites:
			for _, r := range w.Rooms {
				id, pawn, ok := owner(r)
				text := "Suite " + size(r) + " vacant"
				if ok {
					text = fmt.Sprintf("Suite %s %s: %s", size(r), pawn, suiteReason(targets[id]))
				}
				centre := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
				suites = append(suites, StatusRow{Key: fmt.Sprintf("suite.%d.%d", r.Interior.X, r.Interior.Z), Text: text, Severity: StatusInfo, Target: domain.Known(centre), Detail: true})
			}
		}
	}
	if len(suites) > 0 {
		rows = append(rows, StatusRow{Key: "domain.Bedrooms", Text: "Bedrooms", Severity: StatusInfo, Detail: true})
		rows = append(rows, suites...)
	}
	return rows
}

// suiteReason names why a suite's owner has one: the target's trait and
// title reasons, else "space".
func suiteReason(t RoomTarget) string {
	var why []string
	for _, r := range t.Reasons {
		if r != "tier" && r != "common" {
			why = append(why, r)
		}
	}
	if len(why) == 0 {
		return "space"
	}
	return strings.Join(why, ", ")
}
