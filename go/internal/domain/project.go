package domain

import "errors"

// ProjectID names one Project row: "project-<hex8 world digest>-<kind>-<gen>",
// minted per world and kind like a routine goal id. A regression mints the
// next generation rather than reopening the completed row (#1925, epic #1911).
type ProjectID string

type ProjectStatus string

const (
	ProjectOpen      ProjectStatus = "open"
	ProjectCompleted ProjectStatus = "finished"
	ProjectVoided    ProjectStatus = "invalidated"
)

// MaxProjectRecord bounds Project.Record in bytes.
const MaxProjectRecord = 4096

// Project is a build-once outcome (cooking, basic power, defensive layout...):
// it completes once and stays completed as its own record. A completed Project
// measured broken with no work open is replaced by a new row; it has no Episode
// and no RecoveryObserved, unlike a Standard.
type Project struct {
	ID       ProjectID
	Kind     ConcernID // the Project's ConcernID (the routine need it serves)
	Priority int
	Snapshot GenerationSnapshot
	Tick     Tick
	Status   ProjectStatus
	Finding  Finding `json:"Need"`
	// Record is the Project's durable planner intent, as Standard.Record.
	Record string `json:",omitempty"`
}

func NewProject(id ProjectID, kind ConcernID, priority int, snapshot GenerationSnapshot, tick Tick) (Project, error) {
	p := Project{ID: id, Kind: kind, Priority: priority, Snapshot: snapshot, Tick: tick, Status: ProjectOpen, Finding: FindingUnclear}
	return p, p.Validate()
}

func (p Project) Validate() error {
	if !validID(string(p.ID)) || !validID(string(p.Kind)) || p.Priority < 0 || p.Priority > 4 || p.Tick < 0 || p.Snapshot.Validate() != nil {
		return errors.New("invalid project")
	}
	if len(p.Record) > MaxProjectRecord {
		return errors.New("project record exceeds bound")
	}
	switch p.Status {
	case ProjectOpen, ProjectCompleted, ProjectVoided:
	default:
		return errors.New("invalid project status")
	}
	switch p.Finding {
	case FindingUnclear, FindingUnmet, FindingMet:
	default:
		return errors.New("invalid project need")
	}
	if p.Status == ProjectCompleted && p.Finding != FindingMet {
		return errors.New("completed project needs observed recovery")
	}
	return nil
}

// ProjectRegressed reports whether a completed Project was measured broken
// with no work open; the caller opens a new Project row and leaves this one
// as its record.
func ProjectRegressed(p Project, need Finding, openWork bool) bool {
	return p.Status == ProjectCompleted && need == FindingUnmet && !openWork
}

// ReviewProject reviews a Project against the current world. An uncompleted
// Project completes when recovery is measured with no work open; a completed one
// stays completed (an unknown measurement does not reopen it) until the world
// changes or the tick rewinds. Callers check ProjectRegressed first and open a
// new row instead.
func ReviewProject(p Project, current GenerationSnapshot, tick Tick, need Finding, openWork bool) (Project, error) {
	if err := p.Validate(); err != nil {
		return p, err
	}
	if current.Validate() != nil || tick < 0 {
		return p, errors.New("invalid project review scope")
	}
	switch need {
	case FindingUnclear, FindingUnmet, FindingMet:
	default:
		return p, errors.New("invalid observed need")
	}
	if p.Status == ProjectVoided {
		return p, nil
	}
	if ProjectRegressed(p, need, openWork) {
		return p, errors.New("regressed project needs a new project")
	}
	if !p.Snapshot.sameColonyMap(current) || tick < p.Tick {
		p.Status = ProjectVoided
		return p, nil
	}
	p.Snapshot, p.Tick = current, tick
	if p.Status == ProjectCompleted {
		return p, nil
	}
	p.Finding = need
	if need == FindingMet && !openWork {
		p.Status = ProjectCompleted
	}
	return p, nil
}
