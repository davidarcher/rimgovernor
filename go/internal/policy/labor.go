package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Native work types routine goals dispatch pawn work through. Names are
// RimWorld WorkTypeDef defNames as ListPawns reports them.
const (
	WorkConstruction WorkType = "Construction"
	WorkResearch     WorkType = "Research"
	WorkMining       WorkType = "Mining"
	WorkPlantCutting WorkType = "PlantCutting"
	WorkCrafting     WorkType = "Crafting"
	WorkHauling      WorkType = "Hauling"
	WorkCleaning     WorkType = "Cleaning"
	WorkBasic        WorkType = "BasicWorker"
	WorkHandling     WorkType = "Handling"
	WorkCooking      WorkType = "Cooking"
	WorkFirefighter  WorkType = "Firefighter"
	WorkTailoring    WorkType = "Tailoring"
	WorkSmithing     WorkType = "Smithing"
)

// LaborProfile lists the native work types a goal's methods can put pawns to:
// a goal is admissible when any listed type still has free labor. An empty
// profile means the goal issues no pawn work and is bounded only by the
// coarse worker count.
type LaborProfile []WorkType

// GoalLabor names each routine need's labor profile. Goals not listed here
// (emergencies, monitoring-only needs, configuration pushes) have no profile.
func GoalLabor(id GoalID) LaborProfile {
	switch id {
	case ClearHomeObstructions, ClearAncientShrine, EnsureBasicDefense, EnsureComfort, EnsureBasicComfort, EnsureExpansion, MaintainEssentialRepairs, MaintainStoneShell, MaintainSleeping, MaintainFoodStorage, MaintainHomeCoverage, MaintainAnimalContainment, MaintainLighting, MaintainFlooring, MaintainRoutes:
		return LaborProfile{WorkConstruction}
	case MaintainWood, RemoveBlight:
		return LaborProfile{WorkPlantCutting}
	case EnsureResearch:
		return LaborProfile{WorkResearch}
	case MaintainResource:
		return LaborProfile{WorkMining, WorkPlantCutting, WorkCrafting}
	case MaintainEquipment:
		// A wear order is a forced job any pawn carries; the replacement
		// bill needs the bench's work type. Construction keeps the workshop
		// rung available before a bench exists to request its crafting work.
		return LaborProfile{WorkConstruction, WorkTailoring, WorkSmithing, WorkCrafting}
	case SecureSupplies, MaintainStorage, MaintainWaste:
		return LaborProfile{WorkHauling}
	case MaintainCleanFacilities:
		// The bounded cleaning response is a player-forced order any basic
		// worker not incapable of Cleaning can carry, and it exists for the
		// case where nobody has Cleaning at priority > 0.
		return LaborProfile{WorkCleaning, WorkBasic}
	case MaintainHerd:
		return LaborProfile{WorkHandling}
	case MaintainAnimalFeed:
		// Feed uses cooking/hauling for bills and growing for hay fields.
		return LaborProfile{WorkCooking, WorkHauling, WorkGrowing}
	case MaintainFireSafety:
		return LaborProfile{WorkFirefighter}
	case TidyLayout:
		// A re-sited field is sown by growers, a re-sited stockpile filled
		// by haulers and a Camp shell taken down by builders (#611).
		return LaborProfile{WorkGrowing, WorkHauling, WorkConstruction}
	case MaintainMedicalReserves:
		// A medicine bill at a crafting bench, or a wild healroot harvest
		// (#445: the herbal bill held a slot for days unworked).
		return LaborProfile{WorkCrafting, WorkPlantCutting}
	}
	return nil
}

// RoutineLabor counts, per native work type, the pawns RoutineWorkers counts
// whose work settings enable that type (priority above zero and not
// disabled). Any counted pawn with unknown or empty work settings makes the
// whole census unknown: native lists every WorkTypeDef for a pawn whose work
// applies, so an empty list is an unobserved census, and partial labor
// evidence must not admit or refuse work.
func RoutineLabor(pawns []WorkPawn) domain.Fact[map[WorkType]int] {
	labor := map[WorkType]int{}
	for _, p := range pawns {
		available, known := p.Available.Value()
		applies, appliesKnown := p.Applies.Value()
		if known && !available || appliesKnown && !applies {
			continue
		}
		work, workKnown := p.Work.Value()
		if !known || !appliesKnown || !workKnown || len(work) == 0 {
			return domain.Unknown[map[WorkType]int]()
		}
		for _, w := range work {
			if w.Priority > 0 && !w.Disabled {
				labor[w.Work]++
			}
		}
	}
	return domain.Known(labor)
}

// LaborUse is the census of what the pawns RoutineLabor counts are doing
// this review. Busy counts, per work type, the pawns whose current job a
// work giver of that type issued (any pawn, drafted or not). Idle counts,
// per work type, the pawns RoutineLabor counts for that type who are not on
// its work: a pawn with no job or wandering, or one a giver of another type
// holds. Rest, meals, recreation, medical care and forced orders are
// neither, so a colony asleep is no evidence about any commitment.
type LaborUse struct {
	Busy, Idle map[WorkType]int
	// Jobs are the jobs Busy counts, one per pawn, so a commitment can tell
	// work on its own targets from the same work type elsewhere (#643).
	Jobs []PawnJob
}

// idleJob reports a job that is no work at all: the job tracker found
// nothing for the pawn to do.
func idleJob(job PawnJob) bool {
	switch job.Def {
	case "", "Wait", "Wait_Wander", "GotoWander", "Wait_MaintainPosture":
		return true
	}
	return false
}

// RoutineLaborUse builds LaborUse from the same pawns RoutineLabor counts.
// Any counted pawn with an unknown job, or with unknown or empty work
// settings, makes the census unknown, as RoutineLabor's is: partial
// evidence must not release a commitment's slot.
func RoutineLaborUse(pawns []WorkPawn) domain.Fact[LaborUse] {
	use := LaborUse{Busy: map[WorkType]int{}, Idle: map[WorkType]int{}}
	for _, p := range pawns {
		job, jobKnown := p.Job.Value()
		if jobKnown && job.Work != "" {
			use.Busy[job.Work]++
			use.Jobs = append(use.Jobs, job)
		}
		available, known := p.Available.Value()
		applies, appliesKnown := p.Applies.Value()
		if known && !available || appliesKnown && !applies {
			continue
		}
		work, workKnown := p.Work.Value()
		if !known || !appliesKnown || !workKnown || len(work) == 0 || !jobKnown {
			return domain.Unknown[LaborUse]()
		}
		for _, w := range work {
			if w.Priority > 0 && !w.Disabled && w.Work != job.Work && (idleJob(job) || job.Work != "") {
				use.Idle[w.Work]++
			}
		}
	}
	return domain.Known(use)
}

// laborIdle reports a labor profile with no pawn on any of its work types
// while at least one pawn enabled for one of them idles or works for
// another type: the work the profile's commitment issued is not being
// picked up. An empty profile or an unknown census is never idle.
func laborIdle(use domain.Fact[LaborUse], profile LaborProfile) bool {
	v, known := use.Value()
	if !known || len(profile) == 0 {
		return false
	}
	idle := 0
	for _, w := range profile {
		if v.Busy[w] > 0 {
			return false
		}
		idle += v.Idle[w]
	}
	return idle > 0
}

// laborLedger tracks free labor per work type during one ranking pass.
type laborLedger struct {
	free  map[WorkType]int
	known bool
}

func newLaborLedger(labor domain.Fact[map[WorkType]int]) laborLedger {
	v, known := labor.Value()
	l := laborLedger{free: map[WorkType]int{}, known: known}
	for w, n := range v {
		l.free[w] = n
	}
	return l
}

// take reserves one pawn of the first free type in profile. With an unknown
// census or an empty profile nothing is reserved and the goal is admissible;
// otherwise the returned work type names the bottleneck.
func (l *laborLedger) take(profile LaborProfile) (bottleneck WorkType, ok bool) {
	return l.claim(profile, true)
}

func (l *laborLedger) claim(profile LaborProfile, take bool) (bottleneck WorkType, ok bool) {
	if !l.known || len(profile) == 0 {
		return "", true
	}
	for _, w := range profile {
		if l.free[w] > 0 {
			if take {
				l.free[w]--
			}
			return "", true
		}
	}
	sorted := append(LaborProfile(nil), profile...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i] < sorted[j] })
	return sorted[0], false
}

func validLabor(profile LaborProfile) bool {
	if len(profile) > 64 {
		return false
	}
	seen := map[WorkType]bool{}
	for _, w := range profile {
		if !validResource(Resource(w)) || seen[w] {
			return false
		}
		seen[w] = true
	}
	return true
}
