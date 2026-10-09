package buildingruntime

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// layout/ring (#1271): the expansion-phase MaintainHousing step that raised
// the Masonry capacity ring, recorded from `acceptance run layout/ring`
// (tick 15, issue-1271 branch on 8130fd810). The ring is one stuff throughout, the
// one the shell style picks from the recorded stock, apart from its Door at the
// planned door cell, exactly the planned room's walls, its door onto a spine
// hallway.
func TestLayoutRingStepIsMasonry(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r := loadRecorded(t, "layout-ring-review")
	if r.Review.Latches.Housing != policy.HousingExpansion {
		t.Fatalf("housing phase %q, want expansion", r.Review.Latches.Housing)
	}
	step := loadStep(t, "layout-ring-step", policy.MaintainHousing)
	facts := step.Projection
	// The recording predates the shelter plan role (#2037): its starter room
	// is a barracks. The shelter stands on that same slot now, so the replay
	// relabels it rather than hand-editing the recording.
	recorded, _ := facts.LayoutPlan.Value()
	recorded.Rooms = slices.Clone(recorded.Rooms)
	for i, room := range recorded.Rooms {
		if room.Role == "barracks" {
			recorded.Rooms[i].Role = policy.PlannedShelter
			break
		}
	}
	facts.LayoutPlan = domain.Known(recorded)
	if tier := styleTier(facts); tier != policy.TechTierMasonry {
		t.Fatalf("tech tier %v, want Masonry", tier)
	}
	var room policy.PlannedRoom
	for _, candidate := range recorded.Rooms {
		if candidate.Role == policy.PlannedShelter {
			room = candidate
			break
		}
	}
	shell, err := room.Footprint()
	if err != nil {
		t.Fatalf("no planned room for the capacity ring: %v", err)
	}
	placements := shell.StyledPlacements(shellStyle(facts))
	if len(placements) != len(shell.Walls()) {
		t.Fatalf("%d placements for %d planned walls", len(placements), len(shell.Walls()))
	}
	walls := map[any]bool{}
	for _, w := range shell.Walls() {
		walls[w] = true
	}
	stuffs := map[string]bool{}
	for _, b := range placements {
		if !walls[b.Cell()] {
			t.Fatalf("ring cell %v lies off the planned room's walls", b.Cell())
		}
		if b.Cell() == room.Door {
			// The door takes its stuff from the door def's stats (wood opens fastest), not the wall's.
			if b.Definition() != "Door" {
				t.Fatalf("door cell %v holds %s, want a Door", room.Door, b.Definition())
			}
			continue
		}
		// The wall stuff is the stock's call (#2127): this recording holds 400
		// slate blocks, 80 walls' worth, short of the 200-wall shell budget, and
		// wood is plentiful, so the ring is wood. Stone winning once the stock
		// covers a shell is TestShellStyleFollowsTheStock's claim.
		if want := shellStyle(facts).WallStuff(domain.ShellRun); b.Stuff() != want {
			t.Fatalf("%s at %v is %s, want the shell style's %s", b.Definition(), b.Cell(), b.Stuff(), want)
		}
		stuffs[b.Stuff()] = true
	}
	if len(stuffs) != 1 {
		t.Fatalf("ring mixes stuffs %v", stuffs)
	}
	if placements[0].Cell() != room.Door || placements[0].Definition() != "Door" {
		t.Fatalf("ring door %s at %v, planned door %v", placements[0].Definition(), placements[0].Cell(), room.Door)
	}
	plan, _ := facts.LayoutPlan.Value()
	threshold, half := shell.Threshold(), policy.SpineWidth/2
	for _, s := range plan.Hallways() {
		if threshold.X >= min(s.From.X, s.To.X)-half && threshold.X <= max(s.From.X, s.To.X)+half &&
			threshold.Z >= min(s.From.Z, s.To.Z)-half && threshold.Z <= max(s.From.Z, s.To.Z)+half {
			return
		}
	}
	t.Fatalf("door %v opens onto %v, off every hallway %v", room.Door, threshold, plan.Hallways())
}
