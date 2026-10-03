package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EnsureResearch opening slice: pure, native-shape-preserving primitives
// (prerequisite queue, eligible researchers, usable laboratories). No
// domain/store/executor/bridge/buildingruntime wiring exists
// yet for this goal — these functions only make the
// deterministic method-selection logic available and independently testable ahead
// of that wiring, mirroring how 05.5's GearReplace slice started from policy alone.

const (
	// ResearchQueueMax bounds the returned prerequisite queue -- the native
	// research UI cannot usefully preview more.
	ResearchQueueMax = 8
	// researchVisitMax bounds prerequisite-graph inspection, to reject
	// pathological/cyclic native data instead of
	// hanging on it.
	researchVisitMax = 128
)

// ResearchProjectID names a native ResearchProjectDef by defName.
type ResearchProjectID string

// ResearchProjectFacts mirrors the native research snapshot fields
// prerequisite_queue inspects for one project. Hidden/knowledge-category
// projects and anything with unknown prerequisite lists can never be treated
// as an ordinary, selectable prerequisite.
type ResearchProjectFacts struct {
	Name                ResearchProjectID
	Hidden              domain.Fact[bool]
	KnowledgeCategory   string
	Prerequisites       domain.Fact[[]ResearchProjectID]
	HiddenPrerequisites domain.Fact[[]ResearchProjectID]
	// RequiredBuilding is the native requiredResearchBuilding defName, empty
	// when the project names none; LockReasons are the native census's
	// CanStartNow predicates the project fails (prerequisite:<name>,
	// techprints, ResearchLockBench, ...), empty when it can start now.
	RequiredBuilding string
	LockReasons      []string
}

// ResearchLockBench is the native lock reason for a project whose required
// research bench (or a facility on it) the colony does not have; native
// SelectResearch refuses the project while it holds.
const ResearchLockBench = "research_building_or_facilities"

// ResearchBenchNeeded reports whether the bench lock is the only thing
// keeping the project from starting: its prerequisites are done and no
// other native requirement holds, so building the bench is what unlocks the
// selection (#254). A project locked for any other reason is not a bench
// need; the queue owes it a prerequisite first.
func ResearchBenchNeeded(project ResearchProjectFacts) bool {
	needed := false
	for _, reason := range project.LockReasons {
		if reason != ResearchLockBench {
			return false
		}
		needed = true
	}
	return needed
}

// ResearchPrerequisiteQueue topologically orders the native prerequisites of
// targets, rejecting incomplete or cyclic project graphs rather than guessing
// at a native project it cannot fully inspect. finished projects are treated
// as already satisfied. The result is capped at ResearchQueueMax entries,
// matching the native research queue's own practical bound.
func ResearchPrerequisiteQueue(projects map[ResearchProjectID]ResearchProjectFacts, finished []ResearchProjectID, targets []ResearchProjectID) ([]ResearchProjectID, error) {
	done := map[ResearchProjectID]bool{}
	for _, name := range finished {
		done[name] = true
	}
	visiting := map[ResearchProjectID]bool{}
	var result []ResearchProjectID
	visits := 0

	var visit func(name ResearchProjectID) error
	visit = func(name ResearchProjectID) error {
		if done[name] {
			return nil
		}
		visits++
		if visits > researchVisitMax {
			return fmt.Errorf("research prerequisite graph exceeds the inspection bound")
		}
		if visiting[name] {
			return fmt.Errorf("cyclic research prerequisites: %s", name)
		}
		row, ok := projects[name]
		if !ok {
			return fmt.Errorf("unavailable ordinary research prerequisite: %s", name)
		}
		hidden, hiddenKnown := row.Hidden.Value()
		if !hiddenKnown || hidden || row.KnowledgeCategory != "" {
			return fmt.Errorf("unavailable ordinary research prerequisite: %s", name)
		}
		prerequisites, preKnown := row.Prerequisites.Value()
		hiddenPrerequisites, hiddenPreKnown := row.HiddenPrerequisites.Value()
		if !preKnown || !hiddenPreKnown {
			return fmt.Errorf("incomplete native prerequisites: %s", name)
		}
		dependencies := map[ResearchProjectID]bool{}
		for _, dependency := range prerequisites {
			dependencies[dependency] = true
		}
		for _, dependency := range hiddenPrerequisites {
			dependencies[dependency] = true
		}
		ordered := make([]ResearchProjectID, 0, len(dependencies))
		for dependency := range dependencies {
			ordered = append(ordered, dependency)
		}
		sort.Slice(ordered, func(i, j int) bool { return ordered[i] < ordered[j] })
		visiting[name] = true
		for _, dependency := range ordered {
			if err := visit(dependency); err != nil {
				return err
			}
		}
		delete(visiting, name)
		done[name] = true
		result = append(result, name)
		return nil
	}

	for _, target := range targets {
		if err := visit(target); err != nil {
			return nil, err
		}
	}
	if len(result) > ResearchQueueMax {
		result = result[:ResearchQueueMax]
	}
	return result, nil
}

// ResearchLabRequirement mirrors the current or next project's native
// laboratory requirements (requiredResearchBuilding/requiredResearchFacilities).
// RequiredBuilding Unknown means the native project imposes no specific bench
// requirement; RequiredFacilities Unknown means the requirement itself has not
// been observed and no bench can be proven usable yet.
type ResearchLabRequirement struct {
	RequiredBuilding   domain.Fact[string]
	RequiredFacilities domain.Fact[[]string]
}

// ResearchPawn mirrors one native colonist's research-eligibility facts:
// incapacitation state, whether native work assignment applies to them at
// all, and their current Research work-type priority/disabled state.
type ResearchPawn struct {
	Pawn                               PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	Applies                            domain.Fact[bool]
	Work                               domain.Fact[[]WorkPriority]
}
