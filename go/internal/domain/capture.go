package domain

import "errors"

// Capture is explicit intent to bring one already-observed downed, hostile
// humanlike into colony custody as a prisoner. Neither pawn is drafted;
// native eligibility (CanBeCaptured, hostile-to-player, an available
// prisoner bed, reachability) is established at inspection, not here -- the
// same split Rescue uses. Capture and Rescue share the identical
// PawnTargetOrder wire shape (OrderTool.cs's "capture"/"rescue" actions);
// they are kept as distinct domain actions because their patient
// eligibility and completion evidence differ (capture succeeds into
// prisoner custody, rescue succeeds into an ordinary/guest bed).
type Capture struct {
	capturer, patient PawnID
}

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
	return Action{id: id, kind: CaptureAction, capture: capture}, nil
}

func (a Action) Capture() (Capture, bool) { return a.capture, a.kind == CaptureAction }
