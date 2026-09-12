package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

const WorkAssignmentAction ActionKind = "work_assignment"

type WorkSetting struct {
	Definition string
	Priority   int32
}

// WorkAssignment is an immutable, comparable value. The private canonical
// encoding retains a bounded typed list without exposing mutable action slices.
type WorkAssignment struct {
	pawn     PawnID
	before   string
	manual   bool
	settings string
}

func NewWorkAssignment(pawn PawnID, before string, manual bool, settings []WorkSetting) (WorkAssignment, error) {
	if !validID(string(pawn)) || !validID(before) || len(settings) == 0 || len(settings) > 256 {
		return WorkAssignment{}, errors.New("invalid work assignment")
	}
	rows := append([]WorkSetting(nil), settings...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].Definition < rows[j].Definition })
	for i, row := range rows {
		if !validID(row.Definition) || row.Priority < 0 || row.Priority > 4 || !manual && row.Priority != 0 && row.Priority != 3 || i > 0 && rows[i-1].Definition == row.Definition {
			return WorkAssignment{}, errors.New("invalid work priority")
		}
	}
	data, err := json.Marshal(rows)
	if err != nil {
		return WorkAssignment{}, err
	}
	if len(data) > 30000 {
		return WorkAssignment{}, errors.New("work assignment exceeds storage bound")
	}
	return WorkAssignment{pawn, before, manual, string(data)}, nil
}
func (w WorkAssignment) Pawn() PawnID        { return w.pawn }
func (w WorkAssignment) BeforeToken() string { return w.before }
func (w WorkAssignment) Manual() bool        { return w.manual }
func (w WorkAssignment) Settings() []WorkSetting {
	var rows []WorkSetting
	_ = json.Unmarshal([]byte(w.settings), &rows)
	return rows
}
func NewWorkAssignmentAction(id ActionID, w WorkAssignment) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewWorkAssignment(w.pawn, w.before, w.manual, w.Settings())
	if err != nil || canonical != w {
		return Action{}, errors.New("invalid work assignment")
	}
	return Action{id: id, kind: WorkAssignmentAction, work: w}, nil
}
func (a Action) WorkAssignment() (WorkAssignment, bool) {
	return a.work, a.kind == WorkAssignmentAction
}
