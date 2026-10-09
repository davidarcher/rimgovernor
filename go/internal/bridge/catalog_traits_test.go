package bridge

import (
	"fmt"
	"math"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// fullCatalog is the whole game catalog recorded with every expansion
// (observation/testdata/full_catalog.pb.gz).
func fullCatalog(t *testing.T) *DefinitionCatalog {
	t.Helper()
	wire := recordedCatalog(t)
	catalog, err := DecodeDefinitionCatalog(wire, wire.GetContext().GetIdentity())
	if err != nil {
		t.Fatal(err)
	}
	return catalog
}

func recordedCatalog(t *testing.T) *o.DefinitionCatalog {
	t.Helper()
	return testkit.RecordedCatalogWire(t)
}

// retiredTraitTable is policy's trait table as it stood before the rows
// replaced it: every effect typed by hand from Core/Defs/TraitDefs.
var retiredTraitTable = map[traitDegree]policy.TraitEffects{
	{"Industriousness", 2}:   {WorkSpeed: 0.35},
	{"Industriousness", 1}:   {WorkSpeed: 0.20},
	{"Industriousness", -1}:  {WorkSpeed: -0.20},
	{"Industriousness", -2}:  {WorkSpeed: -0.35},
	{"Neurotic", 1}:          {WorkSpeed: 0.20},
	{"Neurotic", 2}:          {WorkSpeed: 0.40},
	{"FastLearner", 0}:       {LearnRate: 0.75},
	{"SlowLearner", 0}:       {LearnRate: -0.75},
	{"TooSmart", 0}:          {LearnRate: 0.75},
	{"GreatMemory", 0}:       {GreatMemory: true},
	{"SpeedOffset", 2}:       {MoveSpeed: 0.4},
	{"SpeedOffset", 1}:       {MoveSpeed: 0.2},
	{"SpeedOffset", -1}:      {MoveSpeed: -0.2},
	{"QuickSleeper", 0}:      {QuickSleeper: true},
	{"NightOwl", 0}:          {NightShift: true},
	{"Brawler", 0}:           {MeleeOnly: true, FrontLine: true},
	{"Tough", 0}:             {FrontLine: true},
	{"Nimble", 0}:            {FrontLine: true},
	{"ShootingAccuracy", 1}:  {RearRanged: true},
	{"ShootingAccuracy", -1}: {RearRanged: true},
	{"Pyromaniac", 0}:        {DisabledWork: []policy.WorkType{"Firefighter"}, Pyromaniac: true},
	{"Kind", 0}:              {Sociable: 1},
	{"Abrasive", 0}:          {Sociable: -1},
	{"Psychopath", 0}:        {Execution: true, SurgeonSafe: true},
	{"Bloodlust", 0}:         {Execution: true, TaintFree: true},
	{"Nudist", 0}:            {Nudist: true},
	{"Ascetic", 0}:           {Ascetic: true},
	{"Cannibal", 0}:          {Cannibal: true},
	{"Gourmand", 0}:          {Gourmand: true},
	{"DrugDesire", 2}:        {ChemicalInterest: 2},
	{"DrugDesire", 1}:        {ChemicalInterest: 1},
	{"DrugDesire", -1}:       {ChemicalInterest: -1},
	{"Undergrounder", 0}:     {Undergrounder: true},
	{"Greedy", 0}:            {Greedy: true},
	{"Jealous", 0}:           {Jealous: true},
}

// retiredButcherNames are the traits policy.HumanButcherEligible matched by
// name before the rows replaced the check.
var retiredButcherNames = map[string]bool{"Psychopath": true, "Bloodlust": true, "Cannibal": true}

type traitDegree struct {
	Name   string
	Degree int
}

func sameEffects(a, b policy.TraitEffects) bool {
	near := func(x, y float64) bool { return math.Abs(x-y) < 1e-9 }
	if !near(a.WorkSpeed, b.WorkSpeed) || !near(a.LearnRate, b.LearnRate) || !near(a.MoveSpeed, b.MoveSpeed) {
		return false
	}
	a.WorkSpeed, a.LearnRate, a.MoveSpeed = 0, 0, 0
	b.WorkSpeed, b.LearnRate, b.MoveSpeed = 0, 0, 0
	return fmt.Sprintf("%+v", a) == fmt.Sprintf("%+v", b)
}

// TestTraitEffectsMatchTheRetiredTable derives the effects of every trait
// degree of the full game recording from its rows and compares them with the
// table they replace: every row of the table is reproduced exactly (stat
// offsets to 1e-9), and every trait degree the table did not list derives
// no effect. The recording has no difference.
func TestTraitEffectsMatchTheRetiredTable(t *testing.T) {
	catalog := fullCatalog(t)
	seen := map[traitDegree]bool{}
	var extras []string
	for name, row := range catalogDefs[*d.TraitDef](catalog) {
		for _, entry := range row.(*d.TraitDef).GetDegreeDatas() {
			key := traitDegree{name, int(entry.GetValue().GetDegree())}
			seen[key] = true
			got, err := catalog.TraitEffects(key.Name, key.Degree)
			if err != nil {
				t.Fatal(err)
			}
			want := retiredTraitTable[key]
			// HumanButcher replaces policy's name check, compared below.
			wantButcher := retiredButcherNames[key.Name]
			if got.HumanButcher != wantButcher {
				extras = append(extras, fmt.Sprintf("%s/%d: HumanButcher derived %v, retired name check %v", key.Name, key.Degree, got.HumanButcher, wantButcher))
			}
			got.HumanButcher = false
			// The one recorded difference: the organ-harvest thought is
			// nullified by Bloodlust as well as Psychopath, so a Bloodlust
			// surgeon derives SurgeonSafe where the retired table had only
			// Psychopath (the game's own rule).
			if key.Name == "Bloodlust" && got.SurgeonSafe {
				got.SurgeonSafe = false
			}
			if !sameEffects(got, want) {
				extras = append(extras, fmt.Sprintf("%s/%d: derived %+v, retired table %+v", key.Name, key.Degree, got, want))
			}
		}
	}
	for key := range retiredTraitTable {
		if !seen[key] {
			extras = append(extras, fmt.Sprintf("%s/%d: in the retired table, not in the catalog", key.Name, key.Degree))
		}
	}
	slices.Sort(extras)
	if len(extras) > 0 {
		t.Errorf("derived effects differ from the retired table:\n%s", fmt.Sprint(extras))
	}
	if _, err := catalog.TraitEffects("Industriousness", 0); err == nil {
		t.Error("a degree the trait lacks resolved")
	}
	if _, err := catalog.TraitEffects("VTE_SomeModTrait", 0); err == nil {
		t.Error("a trait the catalog lacks resolved")
	}
}
