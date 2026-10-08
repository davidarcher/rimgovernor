package policy

import (
	"strings"
)

// DialogOption is one option of the force-pausing choice dialog the colony
// census observed (ColonyFactsSnapshot.dialog), in native order. Keys are
// the language-neutral Keyed translation keys native recovered for the
// label (#179); Selectable already excludes disabled rows, hyperlinks and
// options that open another window instead of resolving or linking.
type DialogOption struct {
	Index      int32
	Label      string
	Keys       []string
	Selectable bool
	Resolves   bool
}

// DialogAnswerPolicy decides which option answers a choice dialog the game
// opened by itself (#156). Prefer is an ordered list of patterns; a pattern
// matches an option when it equals one of the option's translation keys
// (case-insensitive, so the same policy holds in every game language) or is
// a case-insensitive substring of its label (a mod's literal text). The first
// pattern with a selectable match wins. When nothing matches, the default
// answer is the first selectable option that closes the dialog, then the
// first that links onward: the position the game gives its own primary
// answer (DiaOption.DefaultOK, CaravanDemand's "Give", a quest's first
// choice).
type DialogAnswerPolicy struct{ Prefer []string }

// DefaultDialogAnswerPrefer is the passive, non-escalating preference order,
// by Core translation key: dismiss an informational dialog, move on from a
// met caravan rather than trade or attack, keep watching past a game-over
// prompt, and pay a demand rather than fight a caravan the colony did not
// choose to arm. Close dismisses a monolith level letter without opening a
// main tab (#2437).
var DefaultDialogAnswerPrefer = []string{"OK", "Close", "Ignore", "CaravanMeeting_MoveOn", "GameOverKeepWatching", "CaravanDemand_Give"}

// DialogPatternMatches reports whether one Prefer pattern names the option.
func DialogPatternMatches(pattern string, option DialogOption) bool {
	needle := strings.TrimSpace(pattern)
	if needle == "" {
		return false
	}
	for _, key := range option.Keys {
		if strings.EqualFold(key, needle) {
			return true
		}
	}
	return strings.Contains(strings.ToLower(option.Label), strings.ToLower(needle))
}

// The void node's choice (CompVoidNode.OpenDialog, #2438): VoidNodeDisrupt
// ends the awakening quest by collapsing the monolith, VoidNodeEmbrace is the
// irreversible alternative the colony never takes, VoidNodePostpone closes the
// dialog and leaves the node touchable.
const (
	VoidNodeDisrupt  = "VoidNodeDisrupt"
	VoidNodeEmbrace  = "VoidNodeEmbrace"
	VoidNodePostpone = "VoidNodePostpone"
)

func voidNodeOption(options []DialogOption, key string) (DialogOption, bool) {
	for _, option := range options {
		if option.Selectable && DialogPatternMatches(key, DialogOption{Keys: option.Keys}) {
			return option, true
		}
	}
	return DialogOption{}, false
}

// IsVoidNodeDialog is whether the options are the void node's choice.
func IsVoidNodeDialog(options []DialogOption) bool {
	for _, option := range options {
		for _, key := range []string{VoidNodeDisrupt, VoidNodeEmbrace, VoidNodePostpone} {
			if DialogPatternMatches(key, DialogOption{Keys: option.Keys}) {
				return true
			}
		}
	}
	return false
}

// VoidNodeDisruptAbsent is whether the void node's choice offers no
// selectable VoidNodeDisrupt: the dialog is then postponed, and the caller
// names the absence loudly.
func VoidNodeDisruptAbsent(options []DialogOption) bool {
	_, ok := voidNodeOption(options, VoidNodeDisrupt)
	return IsVoidNodeDialog(options) && !ok
}

// ChooseDialogOption applies the policy to the observed options. ok is false
// when no option can answer the dialog at all (every option disabled, a
// hyperlink or a window-opening action), in which case the controller has no
// method and the player must intervene. The void node's choice is answered
// only by its key: VoidNodeDisrupt, else VoidNodePostpone, never by the
// default position and never VoidNodeEmbrace.
func ChooseDialogOption(options []DialogOption, p DialogAnswerPolicy) (DialogOption, bool) {
	if IsVoidNodeDialog(options) {
		if option, ok := voidNodeOption(options, VoidNodeDisrupt); ok {
			return option, true
		}
		return voidNodeOption(options, VoidNodePostpone)
	}
	for _, pattern := range p.Prefer {
		for _, option := range options {
			if option.Selectable && DialogPatternMatches(pattern, option) {
				return option, true
			}
		}
	}
	for _, resolves := range []bool{true, false} {
		for _, option := range options {
			if option.Selectable && option.Resolves == resolves {
				return option, true
			}
		}
	}
	return DialogOption{}, false
}
