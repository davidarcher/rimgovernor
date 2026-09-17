package observation

import (
	"context"
	"encoding/json"
	"math"
	"os"
	"path/filepath"
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

func TestNativeRoutineMoodReplay(t *testing.T) {
	directory := os.Getenv("RIMGOVERNOR_NATIVE_MOOD_CAPTURE")
	if directory == "" {
		t.Skip("requires native mood-review acceptance capture")
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
	colony, pawns, status := &o.ColonyFactsReply{}, &o.ListPawnsReply{}, &o.StatusReply{}
	read("work-colony", colony)
	read("work-pawns", pawns)
	read("work-status", status)
	v, p, s := colony.GetObserved(), pawns.GetObserved(), status.GetObserved()
	if v == nil || p == nil || s == nil || s.Colonists == nil || !proto.Equal(v.Context, p.Context) || !proto.Equal(v.Context, s.Context) {
		t.Fatal("native mood escaped paused bracket")
	}
	identity, err := contextIdentity(v.Context)
	if err != nil {
		t.Fatal(err)
	}
	projection, err := DecodeColony(colony, identity)
	if err != nil {
		t.Fatal(err)
	}
	counts := s.Colonists.Completeness
	if counts == nil || !counts.Page.GetComplete() || counts.GetMatched() != uint64(len(s.Colonists.Pawns)) || counts.GetReturned() != counts.GetMatched() || counts.GetFiltered() != 0 || counts.GetUnreadable() != 0 {
		t.Fatal("incomplete native mood census")
	}
	e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}
	ids := []string{}
	for _, row := range s.Colonists.Pawns {
		ids = append(ids, row.Pawn.GetId())
		e.Colonists = append(e.Colonists, policy.EmergencyPawn{ID: policy.PawnID(row.Pawn.GetId()), Dead: optional(row.Dead), Downed: optional(row.Downed)})
	}
	if err = bridge.ValidateRoutinePawnSnapshot(p, v.Context.Identity, ids); err != nil {
		t.Fatal(err)
	}
	observed := routineMood(v, e, p)
	h, err := policy.ReviewMood(observed, policy.MoodHistory{})
	if err != nil {
		t.Fatal(err)
	}
	var expected struct {
		Setup     struct{ Pawn, Scenario string }
		Reference map[string]struct {
			Active          bool
			Mood, Threshold float64
			Priority        int
			Causes          []struct {
				Need  policy.MoodNeed
				Level *float64
			}
		}
	}
	data, err := os.ReadFile(filepath.Join(directory, "mood-reference.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = json.Unmarshal(data, &expected); err != nil {
		t.Fatal(err)
	}
	if len(h.States) == 0 || len(h.States) != len(expected.Reference) {
		t.Fatal("native mood target mismatch", h)
	}
	for _, state := range h.States {
		want, ok := expected.Reference[string(state.Pawn.ID)]
		m, mk := state.Pawn.Mood.Value()
		threshold, tk := state.Pawn.Threshold.Value()
		if !ok || !want.Active || !state.Active || !mk || !tk || math.Abs(m-want.Mood) > 1e-6 || math.Abs(threshold-want.Threshold) > 1e-6 || state.Priority() != want.Priority || len(state.Causes) != len(want.Causes) {
			t.Fatal(state, want)
		}
		for i, cause := range state.Causes {
			level, k := cause.Level.Value()
			w := want.Causes[i]
			if cause.Need != w.Need || k != (w.Level != nil) || k && math.Abs(level-*w.Level) > 1e-6 {
				t.Fatal(cause, w)
			}
		}
		proposal, err := policy.SelectMoodMethod(state, nil)
		if err != nil {
			t.Fatal(err)
		}
		if string(state.Pawn.ID) == expected.Setup.Pawn {
			reason := map[string]policy.MoodMethodReason{"food": policy.MoodRelief, "mental": policy.MoodMentalBreak, "forced": policy.MoodPlayerWork}[expected.Setup.Scenario]
			if proposal.Reason != reason {
				t.Fatal(proposal, reason)
			}
		}
	}
	ctx := context.Background()
	path := storetest.Path(t)
	db, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	native, known := identity.NativeGeneration.Value()
	if !known {
		t.Fatal("unknown native generation")
	}
	scope := domain.GenerationSnapshot{Colony: identity.Colony, Load: identity.Load, Map: identity.Map, Native: native, Plan: "mood-native-replay", Revision: 1}
	projection.Facts.MoodPawns = observed
	r := store.RoutineReviewRequest{Current: scope, Tick: identity.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: projection.Facts}
	out, err := db.ReviewRoutine(ctx, r)
	if err != nil {
		t.Fatal(err)
	}
	for _, state := range h.States {
		found := false
		for i, binding := range out.Review.Goals {
			if binding.Need == policy.MoodGoal(state.Pawn.ID) {
				found = true
				if out.Goals[i].Goal.Need != domain.NeedDeficit || out.Goals[i].Goal.Priority != state.Priority() {
					t.Fatal(out.Goals[i])
				}
			}
		}
		if !found {
			t.Fatal("missing durable mood goal")
		}
	}
	r.Revision = out.Review.Revision
	r.Enabled = false
	if _, err = db.ReviewRoutine(ctx, r); err != nil {
		t.Fatal(err)
	}
	db.Close()
	db, err = store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	loaded, err := db.LoadRoutineReview(ctx)
	if err != nil || loaded.Enabled || loaded.Mood == nil || len(loaded.Mood.States) != len(h.States) || len(loaded.MoodMethods) != 0 {
		t.Fatal(loaded, err)
	}
}
