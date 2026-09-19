package observation

import (
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestNativeRoutineWorkParity(t *testing.T) {
	directory := os.Getenv("RIMGOVERNOR_NATIVE_WORK_CAPTURE")
	if directory == "" {
		t.Skip("requires native work capture")
	}
	read := func(name string, message proto.Message) {
		t.Helper()
		data, err := os.ReadFile(filepath.Join(directory, name+".json"))
		if err != nil {
			t.Fatal(err)
		}
		if err = protojson.Unmarshal(data, message); err != nil {
			t.Fatal(err)
		}
	}
	colony := &o.ColonyFactsReply{}
	pawns := &o.ListPawnsReply{}
	status := &o.StatusReply{}
	read("work-colony", colony)
	read("work-pawns", pawns)
	read("work-status", status)
	v := colony.GetObserved()
	s := status.GetObserved()
	p := pawns.GetObserved()
	if v == nil || s == nil || p == nil || s.Colonists == nil || !proto.Equal(v.Context, s.Context) || !proto.Equal(v.Context, p.Context) {
		t.Fatal("inconsistent native captures")
	}
	if err := bridge.ValidateColonyFacts(v, v.Context.Identity); err != nil {
		t.Fatal(err)
	}
	census := s.Colonists.Completeness
	if census == nil || !census.Page.GetComplete() || census.GetReturned() != uint64(len(s.Colonists.Pawns)) || census.GetMatched() != census.GetReturned() || census.GetFiltered() != 0 || census.GetUnreadable() != 0 {
		t.Fatal("incomplete native status")
	}
	e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}
	ids := []string{}
	for _, row := range s.Colonists.Pawns {
		ids = append(ids, row.Pawn.GetId())
		e.Colonists = append(e.Colonists, policy.EmergencyPawn{ID: policy.PawnID(row.Pawn.GetId()), Dead: optional(row.Dead), Downed: optional(row.Downed)})
	}
	if err := bridge.ValidateRoutinePawnSnapshot(p, v.Context.Identity, ids); err != nil {
		t.Fatal(err)
	}
	workers, known := routineWork(v, e, p).Value()
	if !known {
		t.Fatal("native workers unknown")
	}
	var expected struct {
		Assignments         map[string]map[string]int
		Capacity, Matches   bool
		MinimumConstruction int                   `json:"minimum_construction"`
		Overrides           []policy.WorkOverride `json:"overrides"`
	}
	data, err := os.ReadFile(filepath.Join(directory, "work-reference.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = json.Unmarshal(data, &expected); err != nil {
		t.Fatal(err)
	}
	d, err := policy.AssignWork(workers, []policy.WorkRequirement{{Work: "Construction", Skill: "Construction", Minimum: expected.MinimumConstruction}}, expected.Overrides)
	if err != nil {
		t.Fatal(err)
	}
	actual := map[string]map[string]int{}
	for _, pawn := range d.Assignments {
		values := map[string]int{}
		for _, v := range pawn.Priorities {
			values[string(v.Work)] = v.Priority
		}
		actual[string(pawn.Pawn)] = values
	}
	if !reflect.DeepEqual(actual, expected.Assignments) {
		t.Fatalf("work mismatch: got %#v want %#v", actual, expected.Assignments)
	}
	capacity, ck := d.Capacity.Value()
	matches, mk := d.Matches.Value()
	if !ck || !mk || capacity != expected.Capacity || matches != expected.Matches {
		t.Fatal(d, expected)
	}
	t.Logf("Native work parity: %d assignments, capacity=%v matches=%v", len(actual), capacity, matches)
}

// WorkPawnRow lifts JobRow's evidence: no job (the current_job issue) is a
// known empty job, a job carries its def and its work giver's type when a
// giver issued it, and a missing or issued job block stays unknown.
func TestWorkPawnRowJob(t *testing.T) {
	row := func(job *o.JobEvidence, issues ...*o.ReadIssue) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("p")}, Job: job, Issues: issues}
	}
	cases := []struct {
		row  *o.PawnState
		want domain.Fact[policy.PawnJob]
	}{
		{row(&o.JobEvidence{Issues: []*o.ReadIssue{{Field: proto.String("current_job")}}}), domain.Known(policy.PawnJob{})},
		{row(&o.JobEvidence{DefName: proto.String("CutPlant"), WorkTypeDefName: proto.String("PlantCutting")}), domain.Known(policy.PawnJob{Def: "CutPlant", Work: policy.WorkPlantCutting})},
		{row(&o.JobEvidence{DefName: proto.String("LayDown")}), domain.Known(policy.PawnJob{Def: "LayDown"})},
		{row(nil), domain.Unknown[policy.PawnJob]()},
		{row(&o.JobEvidence{DefName: proto.String("CutPlant")}, &o.ReadIssue{Field: proto.String("job")}), domain.Unknown[policy.PawnJob]()},
		{row(&o.JobEvidence{}), domain.Unknown[policy.PawnJob]()},
	}
	for i, c := range cases {
		if got := WorkPawnRow(c.row).Job; !reflect.DeepEqual(got, c.want) {
			t.Fatal(i, got, c.want)
		}
	}
}
