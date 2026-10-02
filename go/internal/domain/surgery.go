package domain

import "errors"

// SurgeryAction queues one medical operation bill on one patient (#1162): a
// medical ProductionBillIntent on Actions/Apply. Native re-checks the patient, recipe and
// part live; native doctor jobs choose the surgeon. Applied means queued.
const SurgeryAction ActionKind = "surgery"

// NoSurgeryPart is the part index of a whole-body recipe.
const NoSurgeryPart = -1

// Surgery is an immutable, comparable value: the patient, the recipe
// defName, the body part index in the race body's AllParts (NoSurgeryPart
// for a whole-body recipe) and whether the planner accepted that the recipe
// is a violation on the patient (organ harvest). surgeon, when set, restricts
// the bill to that colonist (#1253); empty keeps vanilla's choice.
type Surgery struct {
	pawn        PawnID
	recipe      string
	part        int
	acknowledge bool
	surgeon     PawnID
}

func NewSurgery(pawn PawnID, recipe string, part int, acknowledgeViolation bool) (Surgery, error) {
	if !validID(string(pawn)) || !validID(recipe) || part < NoSurgeryPart {
		return Surgery{}, errors.New("surgery requires a patient, a recipe and a whole-body or exact part index")
	}
	return Surgery{pawn: pawn, recipe: recipe, part: part, acknowledge: acknowledgeViolation}, nil
}

func (s Surgery) Pawn() PawnID               { return s.pawn }
func (s Surgery) Recipe() string             { return s.recipe }
func (s Surgery) Part() int                  { return s.part }
func (s Surgery) AcknowledgeViolation() bool { return s.acknowledge }
func (s Surgery) Surgeon() PawnID            { return s.surgeon }

// WithSurgeon restricts the bill to one colonist other than the patient.
func (s Surgery) WithSurgeon(surgeon PawnID) (Surgery, error) {
	if !validID(string(surgeon)) || surgeon == s.pawn {
		return Surgery{}, errors.New("surgeon must be a colonist other than the patient")
	}
	s.surgeon = surgeon
	return s, nil
}

func NewSurgeryAction(id ActionID, s Surgery) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewSurgery(s.pawn, s.recipe, s.part, s.acknowledge)
	if err == nil && s.surgeon != "" {
		canonical, err = canonical.WithSurgeon(s.surgeon)
	}
	if err != nil || canonical != s {
		return Action{}, errors.New("invalid surgery")
	}
	return Action{id: id, kind: SurgeryAction, surgery: s}, nil
}

func (a Action) Surgery() (Surgery, bool) {
	return a.surgery, a.kind == SurgeryAction
}
