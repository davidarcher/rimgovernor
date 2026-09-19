package domain

import "errors"

const DialogAnswerAction ActionKind = "dialog_answer"

// DialogAnswer is explicit intent to activate one exact observed option of
// the single force-pausing choice dialog (Verse.Dialog_NodeTree) the game
// opened by itself: a caravan demand or meeting, a quest dialog, a finished
// research project's completion dialog (#156). WindowID plus the option's
// native list position and label stand in for an EntityPrecondition: the
// native AnswerDialog operation refuses on any drift from these exact
// observed values (NativeChoiceDialogOperations.cs), so a dialog that moved
// on can never receive a stale answer. A nonempty LetterToken explicitly targets
// a WandererJoins letter with WindowID carrying its letter ID instead.
type DialogAnswer struct {
	windowID    int32
	optionIndex int32
	optionLabel string
	letterToken string
}

func NewDialogAnswer(windowID, optionIndex int32, optionLabel string) (DialogAnswer, error) {
	if windowID < 0 || optionIndex < 0 || !validID(optionLabel) {
		return DialogAnswer{}, errors.New("dialog answer requires a valid window, option index and label")
	}
	return DialogAnswer{windowID: windowID, optionIndex: optionIndex, optionLabel: optionLabel}, nil
}

// NewJoinerLetterAnswer targets a pending native letter rather than a window.
func NewJoinerLetterAnswer(letterID int32, label, token string) (DialogAnswer, error) {
	d, err := NewDialogAnswer(letterID, 0, label)
	if err != nil || !validID(token) {
		return DialogAnswer{}, errors.New("invalid joiner letter target")
	}
	d.letterToken = token
	return d, nil
}
func (d DialogAnswer) LetterToken() string { return d.letterToken }
func (d DialogAnswer) WindowID() int32     { return d.windowID }
func (d DialogAnswer) OptionIndex() int32  { return d.optionIndex }
func (d DialogAnswer) OptionLabel() string { return d.optionLabel }

func NewDialogAnswerAction(id ActionID, value DialogAnswer) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewDialogAnswer(value.windowID, value.optionIndex, value.optionLabel); err != nil {
		return Action{}, err
	}
	if value.letterToken != "" && (!validID(value.letterToken) || value.optionIndex != 0) {
		return Action{}, errors.New("invalid joiner letter answer")
	}
	return Action{id: id, kind: DialogAnswerAction, dialogAnswer: value}, nil
}

func (a Action) DialogAnswer() (DialogAnswer, bool) {
	return a.dialogAnswer, a.kind == DialogAnswerAction
}
