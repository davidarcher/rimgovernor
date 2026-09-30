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

// ScheduleHours is the timetable length: one TimeAssignmentDef per hour.
const ScheduleHours = 24

// WorkAssignment is an immutable, comparable value. The private canonical
// encoding retains a bounded typed list without exposing mutable action
// slices. An assignment may carry work priorities, an allowed-area
// assignment (named or an explicit clear), a 24-hour timetable, or any of
// them together -- the native WorkSettingsIntent admits the fields
// alone or combined, so this type mirrors that
// at the domain boundary rather than splitting into further action kinds.
type WorkAssignment struct {
	pawn      PawnID
	settings  string
	hasArea   bool
	areaClear bool
	areaID    string
	schedule  string
	food      string
	drug      string
}

func newWorkAssignment(pawn PawnID, settings []WorkSetting, hasArea, areaClear bool, areaID string, schedule []string, food ...string) (WorkAssignment, error) {
	if !validID(string(pawn)) {
		return WorkAssignment{}, errors.New("invalid work assignment")
	}
	if len(settings) == 0 && !hasArea && len(schedule) == 0 && len(food) == 0 {
		return WorkAssignment{}, errors.New("invalid work assignment")
	}
	encodedSchedule := ""
	if len(schedule) > 0 {
		if len(schedule) != ScheduleHours {
			return WorkAssignment{}, errors.New("invalid schedule assignment")
		}
		for _, def := range schedule {
			if !validID(def) {
				return WorkAssignment{}, errors.New("invalid schedule assignment")
			}
		}
		data, err := json.Marshal(schedule)
		if err != nil {
			return WorkAssignment{}, err
		}
		encodedSchedule = string(data)
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
		if !validID(row.Definition) || row.Priority < 0 || row.Priority > 4 || i > 0 && rows[i-1].Definition == row.Definition {
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
	encodedFood := ""
	if len(food) > 0 {
		defs := append([]string(nil), food...)
		sort.Strings(defs)
		for i, def := range defs {
			if !validID(def) || i > 0 && defs[i-1] == def {
				return WorkAssignment{}, errors.New("invalid food assignment")
			}
		}
		data, _ := json.Marshal(defs)
		if len(data) > 30000 {
			return WorkAssignment{}, errors.New("food assignment exceeds storage bound")
		}
		encodedFood = string(data)
	}
	return WorkAssignment{pawn: pawn, settings: string(data), hasArea: hasArea, areaClear: areaClear, areaID: areaID, schedule: encodedSchedule, food: encodedFood}, nil
}

func NewDrugPolicyAssignment(pawn PawnID, name string) (WorkAssignment, error) {
	if !validID(string(pawn)) || !validID(name) {
		return WorkAssignment{}, errors.New("invalid drug policy assignment")
	}
	return WorkAssignment{pawn: pawn, drug: name}, nil
}
func (w WorkAssignment) DrugPolicy() string { return w.drug }

// NewFoodAssignment carries a bounded diet expansion through the pawn settings CAS.
func NewFoodAssignment(pawn PawnID, defs []string) (WorkAssignment, error) {
	return newWorkAssignment(pawn, nil, false, false, "", nil, defs...)
}
func (w WorkAssignment) FoodAllow() []string {
	var defs []string
	if w.food != "" {
		_ = json.Unmarshal([]byte(w.food), &defs)
	}
	return defs
}

func NewWorkAssignment(pawn PawnID, settings []WorkSetting) (WorkAssignment, error) {
	return newWorkAssignment(pawn, settings, false, false, "", nil)
}

// NewScheduleAssignment builds a timetable write, alone or beside work
// priorities (settings may be empty): schedule holds one TimeAssignmentDef
// name per hour, hour 0 first.
func NewScheduleAssignment(pawn PawnID, settings []WorkSetting, schedule []string) (WorkAssignment, error) {
	if len(schedule) == 0 {
		return WorkAssignment{}, errors.New("invalid schedule assignment")
	}
	return newWorkAssignment(pawn, settings, false, false, "", schedule)
}

// NewAreaAssignment builds a work-free allowed-area assignment: clear=true
// requests removing any area restriction, otherwise area names the target
// area's identity (as published in the same identifier space native
// observation and native admission both accept for this field).
func NewAreaAssignment(pawn PawnID, clear bool, area string) (WorkAssignment, error) {
	return newWorkAssignment(pawn, nil, true, clear, area, nil)
}

func (w WorkAssignment) Pawn() PawnID { return w.pawn }
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

// HasSchedule reports whether this assignment writes the timetable;
// Schedule is the hour-indexed TimeAssignmentDef list (empty otherwise).
func (w WorkAssignment) HasSchedule() bool { return w.schedule != "" }
func (w WorkAssignment) Schedule() []string {
	if w.schedule == "" {
		return nil
	}
	var rows []string
	_ = json.Unmarshal([]byte(w.schedule), &rows)
	return rows
}

// Canonical re-validates a WorkAssignment from its exposed fields. Callers
// outside this package that did not construct the value themselves (e.g.
// bridge, decoding a stored payload) use it to confirm the value is still
// exactly what NewWorkAssignment/NewAreaAssignment would have produced.
func (w WorkAssignment) Canonical() (WorkAssignment, error) {
	if w.drug != "" {
		return NewDrugPolicyAssignment(w.pawn, w.drug)
	}
	return newWorkAssignment(w.pawn, w.Settings(), w.hasArea, w.areaClear, w.areaID, w.Schedule(), w.FoodAllow()...)
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
