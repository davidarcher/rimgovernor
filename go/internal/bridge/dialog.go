package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	ob "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// validateChoiceDialog admits the colony census's dialog section: every
// option carries its own list position, a bounded valid label, and the
// selectable/disabled evidence agrees with itself.
func validateChoiceDialog(v *ob.ChoiceDialog) error {
	if v == nil {
		return nil
	}
	if v.WindowId == nil || v.GetWindowId() < 0 || v.WindowType == nil || validID(v.GetWindowType()) != nil || v.Title == nil || v.Text == nil || v.Interactive == nil ||
		len(v.Options) == 0 || len(v.ProtoReflect().GetUnknown()) != 0 {
		return contract("invalid choice dialog census")
	}
	for i, option := range v.Options {
		if option == nil || option.Index == nil || option.GetIndex() != int32(i) || option.Label == nil || validID(option.GetLabel()) != nil || option.Selectable == nil || option.Resolves == nil ||
			(option.GetSelectable() && option.GetDisabledReason() != "") || len(option.ProtoReflect().GetUnknown()) != 0 {
			return contract("invalid choice dialog option")
		}
		for _, key := range option.Keys {
			if validID(key) != nil {
				return contract("invalid choice dialog option key")
			}
		}
	}
	return nil
}

// dialogAction is the DialogIntent of one dialog answer: the exact observed
// window (or joiner letter) ID, option position and label, and for a
// joiner letter its snapshot token.
func dialogAction(action domain.Action) (*op.Action, error) {
	v, ok := action.DialogAnswer()
	if !ok {
		return nil, contract("not a dialog answer action")
	}
	if v.WindowID() < 0 || v.OptionIndex() < 0 || validID(v.OptionLabel()) != nil || (v.LetterToken() != "" && (v.OptionIndex() != 0 || validID(v.LetterToken()) != nil)) {
		return nil, contract("invalid dialog target")
	}
	intent := &op.DialogIntent{WindowId: proto.Int32(v.WindowID()), OptionIndex: proto.Int32(v.OptionIndex()), OptionLabel: proto.String(v.OptionLabel())}
	if v.LetterToken() != "" {
		intent.JoinerLetterToken = proto.String(v.LetterToken())
	}
	return &op.Action{Intent: &op.Action_Dialog{Dialog: intent}}, nil
}

func validateJoinerLetters(rows []*ob.JoinerLetter, tick int64) error {
	seen := map[int32]bool{}
	for _, row := range rows {
		if row == nil || buildingUnknown(row) != nil || row.LetterId == nil || row.GetLetterId() < 0 || seen[row.GetLetterId()] ||
			validID(row.GetSnapshotToken()) != nil || validID(row.GetPawnId()) != nil || row.ExpiresTick == nil || row.GetExpiresTick() <= tick ||
			validID(row.GetAcceptLabel()) != nil || row.CanAccept == nil {
			return contract("invalid joiner letter census")
		}
		seen[row.GetLetterId()] = true
	}
	return nil
}
