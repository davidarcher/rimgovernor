package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func anomalyPawnFixture() *o.PawnState {
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("Thing_Fingerspike1")}, Anomaly: &o.PawnAnomaly{
		Entity: proto.Bool(true), Mutant: proto.Bool(false), Shambler: proto.Bool(false), MinContainmentStrength: proto.Float64(30),
		Held: &o.HeldState{Held: proto.Bool(true), Platform: &c.Ref{Id: proto.String("Building_HoldingPlatform1")},
			Mode: o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_STUDY.Enum(), Escaping: proto.Bool(false)},
		Study: &o.StudyState{StudyEnabled: proto.Bool(true), Completed: proto.Bool(false), ProgressPercent: proto.Float64(.25), AnomalyKnowledge: proto.Float64(2), KnowledgeCategory: proto.String("Basic")},
	}}
}

// TestPawnAnomalyRow: the row block lifts into typed facts, an absent
// field stays unknown, a failed sub-read is named by an issue, and malformed
// blocks are refused.
func TestPawnAnomalyRow(t *testing.T) {
	row := anomalyPawnFixture()
	if err := validatePawnAnomaly(row.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, known := PawnAnomaly(row.Anomaly).Value()
	if !known {
		t.Fatal("anomaly unknown")
	}
	if entity, ok := a.Entity.Value(); !ok || !entity {
		t.Fatal("entity", entity, ok)
	}
	if min, ok := a.MinContainmentStrength.Value(); !ok || min != 30 {
		t.Fatal("minimum containment strength", min, ok)
	}
	held, ok := a.Held.Value()
	if !ok || held == nil {
		t.Fatal("held", held, ok)
	}
	if mode, _ := held.Mode.Value(); mode != policy.ContainmentStudy {
		t.Fatal("mode", mode)
	}
	if platform, _ := held.Platform.Value(); platform != "Building_HoldingPlatform1" {
		t.Fatal("platform", platform)
	}
	if _, ok := held.ExtractBioferrite.Value(); ok {
		t.Fatal("an unread bioferrite flag must stay unknown")
	}
	if _, ok := held.HarvesterAttached.Value(); ok {
		t.Fatal("an unread harvester fact must stay unknown")
	}
	if _, ok := held.BioferritePerDay.Value(); ok {
		t.Fatal("an unread bioferrite production must stay unknown")
	}
	read := anomalyPawnFixture()
	read.Anomaly.Held.HarvesterAttached, read.Anomaly.Held.BioferritePerDay = proto.Bool(true), proto.Float64(1.5)
	if err := validatePawnAnomaly(read.Anomaly); err != nil {
		t.Fatal(err)
	}
	ra, _ := PawnAnomaly(read.Anomaly).Value()
	rh, _ := ra.Held.Value()
	if harvester, ok := rh.HarvesterAttached.Value(); !ok || !harvester {
		t.Fatal("harvester", harvester, ok)
	}
	if perDay, ok := rh.BioferritePerDay.Value(); !ok || perDay != 1.5 {
		t.Fatal("per day", perDay, ok)
	}
	study, _ := a.Study.Value()
	if p, ok := study.ProgressPercent.Value(); !ok || p != .25 {
		t.Fatal("progress", p, ok)
	}
	if _, ok := study.Completed.Value(); !ok {
		t.Fatal("completed must be known")
	}
	if _, ok := study.CurrentlyStudiable.Value(); ok {
		t.Fatal("an unread studiable flag must stay unknown")
	}
	if _, ok := PawnAnomaly(nil).Value(); ok {
		t.Fatal("a pawn without Anomaly must stay unknown")
	}
	plain := &o.PawnAnomaly{Entity: proto.Bool(false), Mutant: proto.Bool(false), Shambler: proto.Bool(false)}
	if err := validatePawnAnomaly(plain); err != nil {
		t.Fatal(err)
	}
	a, _ = PawnAnomaly(plain).Value()
	if held, ok := a.Held.Value(); !ok || held != nil {
		t.Fatal("a pawn with no holding comp is known not held", held, ok)
	}
	if _, ok := a.MinContainmentStrength.Value(); ok {
		t.Fatal("a non-entity has no containment minimum")
	}
	failed := &o.PawnAnomaly{Entity: proto.Bool(true), Study: row.Anomaly.Study,
		Issues: []*o.ReadIssue{{Field: proto.String("held"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
	if err := validatePawnAnomaly(failed); err != nil {
		t.Fatal(err)
	}
	a, _ = PawnAnomaly(failed).Value()
	if _, ok := a.Held.Value(); ok {
		t.Fatal("a failed held read must stay unknown")
	}
	if _, ok := a.Study.Value(); !ok {
		t.Fatal("a failed held read must not hide study")
	}
	for name, mutate := range map[string]func(*o.PawnAnomaly){
		"nan minimum": func(v *o.PawnAnomaly) { v.MinContainmentStrength = proto.Float64(math.NaN()) },
		"unspecified mode": func(v *o.PawnAnomaly) {
			v.Held.Mode = o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_UNSPECIFIED.Enum()
		},
		"unknown mode":     func(v *o.PawnAnomaly) { v.Held.Mode = o.EntityContainmentModeKind(99).Enum() },
		"nan production":   func(v *o.PawnAnomaly) { v.Held.BioferritePerDay = proto.Float64(math.NaN()) },
		"empty platform":   func(v *o.PawnAnomaly) { v.Held.Platform = &c.Ref{} },
		"progress range":   func(v *o.PawnAnomaly) { v.Study.ProgressPercent = proto.Float64(1.5) },
		"nan knowledge":    func(v *o.PawnAnomaly) { v.Study.AnomalyKnowledge = proto.Float64(math.NaN()) },
		"negative points":  func(v *o.PawnAnomaly) { v.Study.StudyPoints = proto.Float64(-1) },
		"known and issued": func(v *o.PawnAnomaly) { v.Issues = []*o.ReadIssue{{Field: proto.String("entity")}} },
	} {
		r := anomalyPawnFixture()
		mutate(r.Anomaly)
		if validatePawnAnomaly(r.Anomaly) == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

// TestPawnAnomalyThreatFacts: the threat facts lift as read, a failed
// read leaves only its own fact unknown, and a fact both known and issued is
// refused.
func TestPawnAnomalyThreatFacts(t *testing.T) {
	read := &o.PawnAnomaly{Entity: proto.Bool(true), HiddenFromPlayer: proto.Bool(true), PsychicRitualInvoker: proto.Bool(false), MeleeOnly: proto.Bool(true)}
	if err := validatePawnAnomaly(read); err != nil {
		t.Fatal(err)
	}
	a, _ := PawnAnomaly(read).Value()
	for name, want := range map[string]bool{"hidden": true, "invoker": false, "melee": true} {
		got := map[string]domain.Fact[bool]{"hidden": a.HiddenFromPlayer, "invoker": a.PsychicRitualInvoker, "melee": a.MeleeOnly}[name]
		if v, ok := got.Value(); !ok || v != want {
			t.Fatalf("%s = %v, %v; want %v", name, v, ok, want)
		}
	}
	failed := &o.PawnAnomaly{MeleeOnly: proto.Bool(true),
		Issues: []*o.ReadIssue{{Field: proto.String("hidden_from_player"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
	if err := validatePawnAnomaly(failed); err != nil {
		t.Fatal(err)
	}
	a, _ = PawnAnomaly(failed).Value()
	if _, ok := a.HiddenFromPlayer.Value(); ok {
		t.Fatal("a failed hidden read must stay unknown")
	}
	if _, ok := a.PsychicRitualInvoker.Value(); ok {
		t.Fatal("an unread invoker flag must stay unknown")
	}
	if v, ok := a.MeleeOnly.Value(); !ok || !v {
		t.Fatal("a failed hidden read must not hide melee_only")
	}
	both := &o.PawnAnomaly{MeleeOnly: proto.Bool(true), Issues: []*o.ReadIssue{{Field: proto.String("melee_only")}}}
	if validatePawnAnomaly(both) == nil {
		t.Fatal("a fact both known and issued was accepted")
	}
}

func anomalyBuildingFixture() *o.BuildingState {
	return &o.BuildingState{Building: &o.EntityRef{Id: proto.String("Building_HoldingPlatform1")}, Anomaly: &o.AnomalyBuilding{
		Holder: &o.EntityHolderState{ContainmentStrength: proto.Float64(42.5), Available: proto.Bool(false), HeldPawn: &c.Ref{Id: proto.String("Thing_Fingerspike1")}},
	}}
}

// TestBuildingAnomalyDoors: a holder's room doors lift with their
// four Building_Door facts; a failed door read leaves them unknown without
// hiding the holder's strength; a door with no cell is refused.
func TestBuildingAnomalyDoors(t *testing.T) {
	row := anomalyBuildingFixture()
	row.Anomaly.Holder.Doors = []*o.AnomalyDoor{{Cell: &c.Cell{X: proto.Int32(7), Z: proto.Int32(2)}, Open: proto.Bool(true), HoldOpen: proto.Bool(true),
		ContainmentBreached: proto.Bool(true), BlockedOpen: proto.Bool(false)}}
	if err := validateBuildingAnomaly(row.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, _ := BuildingAnomaly(row).Value()
	holder, _ := a.Holder.Value()
	doors, ok := holder.Doors.Value()
	if !ok || len(doors) != 1 || doors[0].Cell != (domain.Cell{X: 7, Z: 2}) {
		t.Fatalf("%+v %v", doors, ok)
	}
	for name, fact := range map[string]domain.Fact[bool]{"open": doors[0].Open, "hold_open": doors[0].HoldOpen, "breached": doors[0].Breached} {
		if v, known := fact.Value(); !known || !v {
			t.Errorf("%s %v %v", name, v, known)
		}
	}
	if v, known := doors[0].BlockedOpen.Value(); !known || v {
		t.Errorf("blocked_open %v %v", v, known)
	}
	if s, ok := holder.ContainmentStrength.Value(); !ok || s != 42.5 {
		t.Fatal("strength", s, ok)
	}
	failed := anomalyBuildingFixture()
	failed.Anomaly.Issues = []*o.ReadIssue{{Field: proto.String("doors"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	if err := validateBuildingAnomaly(failed.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, _ = BuildingAnomaly(failed).Value()
	holder, _ = a.Holder.Value()
	if _, ok := holder.Doors.Value(); ok {
		t.Fatal("a failed door read must stay unknown")
	}
	if s, ok := holder.ContainmentStrength.Value(); !ok || s != 42.5 {
		t.Fatal("a failed door read hid the strength", s, ok)
	}
	row.Anomaly.Holder.Doors = []*o.AnomalyDoor{{}}
	if validateBuildingAnomaly(row.Anomaly) == nil {
		t.Fatal("a door without a cell was accepted")
	}
}

// TestBuildingAnomalyRow: a holding platform's containment strength
// and held pawn lift into typed facts and a block native could not read
// stays unknown.
func TestBuildingAnomalyRow(t *testing.T) {
	row := anomalyBuildingFixture()
	if err := validateBuildingAnomaly(row.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, known := BuildingAnomaly(row).Value()
	if !known {
		t.Fatal("anomaly unknown")
	}
	holder, ok := a.Holder.Value()
	if !ok || holder == nil {
		t.Fatal("holder", holder, ok)
	}
	if s, ok := holder.ContainmentStrength.Value(); !ok || s != 42.5 {
		t.Fatal("containment strength", s, ok)
	}
	if holder.HeldPawn != "Thing_Fingerspike1" {
		t.Fatal("held pawn", holder.HeldPawn)
	}
	if study, ok := a.Study.Value(); !ok || study != nil {
		t.Fatal("a platform is known not studiable", study, ok)
	}
	if _, ok := BuildingAnomaly(&o.BuildingState{}).Value(); ok {
		t.Fatal("a row without the block must stay unknown")
	}
	failed := &o.BuildingState{Anomaly: &o.AnomalyBuilding{Issues: []*o.ReadIssue{{Field: proto.String("holder"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}}
	if err := validateBuildingAnomaly(failed.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, _ = BuildingAnomaly(failed).Value()
	if _, ok := a.Holder.Value(); ok {
		t.Fatal("a failed holder read must stay unknown")
	}
	for name, mutate := range map[string]func(*o.AnomalyBuilding){
		"nan strength":      func(v *o.AnomalyBuilding) { v.Holder.ContainmentStrength = proto.Float64(math.NaN()) },
		"negative strength": func(v *o.AnomalyBuilding) { v.Holder.ContainmentStrength = proto.Float64(-1) },
		"empty pawn":        func(v *o.AnomalyBuilding) { v.Holder.HeldPawn = &c.Ref{} },
		"known and issued":  func(v *o.AnomalyBuilding) { v.Issues = []*o.ReadIssue{{Field: proto.String("holder")}} },
	} {
		r := anomalyBuildingFixture()
		mutate(r.Anomaly)
		if validateBuildingAnomaly(r.Anomaly) == nil {
			t.Errorf("%s accepted", name)
		}
	}
	if checkBuildingListRow(row) != nil {
		t.Fatal("list row refused")
	}
}
