package domain

import (
	"encoding/json"
	"errors"
	"math"
	"sort"
)

const ApparelPolicyAction ActionKind = "apparel_policy"

// ApparelPolicySpec is the complete desired native filter. Token binds the
// observed assignment and filter state; intervening edits invalidate it.
type ApparelPolicySpec struct {
	Pawn                   PawnID
	Token, Name            string
	Definitions            []string
	MinHP, MaxHP           float64
	MinQuality, MaxQuality int32
}
type ApparelPolicy struct{ encoded string }

func NewApparelPolicy(v ApparelPolicySpec) (ApparelPolicy, error) {
	if !validID(string(v.Pawn)) || !validID(v.Token) || !validID(v.Name) || len(v.Definitions) == 0 || len(v.Definitions) > 512 || math.IsNaN(v.MinHP) || math.IsNaN(v.MaxHP) || v.MinHP < 0 || v.MaxHP > 1 || v.MinHP > v.MaxHP || v.MinQuality < 0 || v.MaxQuality > 6 || v.MinQuality > v.MaxQuality {
		return ApparelPolicy{}, errors.New("invalid apparel policy")
	}
	v.Definitions = append([]string{}, v.Definitions...)
	sort.Strings(v.Definitions)
	for i, d := range v.Definitions {
		if !validID(d) || i > 0 && d == v.Definitions[i-1] {
			return ApparelPolicy{}, errors.New("invalid apparel definition")
		}
	}
	b, err := json.Marshal(v)
	if err != nil || len(b) > 32768 {
		return ApparelPolicy{}, errors.New("apparel policy exceeds bound")
	}
	return ApparelPolicy{string(b)}, nil
}
func (v ApparelPolicy) Spec() ApparelPolicySpec {
	var s ApparelPolicySpec
	_ = json.Unmarshal([]byte(v.encoded), &s)
	return s
}
func (v ApparelPolicy) Encoded() string { return v.encoded }
func (v ApparelPolicy) Pawn() PawnID    { return v.Spec().Pawn }
func NewApparelPolicyAction(id ActionID, v ApparelPolicy) (Action, error) {
	canonical, err := NewApparelPolicy(v.Spec())
	if !validID(string(id)) || err != nil || canonical != v {
		return Action{}, errors.New("invalid apparel policy action")
	}
	return Action{id: id, kind: ApparelPolicyAction, apparelPolicy: v}, nil
}
func (a Action) ApparelPolicy() (ApparelPolicy, bool) {
	return a.apparelPolicy, a.kind == ApparelPolicyAction
}
