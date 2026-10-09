package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// ReadingPolicyAction writes one reading policy's contents: a
// ReadingPolicyIntent on Actions/Apply. The policy labelled Name (a pawn's
// short name) allows exactly the book definitions; native makes it when
// missing. PawnSettingsIntent.reading_policy assigns it.
const ReadingPolicyAction ActionKind = "reading_policy"

// ReadingPolicy is an immutable, comparable value: the label and the
// canonical (sorted, deduplicated) allowed book definitions, possibly none.
type ReadingPolicy struct {
	name, defs string
}

func NewReadingPolicy(name string, defs []string) (ReadingPolicy, error) {
	if !validID(name) || len(name) > 80 {
		return ReadingPolicy{}, errors.New("a reading policy needs a name")
	}
	rows := append([]string{}, defs...)
	sort.Strings(rows)
	for i, d := range rows {
		if !validID(d) || i > 0 && rows[i-1] == d {
			return ReadingPolicy{}, errors.New("invalid reading policy definition")
		}
	}
	data, _ := json.Marshal(rows)
	return ReadingPolicy{name, string(data)}, nil
}

func (p ReadingPolicy) Name() string { return p.name }
func (p ReadingPolicy) Definitions() []string {
	rows := []string{}
	_ = json.Unmarshal([]byte(p.defs), &rows)
	return rows
}

func NewReadingPolicyAction(id ActionID, p ReadingPolicy) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewReadingPolicy(p.name, p.Definitions())
	if err != nil || canonical != p {
		return Action{}, errors.New("invalid reading policy")
	}
	return Action{id: id, kind: ReadingPolicyAction, readingPolicy: p}, nil
}

func (a Action) ReadingPolicy() (ReadingPolicy, bool) {
	return a.readingPolicy, a.kind == ReadingPolicyAction
}
