package domain

import "errors"

const ResearchSelectAction ActionKind = "research_select"

// ResearchSelect is explicit intent to set the native current research
// project to one already-queued, prerequisite-ordered ResearchProjectDef.
// It is sent as a ResearchIntent on Actions/Apply; EnsureResearch's routine
// planner proposes one only while no native research project is current.
type ResearchSelect struct {
	project string
}

func NewResearchSelect(project string) (ResearchSelect, error) {
	if !validID(project) {
		return ResearchSelect{}, errors.New("research select requires a valid project identity")
	}
	return ResearchSelect{project: project}, nil
}

func (r ResearchSelect) Project() string { return r.project }

func NewResearchSelectAction(id ActionID, value ResearchSelect) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewResearchSelect(value.project); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: ResearchSelectAction, researchSelect: value}, nil
}

func (a Action) ResearchSelect() (ResearchSelect, bool) {
	return a.researchSelect, a.kind == ResearchSelectAction
}
