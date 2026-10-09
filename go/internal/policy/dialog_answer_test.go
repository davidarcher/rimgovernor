package policy

import (
	"testing"
)

func TestChooseDialogOptionPrefersInOrderThenFirstSelectable(t *testing.T) {
	options := []DialogOption{{Index: 0, Label: "Attack", Selectable: true, Resolves: true}, {Index: 1, Label: "Trade with them", Selectable: true, Resolves: true}, {Index: 2, Label: "Ignore them", Selectable: true, Resolves: true}}
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{Prefer: DefaultDialogAnswerPrefer}); !ok || got.Index != 2 {
		t.Fatal(got, ok)
	}
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{Prefer: []string{"trade", "ignore"}}); !ok || got.Index != 1 {
		t.Fatal(got, ok)
	}
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{Prefer: []string{"give"}}); !ok || got.Index != 0 {
		t.Fatal("expected fallback to the first selectable option", got, ok)
	}
	options[0].Selectable = false
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{}); !ok || got.Index != 1 {
		t.Fatal("disabled option chosen", got, ok)
	}
	if _, ok := ChooseDialogOption([]DialogOption{{Label: "OK"}}, DialogAnswerPolicy{Prefer: []string{"OK"}}); ok {
		t.Fatal("no selectable option must yield no choice")
	}
}

// A translated game selects by the Keyed translation key, never by the
// English label; a key pattern is exact while a label pattern stays a
// substring, and the fallback prefers an option that closes the dialog over
// one that only links onward.
func TestChooseDialogOptionMatchesTranslationKeysAndPrefersResolving(t *testing.T) {
	meeting := []DialogOption{
		{Index: 0, Label: "Handeln", Keys: []string{"CaravanMeeting_Trade", "CommandTrade"}, Selectable: true, Resolves: true},
		{Index: 1, Label: "Angreifen", Keys: []string{"CaravanMeeting_Attack"}, Selectable: true, Resolves: true},
		{Index: 2, Label: "Weiterziehen", Keys: []string{"CaravanMeeting_MoveOn"}, Selectable: true, Resolves: true},
	}
	if got, ok := ChooseDialogOption(meeting, DialogAnswerPolicy{Prefer: DefaultDialogAnswerPrefer}); !ok || got.Index != 2 {
		t.Fatal("default policy must move on by key in a translated game", got, ok)
	}
	if got, ok := ChooseDialogOption(meeting, DialogAnswerPolicy{Prefer: []string{"caravanmeeting_attack"}}); !ok || got.Index != 1 {
		t.Fatal("key match is case-insensitive", got, ok)
	}
	if got, ok := ChooseDialogOption(meeting, DialogAnswerPolicy{Prefer: []string{"CaravanMeeting", "CaravanMeeting_Trade"}}); !ok || got.Index != 0 {
		t.Fatal("a key prefix must not match; the exact key must", got, ok)
	}
	if got, ok := ChooseDialogOption(meeting, DialogAnswerPolicy{Prefer: []string{"greif"}}); !ok || got.Index != 1 {
		t.Fatal("label patterns stay substrings", got, ok)
	}
	quest := []DialogOption{{Index: 0, Label: "Tell me more", Selectable: true}, {Index: 1, Label: "Accept", Selectable: true, Resolves: true}}
	if got, ok := ChooseDialogOption(quest, DialogAnswerPolicy{}); !ok || got.Index != 1 {
		t.Fatal("fallback must prefer the resolving option", got, ok)
	}
	quest[1].Selectable = false
	if got, ok := ChooseDialogOption(quest, DialogAnswerPolicy{}); !ok || got.Index != 0 {
		t.Fatal("a linking option answers when nothing resolves", got, ok)
	}
}

// The void node's choice is answered by key, never by position and never by
// embracing.
func TestVoidNodeDialogDisruptsPostponesAndNeverEmbraces(t *testing.T) {
	prefer := DialogAnswerPolicy{Prefer: DefaultDialogAnswerPrefer}
	option := func(i int32, key string, selectable bool) DialogOption {
		return DialogOption{Index: i, Label: key, Keys: []string{key}, Selectable: selectable, Resolves: true}
	}
	node := []DialogOption{option(0, VoidNodeEmbrace, true), option(1, VoidNodePostpone, true), option(2, VoidNodeDisrupt, true)}
	if got, ok := ChooseDialogOption(node, prefer); !ok || got.Index != 2 || VoidNodeDisruptAbsent(node) {
		t.Fatal("disrupt is chosen wherever it stands", got, ok)
	}
	node[2].Selectable = false
	if got, ok := ChooseDialogOption(node, prefer); !ok || got.Index != 1 || !VoidNodeDisruptAbsent(node) {
		t.Fatal("an absent disrupt postpones", got, ok)
	}
	node[1].Selectable = false
	if got, ok := ChooseDialogOption(node, prefer); ok {
		t.Fatal("embrace is never the fallback", got)
	}
	if VoidNodeDisruptAbsent([]DialogOption{option(0, "OK", true)}) {
		t.Fatal("another dialog has no disrupt to miss")
	}
}

// The monolith's dialogs answer by key: the investigate node tree
// takes its first option, the awakening confirmation confirms rather than goes
// back, and the level letter closes instead of opening a main tab.
func TestDefaultPolicyAnswersTheMonolithDialogs(t *testing.T) {
	prefer := DialogAnswerPolicy{Prefer: DefaultDialogAnswerPrefer}
	investigate := []DialogOption{{Index: 0, Label: "Investigate", Selectable: true, Resolves: true}, {Index: 1, Label: "Walk away", Selectable: true, Resolves: true}}
	if got, ok := ChooseDialogOption(investigate, prefer); !ok || got.Index != 0 {
		t.Fatal(got, ok)
	}
	awaken := []DialogOption{{Index: 0, Label: "Confirm", Keys: []string{"Confirm"}, Selectable: true, Resolves: true}, {Index: 1, Label: "Go back", Keys: []string{"GoBack"}, Selectable: true, Resolves: true}}
	if got, ok := ChooseDialogOption(awaken, prefer); !ok || got.Index != 0 {
		t.Fatal(got, ok)
	}
	letter := []DialogOption{{Index: 0, Label: "View quest", Keys: []string{"VoidMonolithViewQuest"}, Selectable: true, Resolves: true}, {Index: 1, Label: "Close", Keys: []string{"Close"}, Selectable: true, Resolves: true}}
	if got, ok := ChooseDialogOption(letter, prefer); !ok || got.Index != 1 {
		t.Fatal(got, ok)
	}
}
