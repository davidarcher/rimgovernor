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
}
type WorkPriority struct {
	Work     WorkType
	Priority int
	Disabled bool
}
type WorkPawn struct {
	ID                                 PawnID
	Available, Applies, Manual, Ranged domain.Fact[bool]
	Skills                             domain.Fact[[]WorkSkill]
	Work                               domain.Fact[[]WorkPriority]
}
type WorkRequirement struct {
	Work    WorkType
	Skill   string
	Minimum int
}
type WorkOverride struct {
	Pawn     PawnID
	Work     WorkType
	Priority int
}
type PawnWorkAssignment struct {
	Pawn       PawnID
	Priorities []WorkPriority
}
type WorkDecision struct {
	Assignments       []PawnWorkAssignment
	Capacity, Matches domain.Fact[bool]
}

// AssignWork ports stable specialist selection and native checkbox semantics.
// This is a proposal/readback comparison, never permission to change pawn settings.
func AssignWork(pawns []WorkPawn, required []WorkRequirement, overrides []WorkOverride) (WorkDecision, error) {
	if len(pawns) > 256 || len(required) > 256 || len(overrides) > 4096 {
		return WorkDecision{}, errors.New("work review exceeds bounds")
	}
	requirements := []WorkRequirement{{Work: "Construction", Skill: "Construction"}, {Work: "Growing", Skill: "Plants"}, {Work: "Cooking", Skill: "Cooking"}, {Work: "Hunting", Skill: "Shooting"}, {Work: "Doctor", Skill: "Medicine"}, {Work: "PlantCutting", Skill: "Plants"}}
	seenRequired := map[WorkType]bool{}
	for _, entry := range required {
		if !validResource(Resource(entry.Work)) || entry.Skill != "" && !validResource(Resource(entry.Skill)) || entry.Minimum < 0 || entry.Minimum > 1000 || seenRequired[entry.Work] {
			return WorkDecision{}, errors.New("invalid work requirement")
		}
		seenRequired[entry.Work] = true
		replaced := false
		for i := range requirements {
			if requirements[i].Work == entry.Work {
				requirements[i] = entry
				replaced = true
			}
		}
		if !replaced {
			requirements = append(requirements, entry)
		}
	}
	type overrideKey struct {
		pawn PawnID
		work WorkType
	}
	custom := map[overrideKey]int{}
	for _, entry := range overrides {
		key := overrideKey{entry.Pawn, entry.Work}
		if !validResource(Resource(entry.Pawn)) || !validResource(Resource(entry.Work)) || entry.Priority < 0 || entry.Priority > 4 {
			return WorkDecision{}, errors.New("invalid work override")
		}
		if _, exists := custom[key]; exists {
			return WorkDecision{}, errors.New("duplicate work override")
		}
		custom[key] = entry.Priority
	}
	type worker struct {
		pawn   WorkPawn
		skills map[string]WorkSkill
		work   map[WorkType]WorkPriority
		manual bool
		ranged bool
	}
	workers := []worker{}
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
		ranged, rk := pawn.Ranged.Value()
		if !mk || !sk || !wk || !rk {
			known = false
			continue
		}
		if len(skills) > 256 || len(work) > 256 {
			return WorkDecision{}, errors.New("work pawn details exceed bounds")
		}
		w := worker{pawn: pawn, manual: manual, ranged: ranged, skills: map[string]WorkSkill{}, work: map[WorkType]WorkPriority{}}
		for _, s := range skills {
			if !validResource(Resource(s.Name)) || s.Level < 0 || s.Level > 1000 {
				return WorkDecision{}, errors.New("invalid work skill")
			}
			if _, exists := w.skills[s.Name]; exists {
				return WorkDecision{}, errors.New("duplicate work skill")
			}
			w.skills[s.Name] = s
		}
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
	load := map[PawnID]int{}
	owners := map[WorkType]PawnID{}
	denied := func(id PawnID, work WorkType) bool {
		v, exists := custom[overrideKey{id, work}]
		return exists && v == 0
	}
	type candidate struct {
		id                 PawnID
		conflict           bool
		load, score, level int
	}
	for _, requirement := range requirements {
		candidates := []candidate{}
		for _, worker := range workers {
			id := worker.pawn.ID
			work, exists := worker.work[requirement.Work]
			if !exists || work.Disabled || denied(id, requirement.Work) || requirement.Work == "Hunting" && !worker.ranged {
				continue
			}
			skill, exists := worker.skills[requirement.Skill]
			if requirement.Skill == "" {
				exists = true
			}
			if !exists || skill.Disabled || skill.Level < requirement.Minimum {
				continue
			}
			bonus := 0
			if skill.Passion == "Minor" {
				bonus = 2
			}
			if skill.Passion == "Major" {
				bonus = 4
			}
			candidates = append(candidates, candidate{id: id, conflict: requirement.Work == "Hunting" && owners["Growing"] == id, load: load[id], score: skill.Level + bonus - 3*load[id], level: skill.Level})
		}
		less := func(a, b candidate) bool {
			if requirement.Work == "Construction" && a.level != b.level {
				return a.level > b.level
			}
			if requirement.Work != "Construction" && requirement.Work != "Research" {
				if a.conflict != b.conflict {
					return !a.conflict
				}
				if a.load != b.load {
					return a.load < b.load
				}
			}
			if a.score != b.score {
				return a.score > b.score
			}
			if a.load != b.load {
				return a.load < b.load
			}
			return a.id < b.id
		}
		sort.Slice(candidates, func(i, j int) bool { return less(candidates[i], candidates[j]) })
		if len(candidates) > 0 {
			owners[requirement.Work] = candidates[0].id
			load[candidates[0].id]++
		}
	}
	hunters := map[PawnID]bool{}
	growers := map[PawnID]bool{}
	if owner, ok := owners["Hunting"]; ok {
		hunters[owner] = true
	}
	if owner, ok := owners["Growing"]; ok {
		growers[owner] = true
	}
	var second PawnID
	for _, w := range workers {
		id := w.pawn.ID
		work, exists := w.work["Hunting"]
		if hunters[id] || id == owners["Growing"] || id == owners["Cooking"] || denied(id, "Hunting") || !w.ranged || !exists || work.Disabled {
			continue
		}
		if second == "" || load[id] < load[second] {
			second = id
		}
	}
	if second != "" {
		hunters[second] = true
	}
	var spare PawnID
	bestLevel := -1
	for _, w := range workers {
		id := w.pawn.ID
		work, wk := w.work["Growing"]
		skill, sk := w.skills["Plants"]
		if load[id] != 0 || hunters[id] || denied(id, "Growing") || !wk || work.Disabled || !sk || skill.Disabled {
			continue
		}
		if skill.Level > bestLevel {
			spare = id
			bestLevel = skill.Level
		}
	}
	if spare != "" {
		growers[spare] = true
	}
	result := WorkDecision{Capacity: domain.Known(true), Matches: domain.Known(true)}
	matches := true
	coverage := map[WorkType]bool{"Doctor": true, "Cooking": true, "Construction": true, "Growing": true}
	for _, r := range required {
		coverage[r.Work] = true
	}
	for work := range coverage {
		if _, ok := owners[work]; !ok {
			result.Capacity = domain.Known(false)
			matches = false
		}
	}
	for _, w := range workers {
		assignment := PawnWorkAssignment{Pawn: w.pawn.ID}
		for name, observed := range w.work {
			if observed.Disabled {
				continue
			}
			priority := 0
			switch name {
			case "Firefighter", "Patient", "BedRest", "PatientBedRest", "Childcare":
				priority = 1
			default:
				if owner, ok := owners[name]; ok {
					primary := owner == w.pawn.ID
					if name == "Growing" {
						primary = growers[w.pawn.ID]
					}
					if name == "Hunting" {
						primary = hunters[w.pawn.ID]
					}
					if primary {
						priority = 1
					} else if w.manual {
						priority = 3
					}
				} else if name == "Hauling" || name == "Cleaning" || name == "BasicWorker" {
					if w.manual || owners["Research"] != w.pawn.ID || name == "BasicWorker" {
						priority = 3
					}
				}
			}
			if value, ok := custom[overrideKey{w.pawn.ID, name}]; ok {
				priority = value
			}
			assignment.Priorities = append(assignment.Priorities, WorkPriority{Work: name, Priority: priority})
			if w.manual {
				matches = matches && observed.Priority == priority
			} else {
				matches = matches && ((observed.Priority > 0) == (priority > 0))
			}
		}
		sort.Slice(assignment.Priorities, func(i, j int) bool { return assignment.Priorities[i].Work < assignment.Priorities[j].Work })
		result.Assignments = append(result.Assignments, assignment)
	}
	result.Matches = domain.Known(matches)
	return result, nil
}
