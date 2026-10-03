package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestPawnAnomalyCreepJoiner (#1740): a creepjoiner's form, benefit and
// whether its downside has fired lift as read; a pawn with no tracker is
// known not to be one; a failed read leaves the block unknown; the row has
// no field for the hidden downside def.
func TestPawnAnomalyCreepJoiner(t *testing.T) {
	row := &o.PawnAnomaly{Entity: proto.Bool(false), Creepjoiner: &o.CreepJoinerState{Form: proto.String("Gaunt"), Benefit: proto.String("Smith"), DownsideTriggered: proto.Bool(false)}}
	if err := validatePawnAnomaly(row); err != nil {
		t.Fatal(err)
	}
	a, _ := PawnAnomaly(row).Value()
	joiner, ok := a.CreepJoiner.Value()
	if !ok || joiner == nil {
		t.Fatal("creepjoiner", joiner, ok)
	}
	if form, _ := joiner.Form.Value(); form != "Gaunt" {
		t.Fatal("form", form)
	}
	if triggered, ok := joiner.DownsideTriggered.Value(); !ok || triggered {
		t.Fatal("downside triggered", triggered, ok)
	}
	if o.File_observations_proto.Messages().ByName("CreepJoinerState").Fields().ByName("downside") != nil {
		t.Fatal("the pawn row must not carry the hidden downside")
	}
	plain, _ := PawnAnomaly(&o.PawnAnomaly{Entity: proto.Bool(false)}).Value()
	if joiner, ok := plain.CreepJoiner.Value(); !ok || joiner != nil {
		t.Fatal("a pawn with no tracker is known not a creepjoiner", joiner, ok)
	}
	failed := &o.PawnAnomaly{Issues: []*o.ReadIssue{{Field: proto.String("creepjoiner"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
	if err := validatePawnAnomaly(failed); err != nil {
		t.Fatal(err)
	}
	a, _ = PawnAnomaly(failed).Value()
	if _, ok := a.CreepJoiner.Value(); ok {
		t.Fatal("a failed creepjoiner read must stay unknown")
	}
	bad := &o.PawnAnomaly{Creepjoiner: &o.CreepJoinerState{Form: proto.String("")}}
	if validatePawnAnomaly(bad) == nil {
		t.Fatal("invalid form name accepted")
	}
}

// TestCreepJoinerCatalogDecode (#1740): the defs index by name; duplicates,
// nonfinite numbers and inverted skill ranges are refused; the def's -1
// sentinel decodes verbatim.
func TestCreepJoinerCatalogDecode(t *testing.T) {
	fixture := func() *o.AnomalyCatalog {
		return &o.AnomalyCatalog{
			CreepjoinerForms:    []*o.CreepJoinerFormRow{{DefName: proto.String("Gaunt"), Weight: proto.Float64(1), MinCombatPoints: proto.Float64(-1), Requires: []string{"Smith"}}},
			CreepjoinerBenefits: []*o.CreepJoinerBenefitRow{{DefName: proto.String("Smith"), Traits: []string{"Kind"}, Skills: []*o.CreepJoinerSkillRange{{Skill: proto.String("Crafting"), Min: proto.Int32(8), Max: proto.Int32(12)}}}},
			CreepjoinerDownsides: []*o.CreepJoinerDownsideRow{{DefName: proto.String("CrumblingMind"), TriggersAfterDaysMin: proto.Float64(1), TriggersAfterDaysMax: proto.Float64(3),
				TriggerMtbDays: proto.Float64(-1), SurgicalInspectionHint: proto.Bool(false)}},
		}
	}
	got, err := DecodeAnomalyCatalog(fixture())
	if err != nil || got.CreepJoinerForms["Gaunt"] == nil || got.CreepJoinerBenefits["Smith"] == nil || got.CreepJoinerDownsides["CrumblingMind"] == nil {
		t.Fatalf("%+v %v", got, err)
	}
	for name, mutate := range map[string]func(*o.AnomalyCatalog){
		"duplicate form":  func(v *o.AnomalyCatalog) { v.CreepjoinerForms = append(v.CreepjoinerForms, v.CreepjoinerForms[0]) },
		"nan weight":      func(v *o.AnomalyCatalog) { v.CreepjoinerForms[0].Weight = proto.Float64(math.NaN()) },
		"inverted skill":  func(v *o.AnomalyCatalog) { v.CreepjoinerBenefits[0].Skills[0].Min = proto.Int32(20) },
		"inf trigger":     func(v *o.AnomalyCatalog) { v.CreepjoinerDownsides[0].TriggersAfterDaysMax = proto.Float64(math.Inf(1)) },
		"duplicate trait": func(v *o.AnomalyCatalog) { v.CreepjoinerBenefits[0].Traits = []string{"Kind", "Kind"} },
		"unnamed":         func(v *o.AnomalyCatalog) { v.CreepjoinerDownsides[0].DefName = nil },
	} {
		v := fixture()
		mutate(v)
		if _, err := DecodeAnomalyCatalog(v); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

// A creepjoiner letter (#1740) may have no timeout: its expiry is the game's
// negative sentinel, mirrored; a missing expiry is still refused.
func TestJoinerLetterCensusCreepJoinerWithoutTimeout(t *testing.T) {
	row := &o.JoinerLetter{LetterId: proto.Int32(3), SnapshotToken: proto.String("creepjoiner-token"), PawnId: proto.String("Pawn_4"), AcceptLabel: proto.String("Accept"),
		CanAccept: proto.Bool(true), Creepjoiner: proto.Bool(true)}
	if err := validateJoinerLetters([]*o.JoinerLetter{row}, 99); err == nil {
		t.Fatal("accepted a letter with no expiry fact")
	}
	row.ExpiresTick = proto.Int64(-1)
	if err := validateJoinerLetters([]*o.JoinerLetter{row}, 99); err != nil {
		t.Fatal(err)
	}
	row.ExpiresTick = proto.Int64(99)
	if err := validateJoinerLetters([]*o.JoinerLetter{row}, 99); err == nil {
		t.Fatal("accepted an expired creepjoiner letter")
	}
}
