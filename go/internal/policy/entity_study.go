package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// WorkDarkStudy is the Anomaly WorkTypeDef the study work giver issues its
// jobs through (Anomaly Defs/WorkGivers StudyInteract: workType DarkStudy,
// relevant skill Intellectual, from the work type row native reports).
const WorkDarkStudy WorkType = "DarkStudy"

// The study rule: a held entity is studied by the
// game's own WorkGiver_DarkStudyInteract, which offers a job only while
// CompStudiable.CurrentlyStudiable holds (decompile): the thing is ever
// studiable, its study is enabled, the holding target's containment mode is
// Study (CompHoldingPlatformTarget.CanStudy) and, when the comp has a
// frequencyTicks, the interval since lastStudiedTick has passed
// (TicksTilNextStudy <= 0). The interval is therefore the game's and the
// planner reads the result rather than restating it: a DarkStudy owner is
// owed while at least one held entity is currently studiable and not
// between studies. Nothing here forces a job, so the interval cannot be
// broken.

// StudyWork is the DarkStudy requirement the held entities owe the work
// planner: one owner while any held entity is currently studiable, none
// otherwise. A held entity whose held or currently-studiable fact is unread
// makes the whole answer unknown, so the planner waits instead of guessing;
// Reason says which entity and fact. Without Anomaly (entities unknown) no
// entity is held and nothing is owed.
func StudyWork(p ContainmentPlanning) (domain.Fact[[]WorkRequirement], string) {
	none := domain.Known([]WorkRequirement{})
	entities, known := p.Entities.Value()
	if !known {
		return none, ""
	}
	owed := false
	for _, e := range entities {
		if dead, ok := e.Dead.Value(); ok && dead {
			continue
		}
		held, ok := e.Held.Value()
		if !ok {
			return domain.Unknown[[]WorkRequirement](), fmt.Sprintf("whether a platform holds entity %s is unread", e.Pawn)
		}
		if !held {
			continue
		}
		studiable, ok := e.CurrentlyStudiable.Value()
		if !ok {
			return domain.Unknown[[]WorkRequirement](), fmt.Sprintf("whether held entity %s is currently studiable is unread", e.Pawn)
		}
		owed = owed || studiable
	}
	if !owed {
		return none, ""
	}
	return domain.Known([]WorkRequirement{{Work: WorkDarkStudy}}), ""
}
