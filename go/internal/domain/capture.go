package domain

import "errors"

// Capture brings an observed humanlike into prisoner custody. The ordinary
// mode uses an undrafted capturer on a downed hostile. Arrest mode names an
// exact prisoner bed and uses an owned draft on a standing neutral ancient.
// Both modes require native eligibility and observed custody completion.
type Capture struct {
	capturer, patient PawnID
	bed               string
}

// NewArrest captures a standing neutral ancient using the existing native Arrest
// operation. The plan must first acquire an owned draft for the capturer.
func NewArrest(capturer, patient PawnID, bed string) (Capture, error) {
	c, err := NewCapture(capturer, patient)
	if err != nil {
		return Capture{}, err
	}
	if !validID(bed) || bed == string(capturer) || bed == string(patient) {
		return Capture{}, errors.New("arrest requires an exact prisoner bed")
	}
	c.bed = bed
	return c, nil
}
func (c Capture) Arrest() bool { return c.bed != "" }
func (c Capture) Bed() string  { return c.bed }

func NewCapture(capturer, patient PawnID) (Capture, error) {
	if !validID(string(capturer)) || !validID(string(patient)) || capturer == patient {
		return Capture{}, errors.New("capture requires distinct valid capturer and patient identities")
	}
	return Capture{capturer: capturer, patient: patient}, nil
}

func (c Capture) Capturer() PawnID { return c.capturer }
func (c Capture) Patient() PawnID  { return c.patient }

const CaptureAction ActionKind = "capture"

func NewCaptureAction(id ActionID, capture Capture) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewCapture(capture.capturer, capture.patient); err != nil {
		return Action{}, err
	}
	if capture.Arrest() {
		if _, err := NewArrest(capture.capturer, capture.patient, capture.bed); err != nil {
			return Action{}, err
		}
	}
	return Action{id: id, kind: CaptureAction, capture: capture}, nil
}

func (a Action) Capture() (Capture, bool) { return a.capture, a.kind == CaptureAction }
