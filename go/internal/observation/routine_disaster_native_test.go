package observation

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestNativeRoutineDisasterReplay(t *testing.T) {
	directory := os.Getenv("RIMGOVERNOR_NATIVE_DISASTER_CAPTURE")
	if directory == "" {
		t.Skip("requires native compound disaster planning capture")
	}
	data, err := os.ReadFile(filepath.Join(directory, "work-colony.json"))
	if err != nil {
		t.Fatal(err)
	}
	colony := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, colony); err != nil {
		t.Fatal(err)
	}
	id, err := contextIdentity(colony.GetObserved().GetContext())
	if err != nil {
		t.Fatal(err)
	}
	projection, err := DecodeColony(colony, id)
	if err != nil {
		t.Fatal(err)
	}
	pawns, status := &o.ListPawnsReply{}, &o.StatusReply{}
	for name, message := range map[string]proto.Message{"work-pawns": pawns, "work-status": status} {
		data, err := os.ReadFile(filepath.Join(directory, name+".json"))
		if err != nil {
			t.Fatal(err)
		}
		if err = protojson.Unmarshal(data, message); err != nil {
			t.Fatal(err)
		}
	}
	v, p, s := colony.GetObserved(), pawns.GetObserved(), status.GetObserved()
	if p == nil || s == nil || s.Colonists == nil || !proto.Equal(v.Context, p.Context) || !proto.Equal(v.Context, s.Context) {
		t.Fatal("recovery workers escaped paused bracket")
	}
	counts := s.Colonists.Completeness
	if counts == nil || !counts.Page.GetComplete() || counts.GetMatched() != uint64(len(s.Colonists.Pawns)) || counts.GetReturned() != counts.GetMatched() || counts.GetFiltered() != 0 || counts.GetUnreadable() != 0 {
		t.Fatal("incomplete recovery worker census")
	}
	emergency := policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}
	ids := []string{}
	for _, row := range s.Colonists.Pawns {
		ids = append(ids, row.Pawn.GetId())
		emergency.Colonists = append(emergency.Colonists, policy.EmergencyPawn{ID: policy.PawnID(row.Pawn.GetId()), Dead: optional(row.Dead), Downed: optional(row.Downed)})
	}
	if err = bridge.ValidateRoutinePawnSnapshot(p, v.Context.Identity, ids); err != nil {
		t.Fatal(err)
	}
	projection.Facts.RecoveryWorkers = recoveryWorkers(routineMood(v, emergency, p))
	var reference struct {
		Reference struct {
			Work    []policy.RecoveryWork
			Damaged []string
		}
	}
	data, err = os.ReadFile(filepath.Join(directory, "disaster-reference.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = json.Unmarshal(data, &reference); err != nil {
		t.Fatal(err)
	}
	pending, err := policy.RecoveryPending(projection.Facts.RecoveryBuildings)
	rows, known := pending.Value()
	if err != nil || !known || len(rows) == 0 || !reflect.DeepEqual(rows, reference.Reference.Work) {
		t.Fatal(rows, known, err)
	}
	path := storetest.Path(t)
	journal, err := store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer func() {
		if journal != nil {
			journal.Close()
		}
	}()
	native, known := id.NativeGeneration.Value()
	if !known {
		t.Fatal("missing native generation")
	}
	request := store.RoutineReviewRequest{Current: domain.GenerationSnapshot{Colony: id.Colony, Load: id.Load, Map: id.Map, Native: native, Plan: "disaster-native-replay", Revision: 1}, Tick: id.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: projection.Facts}
	emergencyObservation, err := bridge.DecodeEmergencyStatus(s, v.Context.Identity)
	if err != nil {
		t.Fatal(err)
	}
	emergencySnapshot, err := policy.NewEmergencySnapshot(request.Current, id.Tick, emergencyObservation.Facts)
	if err != nil {
		t.Fatal(err)
	}
	request.Facts.Hostiles, request.Facts.CriticalPatients = policy.EmergencyNeeds(emergencySnapshot, request.Current, id.Tick)
	request.Facts.CleanupPawns = domain.Known(false) // Fresh journal has no owned drafts.
	result, err := journal.ReviewRoutine(context.Background(), request)
	if err != nil {
		t.Fatal(err)
	}
	h := result.Review.Disaster
	if h == nil || h.Phase != policy.DisasterDisrupted || !reflect.DeepEqual(h.Work, rows) || !reflect.DeepEqual(h.Damaged, reference.Reference.Damaged) {
		t.Fatal(h)
	}
	selection := result.Review.Recovery
	if selection == nil || selection.Selection.Reason != policy.RecoveryAdmissionRequired || len(selection.Selection.Candidates) == 0 {
		t.Fatal("native refuge proposals missing", selection)
	}
	safety, _ := projection.Facts.RecoverySafety.Value()
	for _, candidate := range selection.Selection.Candidates {
		if candidate.Kind != policy.RecoveryAreaProposal {
			t.Fatal("exposure bypassed refuge", candidate)
		}
		found := false
		for _, area := range safety.SafeAreas {
			found = found || area == candidate.Area
		}
		if !found || candidate.PriorArea == nil {
			t.Fatal("unobserved refuge or restriction", candidate)
		}
	}
	journal.Close()
	journal, err = store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	active, err := journal.LoadRoutineReview(context.Background())
	if err != nil || !reflect.DeepEqual(active.Recovery, selection) {
		t.Fatal("restart changed recovery proposals", active.Recovery, err)
	}
	request.Revision = result.Review.Revision
	request.Enabled = false
	request.Tick++
	manual, err := journal.ReviewRoutine(context.Background(), request)
	if err != nil || !reflect.DeepEqual(manual.Review.Disaster, h) || manual.Review.Recovery != nil {
		t.Fatal(manual, err)
	}
	journal.Close()
	journal, err = store.Open(context.Background(), path)
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	loaded, err := journal.LoadRoutineReview(context.Background())
	if err != nil || loaded.Enabled || !reflect.DeepEqual(loaded.Disaster, h) {
		t.Fatal(loaded, err)
	}
}
