package domain

import (
	"encoding/json"
	"errors"
	"math"
	"sort"
)

// DrugPolicyAction writes one drug policy's full contents: a
// DrugPolicyIntent on Actions/Apply. The policy labelled Name (a pawn's
// short name) carries exactly the entries and every other drug is off;
// native makes it when missing. PawnSettingsIntent.drug_policy assigns it.
const DrugPolicyAction ActionKind = "drug_policy"

// MaxDrugCarry bounds DrugPolicyEntry.TakeToInventory.
const MaxDrugCarry = 10

// DrugPolicyEntry is one drug's row of a drug policy (vanilla
// DrugPolicyEntry). An entry allows something: joy, addiction, scheduled
// use or a carried count; a drug with none of them is off and is left out.
// DaysFrequency, OnlyIfMoodBelow and OnlyIfJoyBelow shape scheduled use.
type DrugPolicyEntry struct {
	Drug                                           string
	Joy, Addiction, Scheduled                      bool
	DaysFrequency, OnlyIfMoodBelow, OnlyIfJoyBelow float64
	TakeToInventory                                int
}

// Off reports whether the entry allows nothing.
func (e DrugPolicyEntry) Off() bool {
	return !e.Joy && !e.Addiction && !e.Scheduled && e.TakeToInventory == 0
}

func (e DrugPolicyEntry) valid() bool {
	unit := func(v float64) bool { return !math.IsNaN(v) && v >= 0 && v <= 1 }
	return validID(e.Drug) && !e.Off() && !math.IsNaN(e.DaysFrequency) && e.DaysFrequency > 0 && e.DaysFrequency <= 1000 &&
		unit(e.OnlyIfMoodBelow) && unit(e.OnlyIfJoyBelow) && e.TakeToInventory >= 0 && e.TakeToInventory <= MaxDrugCarry
}

// DrugPolicy is an immutable, comparable value: the label and the
// canonical (sorted by drug, one per drug) entries, possibly none.
type DrugPolicy struct {
	name, entries string
}

func NewDrugPolicy(name string, entries []DrugPolicyEntry) (DrugPolicy, error) {
	if !validID(name) || len(name) > 80 {
		return DrugPolicy{}, errors.New("a drug policy needs a name")
	}
	rows := append([]DrugPolicyEntry{}, entries...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].Drug < rows[j].Drug })
	for i, e := range rows {
		if !e.valid() || i > 0 && rows[i-1].Drug == e.Drug {
			return DrugPolicy{}, errors.New("invalid drug policy entry")
		}
	}
	data, _ := json.Marshal(rows)
	if len(data) > 30000 {
		return DrugPolicy{}, errors.New("drug policy exceeds storage bound")
	}
	return DrugPolicy{name, string(data)}, nil
}

func (p DrugPolicy) Name() string { return p.name }
func (p DrugPolicy) Entries() []DrugPolicyEntry {
	rows := []DrugPolicyEntry{}
	_ = json.Unmarshal([]byte(p.entries), &rows)
	return rows
}

func NewDrugPolicyAction(id ActionID, p DrugPolicy) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewDrugPolicy(p.name, p.Entries())
	if err != nil || canonical != p {
		return Action{}, errors.New("invalid drug policy")
	}
	return Action{id: id, kind: DrugPolicyAction, drugPolicy: p}, nil
}

func (a Action) DrugPolicy() (DrugPolicy, bool) {
	return a.drugPolicy, a.kind == DrugPolicyAction
}
