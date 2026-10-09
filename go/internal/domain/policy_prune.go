package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

// PolicyPruneAction deletes outfit, drug, food or reading policies, or
// allowed areas, that no bot planner assigned: a PolicyPruneIntent
// on Actions/Apply. Native first moves every player pawn still holding one
// onto its own per-pawn policy (or unrestricted), then deletes it; an id
// already gone applies again.
const PolicyPruneAction ActionKind = "policy_prune"

// PolicyDatabase names the native database a prune deletes from.
type PolicyDatabase string

const (
	OutfitPolicies  PolicyDatabase = "outfit"
	DrugPolicies    PolicyDatabase = "drug"
	FoodPolicies    PolicyDatabase = "food"
	ReadingPolicies PolicyDatabase = "reading"
	AllowedAreas    PolicyDatabase = "allowed_area"
)

func (d PolicyDatabase) valid() bool {
	switch d {
	case OutfitPolicies, DrugPolicies, FoodPolicies, ReadingPolicies, AllowedAreas:
		return true
	}
	return false
}

// PolicyPrune is an immutable, comparable value: the database and the
// canonical (sorted, deduplicated) load ids to delete, at least one.
type PolicyPrune struct {
	database PolicyDatabase
	ids      string
}

func NewPolicyPrune(database PolicyDatabase, ids []string) (PolicyPrune, error) {
	if !database.valid() {
		return PolicyPrune{}, errors.New("invalid policy database")
	}
	if len(ids) == 0 {
		return PolicyPrune{}, errors.New("policy prune needs ids")
	}
	rows := append([]string{}, ids...)
	sort.Strings(rows)
	for i, id := range rows {
		if !validID(id) || i > 0 && rows[i-1] == id {
			return PolicyPrune{}, errors.New("invalid policy id")
		}
	}
	data, _ := json.Marshal(rows)
	return PolicyPrune{database, string(data)}, nil
}

func (p PolicyPrune) Database() PolicyDatabase { return p.database }
func (p PolicyPrune) IDs() []string {
	var ids []string
	_ = json.Unmarshal([]byte(p.ids), &ids)
	return ids
}

func NewPolicyPruneAction(id ActionID, p PolicyPrune) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewPolicyPrune(p.database, p.IDs())
	if err != nil || canonical != p {
		return Action{}, errors.New("invalid policy prune")
	}
	return Action{id: id, kind: PolicyPruneAction, policyPrune: p}, nil
}

func (a Action) PolicyPrune() (PolicyPrune, bool) {
	return a.policyPrune, a.kind == PolicyPruneAction
}
