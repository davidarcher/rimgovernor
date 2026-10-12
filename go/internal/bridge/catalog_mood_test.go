package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// TestMoodModelFromRecordedCatalog reads the break curve and the priced
// thoughts' stages from the recorded game (#2954).
func TestMoodModelFromRecordedCatalog(t *testing.T) {
	m, err := fullCatalog(t).moodModel()
	if err != nil {
		t.Fatal(err)
	}
	if !m.Break.Valid() || math.Abs(m.Break.Threshold-0.35) > 1e-6 {
		t.Fatalf("break model = %+v", m.Break)
	}
	if dark, ok := m.DarkPoints(); !ok || dark != 5 {
		t.Errorf("dark = %v %v, want 5", dark, ok)
	}
	// A bedroom of stage 2 (mediocre) is +1; the same stage in barracks is -4.
	if got, ok := m.LostPoints(policy.LostRoleEffect{Effect: policy.EffectSleepMood, Role: policy.RoomRoleBedroom, Got: policy.RoomRoleBarracks}, 2); !ok || got != 5 {
		t.Errorf("bedroom to barracks at stage 2 = %v %v, want 5", got, ok)
	}
	// Throne and worship carry no mood; a role with no thought gives none.
	if got, ok := m.LostPoints(policy.LostRoleEffect{Effect: policy.EffectThrone, Role: policy.RoomRoleThroneRoom}, 0); !ok || got != 0 {
		t.Errorf("throne = %v %v", got, ok)
	}
	// Every priced role is a thought the catalog has.
	for _, role := range []policy.RoomRole{policy.RoomRoleBedroom, policy.RoomRoleBarracks, policy.RoomRoleHospital, policy.RoomRolePrisonCell, policy.RoomRolePrisonBarracks} {
		if _, ok := m.LostPoints(policy.LostRoleEffect{Effect: policy.EffectSleepMood, Role: role, Got: role}, 3); !ok {
			t.Errorf("role %s has no thought stages", role)
		}
	}
}

// TestMoodModelBreaks projects a hand-built catalog: generic breaks fold into
// their tier by commonality and mean state length, a hediff break uses the
// disappear range, and gated, anomalous and unpriced breaks stay out.
func TestMoodModelBreaks(t *testing.T) {
	rows := map[protoreflect.FullName]map[string]proto.Message{}
	add := func(m proto.Message, name string) {
		full := m.ProtoReflect().Descriptor().FullName()
		if rows[full] == nil {
			rows[full] = map[string]proto.Message{}
		}
		rows[full][name] = m
	}
	add(&d.StatDef{DefName: "MentalBreakThreshold", DefaultBaseValue: 0.5}, "MentalBreakThreshold")
	minor := d.MentalBreakIntensity_MENTAL_BREAK_INTENSITY_MINOR
	major := d.MentalBreakIntensity_MENTAL_BREAK_INTENSITY_MAJOR
	// A: 10000 ticks fixed (1/6 day); B: 20000 fixed; both minor, weights 1 and 3.
	add(&d.MentalStateDef{DefName: "A", MinTicksBeforeRecovery: 10000, MaxTicksBeforeRecovery: 10000}, "A")
	add(&d.MentalStateDef{DefName: "B", MinTicksBeforeRecovery: 20000, MaxTicksBeforeRecovery: 20000}, "B")
	add(&d.MentalBreakDef{DefName: "A", MentalState: "A", BaseCommonality: 1, Intensity: minor}, "A")
	add(&d.MentalBreakDef{DefName: "B", MentalState: "B", BaseCommonality: 3, Intensity: minor}, "B")
	// Gated, anomalous and unpriced breaks stay out.
	add(&d.MentalBreakDef{DefName: "Gated", MentalState: "A", BaseCommonality: 9, Intensity: minor, RequiredTrait: "Pyromaniac"}, "Gated")
	add(&d.MentalBreakDef{DefName: "Weird", MentalState: "A", BaseCommonality: 9, Intensity: minor, AnomalousBreak: true}, "Weird")
	add(&d.MentalBreakDef{DefName: "RunWild", BaseCommonality: 9, Intensity: major}, "RunWild")
	// Catatonic is a hediff that disappears after 1 to 3 days.
	disappears := &d.HediffCompProperties_Disappears{DisappearsAfterTicks: &d.IntRange{Min: 60000, Max: 180000}}
	add(&d.HediffDef{DefName: "CatatonicBreakdown", Comps: []*d.Opt_HediffCompPropertiesAny{{Value: &d.HediffCompPropertiesAny{
		Value: &d.HediffCompPropertiesAny_HediffCompProperties_Disappears{HediffCompProperties_Disappears: disappears}}}}}, "CatatonicBreakdown")
	add(&d.MentalBreakDef{DefName: "Catatonic", BaseCommonality: 2, Intensity: major}, "Catatonic")
	m, err := (&DefinitionCatalog{Defs: rows}).moodModel()
	if err != nil {
		t.Fatal(err)
	}
	if m.Break.Threshold != 0.5 {
		t.Errorf("threshold = %v", m.Break.Threshold)
	}
	want := policy.MoodTierModel{Weight: 4, Days: (1*(1.0/6) + 3*(1.0/3)) / 4}
	if got := m.Break.Tiers[policy.TierMinor]; math.Abs(got.Weight-want.Weight) > 1e-9 || math.Abs(got.Days-want.Days) > 1e-9 {
		t.Errorf("minor = %+v, want %+v", got, want)
	}
	if got := m.Break.Tiers[policy.TierMajor]; got.Weight != 2 || got.Days != 2 {
		t.Errorf("major = %+v, want weight 2 days 2 (Catatonic only)", got)
	}
}
