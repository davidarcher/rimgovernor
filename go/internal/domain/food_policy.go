package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// FoodPolicyAction writes one food policy's contents: a
// FoodPolicyIntent on Actions/Apply. The policy labelled Name (a pawn's
// short name) allows exactly the food definitions; native makes it when
// missing. PawnSettingsIntent.food_policy assigns it.
const FoodPolicyAction ActionKind = "food_policy"

// FoodPolicy is an immutable, comparable value: the label and the
// canonical (sorted, deduplicated) allowed food definitions, possibly none.
type FoodPolicy struct {
	name, defs string
}

func NewFoodPolicy(name string, defs []string) (FoodPolicy, error) {
	if !validID(name) || len(name) > 80 {
		return FoodPolicy{}, errors.New("a food policy needs a name")
	}
	rows := append([]string{}, defs...)
	sort.Strings(rows)
	for i, d := range rows {
		if !validID(d) || i > 0 && rows[i-1] == d {
			return FoodPolicy{}, errors.New("invalid food policy definition")
		}
	}
	data, _ := json.Marshal(rows)
	return FoodPolicy{name, string(data)}, nil
}

func (p FoodPolicy) Name() string { return p.name }
func (p FoodPolicy) Definitions() []string {
	rows := []string{}
	_ = json.Unmarshal([]byte(p.defs), &rows)
	return rows
}

func NewFoodPolicyAction(id ActionID, p FoodPolicy) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewFoodPolicy(p.name, p.Definitions())
	if err != nil || canonical != p {
		return Action{}, errors.New("invalid food policy")
	}
	return Action{id: id, kind: FoodPolicyAction, foodPolicy: p}, nil
}

func (a Action) FoodPolicy() (FoodPolicy, bool) {
	return a.foodPolicy, a.kind == FoodPolicyAction
}
