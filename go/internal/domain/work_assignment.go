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
// encoding retains a bounded typed list without exposing mutable action
// slices. An assignment may carry work priorities, an allowed-area
// assignment (named or an explicit clear), or both together -- the native
// PatchPawn write surface admits either field alone or combined through one
// shared CAS token, so this type mirrors that at the domain boundary rather
// than splitting into a second action kind.
type WorkAssignment struct {
	pawn      PawnID
	before    string
	manual    bool
	settings  string
	hasArea   bool
	areaClear bool
	areaID    string
}

func newWorkAssignment(pawn PawnID, before string, manual bool, settings []WorkSetting, hasArea, areaClear bool, areaID string) (WorkAssignment, error) {
	if !validID(string(pawn)) || !validID(before) {
		return WorkAssignment{}, errors.New("invalid work assignment")
	}
	if len(settings) == 0 && !hasArea {
		return WorkAssignment{}, errors.New("invalid work assignment")
	}
	if len(settings) > 256 {
		return WorkAssignment{}, errors.New("invalid work assignment")
	}
	if hasArea {
		if areaClear && areaID != "" {
			return WorkAssignment{}, errors.New("invalid area assignment")
		}
		if !areaClear && !validID(areaID) {
			return WorkAssignment{}, errors.New("invalid area assignment")
		}
	} else if areaClear || areaID != "" {
		return WorkAssignment{}, errors.New("invalid area assignment")
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
	return WorkAssignment{pawn, before, manual, string(data), hasArea, areaClear, areaID}, nil
}

func NewWorkAssignment(pawn PawnID, before string, manual bool, settings []WorkSetting) (WorkAssignment, error) {
	return newWorkAssignment(pawn, before, manual, settings, false, false, "")
}

// NewAreaAssignment builds a work-free allowed-area assignment: clear=true
// requests removing any area restriction, otherwise area names the target
// area's identity (as published in the same identifier space native
// observation and native admission both accept for this field).
func NewAreaAssignment(pawn PawnID, before string, clear bool, area string) (WorkAssignment, error) {
	return newWorkAssignment(pawn, before, false, nil, true, clear, area)
}

func (w WorkAssignment) Pawn() PawnID        { return w.pawn }
func (w WorkAssignment) BeforeToken() string { return w.before }
func (w WorkAssignment) Manual() bool        { return w.manual }
func (w WorkAssignment) Settings() []WorkSetting {
	var rows []WorkSetting
	_ = json.Unmarshal([]byte(w.settings), &rows)
	return rows
}

// HasArea reports whether this assignment carries an allowed-area change.
// AreaClear reports whether that change clears the restriction (only
// meaningful when HasArea is true); Area names the target area otherwise.
func (w WorkAssignment) HasArea() bool   { return w.hasArea }
func (w WorkAssignment) AreaClear() bool { return w.hasArea && w.areaClear }
func (w WorkAssignment) Area() string    { return w.areaID }

// Canonical re-validates a WorkAssignment from its exposed fields. Callers
// outside this package that did not construct the value themselves (e.g.
// bridge, decoding a stored payload) use it to confirm the value is still
// exactly what NewWorkAssignment/NewAreaAssignment would have produced.
func (w WorkAssignment) Canonical() (WorkAssignment, error) {
	return newWorkAssignment(w.pawn, w.before, w.manual, w.Settings(), w.hasArea, w.areaClear, w.areaID)
}

func NewWorkAssignmentAction(id ActionID, w WorkAssignment) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := w.Canonical()
	if err != nil || canonical != w {
		return Action{}, errors.New("invalid work assignment")
	}
	return Action{id: id, kind: WorkAssignmentAction, work: w}, nil
}
func (a Action) WorkAssignment() (WorkAssignment, bool) {
	return a.work, a.kind == WorkAssignmentAction
}
