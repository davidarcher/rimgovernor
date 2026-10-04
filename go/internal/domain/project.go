package domain

import "errors"

// ProjectID names one Project row: "project-<hex8 world digest>-<kind>-<gen>",
// minted per world and kind like a routine goal id. A regression mints the
// next generation rather than reopening the finished row (#1925, epic #1911).
type ProjectID string

type ProjectStatus string

const (
	ProjectOpen        ProjectStatus = "open"
	ProjectFinished    ProjectStatus = "finished"
	ProjectInvalidated ProjectStatus = "invalidated"
)

// MaxProjectRecord bounds Project.Record in bytes.
const MaxProjectRecord = 4096

// Project is a build-once outcome (cooking, basic power, defensive layout...):
// it finishes once and stays finished as its own record. A finished Project
// measured broken with no work open is replaced by a new row; it has no Epoch
// and no RecoveryObserved, unlike a Standard goal.
type Project struct {
	ID       ProjectID
	Kind     GoalID // the Project's GoalID (the routine need it serves)
	Priority int
	Snapshot GenerationSnapshot
	Tick     Tick
	Status   ProjectStatus
	Need     NeedState
	// Record is the Project's durable planner intent, as Goal.Record.
	Record string `json:",omitempty"`
}

func NewProject(id ProjectID, kind GoalID, priority int, snapshot GenerationSnapshot, tick Tick) (Project, error) {
	p := Project{ID: id, Kind: kind, Priority: priority, Snapshot: snapshot, Tick: tick, Status: ProjectOpen, Need: NeedUnknown}
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
	case ProjectOpen, ProjectFinished, ProjectInvalidated:
	default:
		return errors.New("invalid project status")
	}
	switch p.Need {
	case NeedUnknown, NeedDeficit, NeedRecovered:
	default:
		return errors.New("invalid project need")
	}
	if p.Status == ProjectFinished && p.Need != NeedRecovered {
		return errors.New("finished project needs observed recovery")
	}
	return nil
}

// ProjectRegressed reports whether a finished Project was measured broken
// with no work open; the caller opens a new Project row and leaves this one
// as its record.
func ProjectRegressed(p Project, need NeedState, openWork bool) bool {
	return p.Status == ProjectFinished && need == NeedDeficit && !openWork
}

// ReviewProject reviews a Project against the current world. An unfinished
// Project finishes when recovery is measured with no work open; a finished one
// stays finished (an unknown measurement does not reopen it) until the world
// changes or the tick rewinds. Callers check ProjectRegressed first and open a
// new row instead.
func ReviewProject(p Project, current GenerationSnapshot, tick Tick, need NeedState, openWork bool) (Project, error) {
	if err := p.Validate(); err != nil {
		return p, err
	}
	if current.Validate() != nil || tick < 0 {
		return p, errors.New("invalid project review scope")
	}
	switch need {
	case NeedUnknown, NeedDeficit, NeedRecovered:
	default:
		return p, errors.New("invalid observed need")
	}
	if p.Status == ProjectInvalidated {
		return p, nil
	}
	if ProjectRegressed(p, need, openWork) {
		return p, errors.New("regressed project needs a new project")
	}
	if !p.Snapshot.sameColonyMap(current) || tick < p.Tick {
		p.Status = ProjectInvalidated
		return p, nil
	}
	p.Snapshot, p.Tick = current, tick
	if p.Status == ProjectFinished {
		return p, nil
	}
	p.Need = need
	if need == NeedRecovered && !openWork {
		p.Status = ProjectFinished
	}
	return p, nil
}
