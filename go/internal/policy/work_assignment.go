package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type WorkType string
type WorkSkill struct {
	Name     string
	Level    int
	Passion  string
	Disabled bool
	// Stored is the native stored level beneath aptitude offsets; zero when
	// the read did not carry it.
	Stored int
}
type WorkPriority struct {
	Work     WorkType
	Priority int
	Disabled bool
	// Skill is the work type's first relevant skill ("" for unskilled work)
	// and Order its natural priority, both read from the WorkTypeDef when
	// the row is read (the higher Order is filled first); zero on a row the
	// planner writes back.
	Skill string
	Order int
}
type WorkPawn struct {
	ID                                        PawnID
	Available, Applies, Manual, Ranged, Hunts domain.Fact[bool]
	Skills                                    domain.Fact[[]WorkSkill]
	Work                                      domain.Fact[[]WorkPriority]
	// Traits, Incapable (backstory work types) and Age feed the pawn profile;
	// unknown rows degrade the planner to skill-only ordering rather than
	// making the review unknown.
	Traits    domain.Fact[[]PawnTrait]
	Incapable domain.Fact[[]WorkType]
	Age       domain.Fact[float64]
	// Schedule is the current timetable, one TimeAssignmentDef per hour
	// (hour 0 first); unknown when the read carried no complete timetable.
	Schedule        domain.Fact[[]string]
	FoodRestriction domain.Fact[FoodRestriction]
	// MedicalCare is the MedicalCareCategory name from the settings block
	// (#1307 carry tier); unknown when the read did not carry it.
	MedicalCare domain.Fact[string]
	// PolicyInputs are the per-pawn policy planner inputs (#1297).
	PolicyInputs domain.Fact[PawnPolicyInputs]
	// Job is the pawn's current job; unknown when the read carried no job
	// block.
	Job domain.Fact[PawnJob]
	// Inspiration is the current InspirationDef defName (#1187); known "" is
	// no inspiration, unknown is a read that did not carry it.
	Inspiration domain.Fact[string]
	// Biotech is the pawn's Biotech facts (#1678); unknown without Biotech.
	Biotech domain.Fact[PawnBiotech]
	// Hostility inputs (#1299): the Assign-tab response (unknown where the
	// pawn has none to configure), violence from the disabled work tags,
	// BloodLoss severity and summary health.
	Hostility       domain.Fact[domain.HostilityResponse]
	ViolenceCapable domain.Fact[bool]
	BloodLoss       domain.Fact[float64]
	Health          domain.Fact[float64]
	// SelfTend is playerSettings.selfTend (#1305); unknown when the read
	// carried no care settings.
	SelfTend domain.Fact[bool]
	// Rest, Joy and Mood are need levels from the pawn needs block; unknown
	// when the read carried no readable needs (#1312).
	Rest, Joy, Mood domain.Fact[float64]
	// BreakThreshold is the minor mental break threshold from the needs
	// block; HighExpectations the social block's expectations pressure
	// (#1542 mood tier).
	BreakThreshold   domain.Fact[float64]
	HighExpectations domain.Fact[bool]
	// Psyfocus and PsyfocusTarget (0-1) and PsylinkLevel (#1313): known
	// only for a psycaster; unknown without Royalty, a psylink or needs.
	Psyfocus, PsyfocusTarget domain.Fact[float64]
	PsylinkLevel             domain.Fact[int]
}

// HostilityPawn is the pawn's hostility inputs.
func (w WorkPawn) HostilityPawn() HostilityPawn {
	return HostilityPawn{ID: w.ID, Current: w.Hostility, ViolenceCapable: w.ViolenceCapable, Age: w.Age, BloodLoss: w.BloodLoss, Health: w.Health, Job: w.Job}
}

// PawnJob is one pawn's current job as the census saw it. Def is the
// JobDef name, empty for a pawn with no job; Work is the WorkTypeDef of
// the work giver that issued it, empty for a job no giver issued (a forced
// order, rest, a meal, wandering).
type PawnJob struct {
	Def  string
	Work WorkType
	// Target is the thing or cell the job works (#643); unknown from a
	// producer that carried none, a known zero JobTarget for a job with no
	// target.
	Target domain.Fact[JobTarget]
}
type WorkRequirement struct {
	Work    WorkType
	Skill   string
	Minimum int
}
type PawnWorkAssignment struct {
	Pawn       PawnID
	Priorities []WorkPriority
}

// WorkCoverage is the planner's per-work-type census: how many owners the
// colony wanted, how many it found and how many pawns could do the work at
// all, so a goal can say "no capable miner" instead of stalling.
type WorkCoverage struct {
	Work                    WorkType
	Demand, Owners, Capable int
}

// DecayingSkill flags a skill above 10 that no owner or secondary assignment
// exercises; the planner gives it a maintenance slot when the pawn owns
// nothing else.
type DecayingSkill struct {
	Pawn  PawnID
	Skill string
	Level int
}

// WorkRosterReport is what a Rounds pass records of the roster planner
// for presentation (#448): the coverage census, the decaying
// skills and every work pawn's typed profile as of Tick. Presentation only;
// no planner reads it back.
type WorkRosterReport struct {
	Tick     domain.Tick
	Coverage []WorkCoverage
	Decaying []DecayingSkill `json:",omitempty"`
	Profiles []PawnProfile
	// Help is the construction helper record (#653): helpers, unmet ready
	// work and the reason spare capacity is or is not used. The next
	// review reads it back for hysteresis.
	Help *ConstructionHelpRecord `json:",omitempty"`
}

type WorkDecision struct {
	Assignments       []PawnWorkAssignment
	Capacity, Matches domain.Fact[bool]
	Coverage          []WorkCoverage
	Decaying          []DecayingSkill
	// Help is the construction helper decision; nil without WorkDemand.Help.
	Help *ConstructionHelpRecord
}

// WorkDemand carries the colony census the baseline owner table scales by.
// Zero values ask for the baseline alone.
type WorkDemand struct {
	// Resting temporarily replaces the roster with medical rest until immunity.
	Resting []DiseaseRest
	// GrowingCells is the sown/sowable field area; one grower per 150 cells
	// beyond the first.
	GrowingCells int64
	// Construction is whether blueprints or frames are pending; the second
	// constructor is wanted only then.
	Construction bool
	// Prisoners is the prisoner count; a warden is wanted only with one.
	Prisoners int
	// Help enables construction helpers (#653); nil plans none.
	Help *ConstructionHelp
	// HaulBacklog raises Hauling and Cleaning from 4 to 3 (#1278).
	HaulBacklog bool
	// Handling is whether the herd plan holds a milk or wool job: a kept animal
	// yields only to a handler, and the gather speed and yield stats scale with
	// the Animals skill (#1650), so the best Animals pawn owns Handling.
	Handling bool
	// Arming is whether the food plan opened a hunt that waits on a hunter's
	// weapon (HuntArming): Hunting is then assignable to an unarmed colonist
	// before the weapon exists (#2162), so the hunter-aware armory demand
	// sees whom to arm.
	Arming bool
}

// haulBacklogStacks is the loot census count of unforbidden, safe stacks
// with storage headroom that makes a haul backlog (#1278).
const haulBacklogStacks = 20

const (
	WorkDoctor  WorkType = "Doctor"
	WorkWarden  WorkType = "Warden"
	WorkHunting WorkType = "Hunting"
	WorkFishing WorkType = "Fishing"
	WorkGrowing WorkType = "Growing"
	WorkArt     WorkType = "Art"
	WorkPatient WorkType = "Patient"
	WorkBedRest WorkType = "PatientBedRest"
)

// workFloor is the skill level below which a pawn is not capable of the work
// at all: a cook under 5 poisons meals, a doctor under 4 botches surgery, a
// constructor under 4 fails builds and wastes materials.
func workFloor(work WorkType) int {
	switch work {
	case WorkCooking:
		return 5
	case WorkDoctor, WorkConstruction:
		return 4
	}
	return 0
}

// pinned work is always priority 1 for every pawn (native defaults).
func pinnedWork(work WorkType) bool {
	switch work {
	case WorkFirefighter, WorkPatient, "BedRest", WorkBedRest, "Childcare":
		return true
	}
	return false
}

// coreWork is the set whose missing owner is a capacity deficit (the work
// goal's Capacity fact): the roles a colony cannot run without.
func coreWork(work WorkType) bool {
	return work == WorkDoctor || work == WorkCooking || work == WorkConstruction || work == WorkGrowing
}

func basicWork(work WorkType) bool {
	return work == WorkHauling || work == WorkCleaning || work == WorkBasic
}

// baselineDemand is the owner count the colony always wants per work type,
// before the review's requirements. Core roles that keep a colony alive are
// always wanted; the rest are wanted when a natural specialist exists.
func baselineDemand(work WorkType, pawns int, demand WorkDemand) int {
	switch work {
	case WorkDoctor, WorkConstruction, WorkPlantCutting, WorkHunting:
		n := 1
		if work == WorkConstruction && demand.Construction && pawns >= 6 {
			n = 2
		}
		if work == WorkHunting && pawns >= 4 {
			n = 2
		}
		return n
	case WorkCooking:
		return 1 + pawns/8
	case WorkGrowing:
		return 1 + int(demand.GrowingCells/150)
	case WorkWarden:
		if demand.Prisoners > 0 {
			return 1
		}
	case WorkHandling:
		if demand.Handling {
			return 1
		}
	}
	return 0
}

// AssignWork is PlanWork with the baseline demand alone.
func AssignWork(pawns []WorkPawn, required []WorkRequirement) (WorkDecision, error) {
	return PlanWork(pawns, required, WorkDemand{})
}

type workCandidate struct {
	worker  *workWorker
	fitness float64
	level   int
}

type workWorker struct {
	pawn    WorkPawn
	profile PawnProfile
	work    map[WorkType]WorkPriority
	load    int
	owns    map[WorkType]int // planned priority per work type
	// arming marks an unarmed colonist the plan is arming: Hunting is
	// assignable before the weapon exists.
	arming bool
}

// PlanWork is the roster planner: every work type native reports gets owners
// by fitness (level, passion, trait work speed, incumbency), growth
// secondaries (a passion within five levels of the weakest owner) and, under
// numbered priorities, every capable pawn at 3 or 4, never what a trait
// forbids. A temporary disease rest hold zeroes a resting pawn's work.
// Autopilot owns every priority (#719): no player edit is exempt.
// This is a proposal/readback comparison,
// never permission to change pawn settings.
func PlanWork(pawns []WorkPawn, required []WorkRequirement, demand WorkDemand) (WorkDecision, error) {
	if err := validateDiseaseRest(demand.Resting); err != nil {
		return WorkDecision{}, err
	}
	resting := map[PawnID]bool{}
	for _, row := range demand.Resting {
		resting[row.Pawn] = true
	}
	requirements := map[WorkType]WorkRequirement{}
	for _, entry := range required {
		if !validResource(Resource(entry.Work)) || entry.Skill != "" && !validResource(Resource(entry.Skill)) || entry.Minimum < 0 || entry.Minimum > 1000 {
			return WorkDecision{}, errors.New("invalid work requirement")
		}
		if _, seen := requirements[entry.Work]; seen {
			return WorkDecision{}, errors.New("invalid work requirement")
		}
		requirements[entry.Work] = entry
	}
	workers := []*workWorker{}
	seen := map[PawnID]bool{}
	known := true
	for _, pawn := range pawns {
		if !validResource(Resource(pawn.ID)) || seen[pawn.ID] {
			return WorkDecision{}, errors.New("invalid work pawn")
		}
		seen[pawn.ID] = true
		available, ak := pawn.Available.Value()
		applies, pk := pawn.Applies.Value()
		if ak && !available || pk && !applies {
			continue
		}
		if !ak || !pk {
			known = false
			continue
		}
		manual, mk := pawn.Manual.Value()
		skills, sk := pawn.Skills.Value()
		work, wk := pawn.Work.Value()
		_, rk := pawn.Ranged.Value()
		// A checkbox-mode readback (native not yet flipped, #1276) is unknown.
		if !mk || !manual || !sk || !wk || !rk {
			known = false
			continue
		}
		names := map[string]bool{}
		for _, s := range skills {
			if !validResource(Resource(s.Name)) || s.Level < 0 || s.Level > 1000 {
				return WorkDecision{}, errors.New("invalid work skill")
			}
			if names[s.Name] {
				return WorkDecision{}, errors.New("duplicate work skill")
			}
			names[s.Name] = true
		}
		w := &workWorker{pawn: pawn, work: map[WorkType]WorkPriority{}, owns: map[WorkType]int{}, profile: BuildProfile(pawn)}
		for _, value := range work {
			if !validResource(Resource(value.Work)) || value.Priority < 0 || value.Priority > 4 {
				return WorkDecision{}, errors.New("invalid work priority")
			}
			if _, exists := w.work[value.Work]; exists {
				return WorkDecision{}, errors.New("duplicate work type")
			}
			w.work[value.Work] = value
		}
		workers = append(workers, w)
	}
	if !known {
		return WorkDecision{}, nil
	}
	sort.Slice(workers, func(i, j int) bool { return workers[i].pawn.ID < workers[j].pawn.ID })
	// Every work type native reports, in WorkTypeDef natural-priority order
	// (the order the planner fills owners in, so the scarcest roles pick
	// first), ties by name; the skill of each is its row's.
	rowSkill := map[WorkType]string{}
	rowOrder := map[WorkType]int{}
	var types []WorkType
	for _, w := range workers {
		for name, row := range w.work {
			if _, seenWork := rowOrder[name]; !seenWork && !pinnedWork(name) && !basicWork(name) {
				types = append(types, name)
			}
			rowOrder[name] = row.Order
			rowSkill[name] = row.Skill
		}
	}
	sort.Slice(types, func(i, j int) bool {
		if rowOrder[types[i]] != rowOrder[types[j]] {
			return rowOrder[types[i]] > rowOrder[types[j]]
		}
		return types[i] < types[j]
	})
	// able lists who can do a work type at all, at or above its floor (a
	// requirement raises the floor); the incumbent bonus keeps owners stable
	// across reviews when fitness is close.
	skillOf := func(work WorkType) string {
		if r, ok := requirements[work]; ok && r.Skill != "" {
			return r.Skill
		}
		return rowSkill[work]
	}
	floorOf := func(work WorkType) int {
		floor := workFloor(work)
		if r, ok := requirements[work]; ok && r.Minimum > floor {
			floor = r.Minimum
		}
		return floor
	}
	ableAt := func(w *workWorker, work WorkType, floor int) bool {
		row, exists := w.work[work]
		if resting[w.pawn.ID] || !exists || row.Disabled || w.profile.Forbidden(work) || w.profile.Incapable[work] {
			return false
		}
		if work == WorkHunting && !w.profile.Ranged && !w.arming {
			return false
		}
		skill := skillOf(work)
		if skill == "" {
			return true
		}
		s := w.profile.Skill(skill)
		return !s.Disabled && s.Level >= floor
	}
	able := func(w *workWorker, work WorkType) bool { return ableAt(w, work, floorOf(work)) }
	fitness := func(w *workWorker, work WorkType) float64 {
		f := 10 * w.profile.Effects.WorkSpeed
		if skill := skillOf(work); skill != "" {
			s := w.profile.Skill(skill)
			f += float64(s.Level)
			switch s.Passion {
			case "Minor":
				f += 2
			case "Major":
				f += 4
			}
		}
		switch work {
		case WorkHunting:
			f += 5 * w.profile.Effects.MoveSpeed
		case WorkWarden:
			f += 5 * float64(w.profile.Effects.Sociable)
			if w.profile.Effects.Execution {
				f++
			}
		case WorkDoctor:
			if w.profile.Effects.SurgeonSafe {
				f++
			}
		}
		if w.work[work].Priority == 1 {
			f += 1.5
		}
		return f - 3*float64(w.load)
	}
	pawnCount := len(workers)
	if demand.Arming {
		// Arm the best unarmed colonists the baseline Hunting demand still
		// lacks an armed owner for.
		missing := baselineDemand(WorkHunting, pawnCount, demand)
		var unarmed []*workWorker
		for _, w := range workers {
			if w.profile.Ranged {
				if able(w, WorkHunting) {
					missing--
				}
				continue
			}
			w.arming = true
			if able(w, WorkHunting) {
				unarmed = append(unarmed, w)
			}
			w.arming = false
		}
		sort.SliceStable(unarmed, func(i, j int) bool { return fitness(unarmed[i], WorkHunting) > fitness(unarmed[j], WorkHunting) })
		for i := 0; i < missing && i < len(unarmed); i++ {
			unarmed[i].arming = true
		}
	}
	demandOf := func(work WorkType) int {
		n := baselineDemand(work, pawnCount, demand)
		if _, ok := requirements[work]; ok && n == 0 {
			n = 1
		}
		if n == 0 && skillOf(work) != "" {
			// A natural specialist (level 6 or a passion) makes the type
			// worth an owner even without a standing need, so nobody with
			// a skill is parked on hauling.
			for _, w := range workers {
				s := w.profile.Skill(skillOf(work))
				if able(w, work) && (s.Level >= 6 || s.Passion != "") {
					return 1
				}
			}
		}
		return n
	}
	result := WorkDecision{Capacity: domain.Known(true), Matches: domain.Known(true)}
	owners := map[WorkType][]*workWorker{}
	for _, work := range types {
		want := demandOf(work)
		coverage := WorkCoverage{Work: work, Demand: want}
		var candidates []workCandidate
		for _, w := range workers {
			if !able(w, work) {
				continue
			}
			coverage.Capable++
			candidates = append(candidates, workCandidate{worker: w, fitness: fitness(w, work), level: w.profile.Skill(skillOf(work)).Level})
		}
		if len(candidates) == 0 && want > 0 {
			// Nobody clears the safety floor: the best pawn the review's
			// own minimum admits still owns the work rather than leaving a
			// core role (a cook under 5) empty; Capable stays zero so the
			// coverage row says so.
			floor := 0
			if r, ok := requirements[work]; ok {
				floor = r.Minimum
			}
			for _, w := range workers {
				if ableAt(w, work, floor) {
					candidates = append(candidates, workCandidate{worker: w, fitness: fitness(w, work), level: w.profile.Skill(skillOf(work)).Level})
				}
			}
		}
		less := func(a, b workCandidate) bool {
			// Construction owners by raw level: a failed build wastes the
			// materials, so speed and load never outrank skill there. A
			// hunter who also grows (or cooks) loses the field.
			if work == WorkConstruction && a.level != b.level {
				return a.level > b.level
			}
			if work == WorkHunting {
				ac, bc := a.worker.owns[WorkGrowing] == 1 || a.worker.owns[WorkCooking] == 1, b.worker.owns[WorkGrowing] == 1 || b.worker.owns[WorkCooking] == 1
				if ac != bc {
					return !ac
				}
			}
			if a.fitness != b.fitness {
				return a.fitness > b.fitness
			}
			if a.worker.load != b.worker.load {
				return a.worker.load < b.worker.load
			}
			return a.worker.pawn.ID < b.worker.pawn.ID
		}
		sort.Slice(candidates, func(i, j int) bool { return less(candidates[i], candidates[j]) })
		for i := 0; i < len(candidates) && i < want; i++ {
			c := candidates[i]
			c.worker.owns[work] = 1
			c.worker.load++
			owners[work] = append(owners[work], c.worker)
			coverage.Owners++
		}
		if _, required := requirements[work]; want > 0 && coverage.Owners == 0 && (required || coreWork(work)) {
			result.Capacity = domain.Known(false)
		}
		// Secondaries: the next-best backup when the type has demand, and
		// every passion within five levels of the weakest owner so it
		// trains beside the specialist (growth).
		weakest := -1
		for _, o := range owners[work] {
			if l := o.profile.Skill(skillOf(work)).Level; weakest < 0 || l < weakest {
				weakest = l
			}
		}
		for i, c := range candidates {
			if c.worker.owns[work] == 1 {
				continue
			}
			passion := skillOf(work) != "" && c.worker.profile.Skill(skillOf(work)).Passion != ""
			if want > 0 && i == len(owners[work]) || passion && weakest >= 0 && c.level >= weakest-5 {
				c.worker.owns[work] = 2
			}
		}
		result.Coverage = append(result.Coverage, coverage)
	}
	if demand.Help != nil {
		// Helpers (construction_helpers.go): sub-floor pawns the native
		// floor admits, at the lowest rank; owners are not
		// helpers.
		nativeFloor := 0
		if r, ok := requirements[WorkConstruction]; ok {
			nativeFloor = r.Minimum
		}
		rec := planConstructionHelp(*demand.Help, workers, owners[WorkConstruction], func(w *workWorker) bool {
			return w.owns[WorkConstruction] == 0 && !able(w, WorkConstruction) && ableAt(w, WorkConstruction, nativeFloor)
		})
		byID := map[PawnID]*workWorker{}
		for _, w := range workers {
			byID[w.pawn.ID] = w
		}
		for _, id := range rec.Helpers {
			byID[id].owns[WorkConstruction] = 4
		}
		result.Help = &rec
	}
	if demand.Construction {
		// Apprentices: a passionate pawn one level under the floor builds
		// at the lowest rank while builds wait, so the passion trains it
		// past the floor instead of leaving two owners to build alone.
		for _, w := range workers {
			if w.owns[WorkConstruction] != 0 || able(w, WorkConstruction) {
				continue
			}
			if s := w.profile.SkillFor(WorkConstruction); s.Passion != "" && ableAt(w, WorkConstruction, floorOf(WorkConstruction)-1) {
				w.owns[WorkConstruction] = 4
			}
		}
	}
	// Decay: a skill above 10 no owner or secondary slot exercises; a pawn
	// owning nothing keeps it exercised at 2.
	for _, w := range workers {
		for _, name := range sortedSkills(w.profile) {
			s := w.profile.Skills[name]
			if s.Level <= 10 || s.Disabled {
				continue
			}
			exercised := false
			var maintain WorkType
			for _, work := range types {
				if skillOf(work) != name {
					continue
				}
				if p := w.owns[work]; p == 1 || p == 2 {
					exercised = true
				}
				if maintain == "" && able(w, work) {
					maintain = work
				}
			}
			if exercised || maintain == "" {
				continue
			}
			if w.load == 0 {
				w.owns[maintain] = 2
				continue
			}
			result.Decaying = append(result.Decaying, DecayingSkill{Pawn: w.pawn.ID, Skill: name, Level: s.Level})
		}
	}
	matches := true
	for _, w := range workers {
		assignment := PawnWorkAssignment{Pawn: w.pawn.ID}
		for name, observed := range w.work {
			if observed.Disabled {
				continue
			}
			priority := 0
			switch {
			case pinnedWork(name):
				if !w.profile.Forbidden(name) {
					priority = 1
				}
			case basicWork(name):
				// Hauling and Cleaning sit below skilled work at 4, and
				// rise to 3 only during a haul backlog (#1278); the
				// research owner keeps them at 4.
				priority = 3
				if name != WorkBasic && (!demand.HaulBacklog || w.owns[WorkResearch] == 1) {
					priority = 4
				}
			default:
				priority = w.owns[name]
				if priority == 0 && able(w, name) {
					// Everyone capable: 3 with a working level, 4 while
					// still low.
					priority = 4
					if skill := skillOf(name); skill == "" || w.profile.Skill(skill).Level >= 8 {
						priority = 3
					}
				}
			}
			if resting[w.pawn.ID] {
				priority = 0
				if name == WorkPatient || name == WorkBedRest || name == "BedRest" {
					priority = 1
				}
			}
			assignment.Priorities = append(assignment.Priorities, WorkPriority{Work: name, Priority: priority})
			matches = matches && observed.Priority == priority
		}
		sort.Slice(assignment.Priorities, func(i, j int) bool { return assignment.Priorities[i].Work < assignment.Priorities[j].Work })
		result.Assignments = append(result.Assignments, assignment)
	}
	result.Matches = domain.Known(matches)
	return result, nil
}

func sortedSkills(p PawnProfile) []string {
	names := make([]string, 0, len(p.Skills))
	for name := range p.Skills {
		names = append(names, name)
	}
	sort.Strings(names)
	return names
}

// RoundsWorkDemand derives the planner's demand census from the routine
// facts: field cells scale growers, pending blueprints (the definitions the
// review is building) want a second constructor and a prisoner wants a
// warden. Unknown facts fall back to the baseline.
func RoundsWorkDemand(facts RoundsFacts, building bool) WorkDemand {
	demand := WorkDemand{Construction: building, Arming: HuntArming(facts.FoodPlan)}
	jobs := PlanHerd(facts.HerdPlanInput()).Jobs
	_, milk := jobs[HerdJobMilk]
	_, wool := jobs[HerdJobWool]
	demand.Handling = milk || wool
	if cells, ok := facts.GrowingCells.Value(); ok {
		demand.GrowingCells = cells
	}
	if prisoners, ok := facts.Prisoners.Value(); ok {
		demand.Prisoners = len(prisoners)
	}
	if loot, ok := facts.EventLoot.Value(); ok {
		stacks := 0
		for _, row := range loot {
			if room, rk := row.StorageHeadroom.Value(); !row.Forbidden && row.SafetyKnown && row.SafeToHaul && rk && room > 0 {
				stacks++
			}
		}
		demand.HaulBacklog = stacks >= haulBacklogStacks
	}
	return demand
}

// WorkChanges is the WorkSettingsIntent work rows an assignment needs on a pawn:
// the exact priorities the readback does not already hold. ok is false when
// the pawn's readback lacks a planned work type or native has not yet
// switched the pawn to numbered priorities (#1276): unknown, wait.
func WorkChanges(pawn WorkPawn, assignment PawnWorkAssignment) (changed []domain.WorkSetting, ok bool) {
	manual, mk := pawn.Manual.Value()
	current, ck := pawn.Work.Value()
	if !mk || !ck || !manual {
		return nil, false
	}
	values := map[WorkType]int{}
	for _, row := range current {
		values[row.Work] = row.Priority
	}
	for _, setting := range assignment.Priorities {
		old, known := values[setting.Work]
		if !known {
			return nil, false
		}
		want := setting.Priority
		if old != want {
			changed = append(changed, domain.WorkSetting{Definition: string(setting.Work), Priority: int32(want)})
		}
	}
	return changed, true
}
