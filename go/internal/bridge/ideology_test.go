package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func ideologySnapshot() *o.IdeologySnapshot {
	return &o.IdeologySnapshot{Context: pbContext(), IdeoId: proto.String("Ideo_1"), Memes: []string{"Structure_Animist"},
		Precepts:          []*o.IdeoPrecept{{Id: proto.String("Precept_1"), DefName: proto.String("Slavery_Abhorrent")}},
		Roles:             []*o.IdeoRole{{Id: proto.String("Precept_2"), DefName: proto.String("IdeoRole_Moral"), Active: proto.Bool(true), Pawns: []*c.Ref{{Id: proto.String("Thing_Human1")}}}},
		Rituals:           []*o.IdeoRitual{{Id: proto.String("Precept_3"), DefName: proto.String("Ritual_Sermon"), Pattern: proto.String("Sermon"), LastFinishedTick: proto.Int32(1200), ActiveObligations: proto.Int32(1), RepeatPenaltyActive: proto.Bool(false), Running: proto.Bool(false)}},
		Buildings:         []*o.IdeoBuilding{{Id: proto.String("Precept_4"), DefName: proto.String("IdeoBuilding_Altar"), Building: proto.String("Altar")}},
		ObligationsActive: proto.Bool(true), Believers: proto.Int32(4), MinBelieversForObligations: proto.Int32(3)}
}


// TestIdeologySectionDecodesAgainstCatalog (#1654): the held precepts, roles,
// rituals and buildings resolve in the catalog and reach policy as facts.
func TestIdeologySectionDecodesAgainstCatalog(t *testing.T) {
	ideology, err := DecodeIdeology(ideologySnapshot(), pbIdentity(), ideologyCatalogRows())
	if err != nil {
		t.Fatal(err)
	}
	f := ideology.Facts
	if f.IdeoID != "Ideo_1" || !f.ObligationsActive || f.Believers != 4 || f.MinBelievers != 3 || len(f.Memes) != 1 {
		t.Fatalf("%+v", f)
	}
	if len(f.Roles) != 1 || !f.Roles[0].Active || f.Roles[0].Pawns[0] != domain.PawnID("Thing_Human1") {
		t.Fatalf("%+v", f.Roles)
	}
	if f.Rituals[0].LastFinishedTick != 1200 || f.Rituals[0].ActiveObligations != 1 || f.Rituals[0].Pattern != "Sermon" {
		t.Fatalf("%+v", f.Rituals)
	}
	effects := ideology.EffectsInForce()
	if len(effects) != 3 || effects[0].Precept.Name != "Slavery_Abhorrent" || !effects[0].Effect.Penalises() {
		t.Fatalf("%+v", effects)
	}
	if got := ideology.RequiredBuildings(); len(got) != 2 || got[0] != "Altar" {
		t.Fatalf("required buildings %v", got)
	}
}

// TestIdeologySectionRefusesWhatTheCatalogLacks: an unknown def, a repeated
// precept id or a section without catalog defs is a contract failure, never
// a skipped row.
func TestIdeologySectionRefusesWhatTheCatalogLacks(t *testing.T) {
	for name, change := range map[string]func(*o.IdeologySnapshot){
		"unknown-precept":  func(v *o.IdeologySnapshot) { v.Precepts[0].DefName = proto.String("Nope") },
		"unknown-role":     func(v *o.IdeologySnapshot) { v.Roles[0].DefName = proto.String("Nope") },
		"unknown-meme":     func(v *o.IdeologySnapshot) { v.Memes = []string{"Nope"} },
		"unknown-pattern":  func(v *o.IdeologySnapshot) { v.Rituals[0].Pattern = proto.String("Nope") },
		"duplicate-id":     func(v *o.IdeologySnapshot) { v.Buildings[0].Id = v.Precepts[0].Id },
		"duplicate-pawn":   func(v *o.IdeologySnapshot) { v.Roles[0].Pawns = append(v.Roles[0].Pawns, v.Roles[0].Pawns[0]) },
		"role-no-state":    func(v *o.IdeologySnapshot) { v.Roles[0].Active = nil },
		"no-ideo":          func(v *o.IdeologySnapshot) { v.IdeoId = nil },
		"negative-believe": func(v *o.IdeologySnapshot) { v.Believers = proto.Int32(-1) },
		"no-last-tick":     func(v *o.IdeologySnapshot) { v.Rituals[0].LastFinishedTick = nil },
		"other-world":      func(v *o.IdeologySnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
	} {
		t.Run(name, func(t *testing.T) {
			v := ideologySnapshot()
			change(v)
			if _, err := DecodeIdeology(v, pbIdentity(), ideologyCatalogRows()); err == nil {
				t.Fatal("bad ideology section accepted")
			}
		})
	}
	if _, err := DecodeIdeology(ideologySnapshot(), pbIdentity(), nil); err == nil {
		t.Fatal("section without catalog defs accepted")
	}
}

// TestRoutineFrameCarriesTheIdeology: a frame with an ideology section
// decodes it against the load's catalog; a frame without one leaves it nil
// (unknown), and a section whose catalog has no Ideology defs fails.
func TestRoutineFrameCarriesTheIdeology(t *testing.T) {
	catalog := ideologyCatalogRows()
	frame := &o.BundleSnapshot{Context: pbContext(), Ideology: ideologySnapshot()}
	got, err := DecodeRoutineFrame(frame, catalog)
	if err != nil || got.Ideology == nil || got.Ideology.Facts.IdeoID != "Ideo_1" {
		t.Fatalf("%+v %v", got.Ideology, err)
	}
	frame.Ideology = nil
	if got, err = DecodeRoutineFrame(frame, catalog); err != nil || got.Ideology != nil {
		t.Fatalf("%+v %v", got.Ideology, err)
	}
	frame.Ideology = ideologySnapshot()
	if _, err = DecodeRoutineFrame(frame, &DefinitionCatalog{}); err == nil {
		t.Fatal("ideology section accepted without catalog defs")
	}
}

// TestIdeologySectionIsHeldWhileOmitted (#1347): native omits an unchanged
// ideology section; the hold serves the last carried one at each frame's
// tick, and a seq it does not hold is a gap that asks for a keyframe.
func TestIdeologySectionIsHeldWhileOmitted(t *testing.T) {
	var hold sectionHold
	frame := func(tick int64, seq uint64, carry bool) *o.BundleSnapshot {
		v := &o.BundleSnapshot{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(7)},
			Watermarks: []*o.SectionWatermark{{Section: proto.String("ideology"), Seq: proto.Uint64(seq), CapturedTick: proto.Int64(1)}}}
		if carry {
			v.Ideology = ideologySnapshot()
		}
		return v
	}
	if held, gap := hold.fill(frame(10, 1, true)); held != 0 || gap {
		t.Fatalf("keyframe: held %d gap %v", held, gap)
	}
	next := frame(11, 1, false)
	if held, gap := hold.fill(next); held != 1 || gap || next.Ideology.GetContext().GetTick() != 11 || next.Ideology.GetIdeoId() != "Ideo_1" {
		t.Fatalf("omitted frame: held %d gap %v %+v", held, gap, next.Ideology)
	}
	if _, gap := hold.fill(frame(12, 3, false)); !gap {
		t.Fatal("a missed ideology change did not ask for a keyframe")
	}
}

// TestPawnPolicyInputsCarryIdeoCertainty: certainty rides the pawn row's
// policy inputs; a pawn without an ideoligion has none, and a certainty
// outside 0..1 or without an ideoligion is refused.
func TestPawnPolicyInputsCarryIdeoCertainty(t *testing.T) {
	row := &o.PawnPolicyInputs{IdeoId: proto.String("Ideo_1"), IdeoCertainty: proto.Float64(0.75)}
	if err := validatePolicyInputs(row); err != nil {
		t.Fatal(err)
	}
	if certainty, known := PawnPolicyInputs(row).Value(); !known || func() bool { v, ok := certainty.IdeoCertainty.Value(); return !ok || v != 0.75 }() {
		t.Fatalf("%+v", certainty)
	}
	if certainty, _ := PawnPolicyInputs(&o.PawnPolicyInputs{}).Value(); func() bool { _, ok := certainty.IdeoCertainty.Value(); return ok }() {
		t.Fatal("certainty known without an ideoligion")
	}
	for name, bad := range map[string]*o.PawnPolicyInputs{
		"above-one":    {IdeoId: proto.String("Ideo_1"), IdeoCertainty: proto.Float64(1.5)},
		"negative":     {IdeoId: proto.String("Ideo_1"), IdeoCertainty: proto.Float64(-0.1)},
		"no-ideo":      {IdeoCertainty: proto.Float64(0.5)},
		"not-a-number": {IdeoId: proto.String("Ideo_1"), IdeoCertainty: proto.Float64(math.NaN())},
	} {
		if validatePolicyInputs(bad) == nil {
			t.Fatalf("%s accepted", name)
		}
	}
}
