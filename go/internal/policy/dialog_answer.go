package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// DialogOption is one option of the force-pausing choice dialog the colony
// census observed (ColonyFactsSnapshot.dialog), in native order.
type DialogOption struct {
	Index      int32
	Label      string
	Selectable bool
	Resolves   bool
}

// DialogAnswerPolicy decides which option answers a choice dialog the game
// opened by itself (#156). Prefer is an ordered list of label patterns; the
// first pattern that matches a selectable option (case-insensitive substring)
// wins. When nothing matches, the default answer is the first selectable
// option: the position the game gives its own primary answer
// (DiaOption.DefaultOK, CaravanDemand's "Give", a quest's first choice).
type DialogAnswerPolicy struct{ Prefer []string }

// DefaultDialogAnswerPrefer is the passive, non-escalating preference order:
// dismiss an informational dialog, ignore a passing caravan rather than
// trade or attack, keep playing past a game-over prompt, and pay a demand
// rather than fight a caravan the colony did not choose to arm.
var DefaultDialogAnswerPrefer = []string{"OK", "Ignore", "Keep playing", "Give"}

// ChooseDialogOption applies the policy to the observed options. ok is false
// when no option can answer the dialog at all (every option disabled or a
// hyperlink), in which case the controller has no method and the player must
// intervene.
func ChooseDialogOption(options []DialogOption, p DialogAnswerPolicy) (DialogOption, bool) {
	for _, pattern := range p.Prefer {
		needle := strings.ToLower(strings.TrimSpace(pattern))
		if needle == "" {
			continue
		}
		for _, option := range options {
			if option.Selectable && strings.Contains(strings.ToLower(option.Label), needle) {
				return option, true
			}
		}
	}
	for _, option := range options {
		if option.Selectable {
			return option, true
		}
	}
	return DialogOption{}, false
}

// DialogRefused mirrors NamingRefused: the native preview reported the exact
// targeted option is no longer selectable, re-checked immediately before
// dispatch.
const DialogRefused Reason = "dialog_refused"

// DialogAnswerFacts is the fresh native preview EvaluateDialogAnswer
// re-checks immediately before dispatch. Accepted is native's current
// verdict that the exact window/index/label still names a selectable
// option; Unknown can never authorize a write.
type DialogAnswerFacts struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Accepted domain.Fact[bool]
}

type DialogAnswerRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       DialogAnswerFacts
}

// EvaluateDialogAnswer re-validates one already-observed dialog answer
// immediately before dispatch, the same shape EvaluateConfirmColonyNames
// uses. Admission proves the option is still there and selectable right now;
// whether the dialog actually closed is observed afterwards.
func EvaluateDialogAnswer(r DialogAnswerRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	value, ok := r.Action.DialogAnswer()
	canonical, err := domain.NewDialogAnswerAction(r.Action.ID(), value)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.Tick < r.MinimumTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.Tick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	accepted, known := f.Accepted.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !accepted {
		return refuse(DialogRefused)
	}
	return DraftDecision{Admitted: true}
}
