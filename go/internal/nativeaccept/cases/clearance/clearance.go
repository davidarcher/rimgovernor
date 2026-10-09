// Package clearance verifies routine clearance through the service and native pawn work.
package clearance

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const fixtureKey = "clearance_fixture"

func init() {
	// ancient_wall, roof_support_refused and standing_designation replay as
	// colony snapshots instead (internal/snapshot, #746).
	for _, scenario := range []string{"chunk_dump"} {
		cases.Register(cases.Case{
			Name:        "clearance/" + strings.ReplaceAll(scenario, "_", "-"),
			Scope:       "Routine Home clearance: " + scenario + "; exact native targets, journal holds and observed completion.",
			Start:       cases.Save{Name: "RimGovernor-tribal8-baseline"},
			RequiredOps: []string{"test/clearance_prepare", "test/clearance_support", "test/clearance_audit"},
			Serve:       &cases.ServeSpec{Families: []routinefamily.Family{routinefamily.Clearance, routinefamily.Tend, routinefamily.Rescue}, Prefix: "clearance"},
			Stages:      []string{"clearance-ready"}, Budget: 4 * time.Minute, Crew: cases.Crew{Size: 3}, Stall: 60 * time.Second,
			Run: func(ctx context.Context, s cases.Session) error { return run(ctx, s, scenario) },
		})
	}
}

func run(ctx context.Context, s cases.Session, scenario string) error {
	var fixture map[string]any
	if err := s.Stage(ctx, "clearance-ready", func(ctx context.Context) error {
		var err error
		fixture, err = s.Harness().Call(ctx, "prepare-clearance", "test/clearance_prepare", map[string]any{"scenario": scenario})
		if err == nil {
			na.SetCheckpointState(fixtureKey, fixture)
		}
		return err
	}); err != nil {
		return err
	}
	if restored := cases.RestoredState(s, fixtureKey); restored != nil {
		fixture, _ = na.AsMap(restored)
	}
	if fixture == nil {
		return fmt.Errorf("missing staged clearance identities")
	}
	s.Report()["fixture"] = fixture
	before, err := census(ctx, s, "before")
	if err != nil {
		return err
	}
	s.Report()["census_before"] = before
	return runChunks(ctx, s, fixture, before)
}

func wait(service *na.ServiceProcess) na.Wait {
	return na.Wait{Ceiling: 2 * time.Minute, Stall: 60 * time.Second, Interval: time.Second, Terminal: service.Exited}
}

func start(ctx context.Context, s cases.Session) (*na.ServiceProcess, error) {
	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return nil, err
	}
	if _, err = service.Acquire(); err != nil {
		service.Stop()
		return nil, err
	}
	service.KeepAuthority(ctx)
	return service, nil
}

func reattach(ctx context.Context, s cases.Session) error {
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	_, err = h.Call(ctx, "pause-audit", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false})
	return err
}

func census(ctx context.Context, s cases.Session, label string) (map[string]any, error) {
	reply, err := s.Harness().Wire(ctx, label, "observations_get_clearance_targets", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	return observed, nil
}

func audit(ctx context.Context, s cases.Session, fixture map[string]any, label string) (map[string]any, error) {
	var ids []string
	for _, raw := range na.AsSlice(fixture["chunks"]) {
		ids = append(ids, na.AsString(raw))
	}
	result, err := s.Harness().Call(ctx, label, "test/clearance_audit", map[string]any{"target": na.AsString(fixture["target"]), "chunks": strings.Join(ids, ";")})
	s.Report()[label] = result
	return result, err
}

func boolean(raw any) bool { value, _ := na.AsBool(raw); return value }

func runChunks(ctx context.Context, s cases.Session, fixture, before map[string]any) error {
	ids := na.AsSlice(fixture["chunks"])
	if len(ids) != 3 {
		return fmt.Errorf("expected three fixture chunks")
	}
	for _, id := range ids {
		found := false
		for _, raw := range na.AsSlice(before["chunks"]) {
			row, _ := na.AsMap(raw)
			if row["entityId"] == id {
				found = true
				if boolean(row["stored"]) || boolean(row["forbidden"]) || boolean(row["destination"]) {
					return fmt.Errorf("chunk is already stored or forbidden: %v", row)
				}
			}
		}
		if !found {
			return fmt.Errorf("chunk %v absent from clearance census", id)
		}
	}
	service, err := start(ctx, s)
	if err != nil {
		return err
	}
	defer service.Stop()
	journal, err := service.Store(ctx)
	if err != nil {
		return err
	}
	err = na.WaitProgress(ctx, wait(service), func(ctx context.Context) (string, bool, error) {
		review, err := journal.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Standards {
			if binding.Concern != policy.ClearHomeObstructions {
				continue
			}
			goal, err := journal.LoadStandard(ctx, binding.Standard)
			if err != nil {
				return "", false, err
			}
			s.Report()["chunk_standard"] = goal.Standard
			// No Haul designation is ever ordered (#2513): the native mod marks chunks always haulable.
			return na.Signature(goal.Standard.Finding, len(goal.Methods)), goal.Standard.Finding == domain.FindingMet, nil
		}
		return "waiting for chunk standard", false, nil
	})
	if err != nil {
		return err
	}
	service.Stop()
	if err = reattach(ctx, s); err != nil {
		return err
	}
	// Goal recovery means a destination exists (the materials yard takes
	// chunks and slag; no dump zone is created). Ordinary native hauling must
	// still move all three items; never equate a store with storage.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: time.Minute, Stall: 30 * time.Second}, func(ctx context.Context) (string, bool, error) {
		live, err := audit(ctx, s, fixture, "chunks_after")
		if err != nil {
			return "", false, err
		}
		if err = checkDump(live, fixture); err == nil {
			return "stored", true, nil
		}
		if _, advanceErr := s.Advance(ctx, 250); advanceErr != nil {
			return "", false, advanceErr
		}
		return na.Signature(live), false, nil
	})
	if err != nil {
		return err
	}
	after, err := census(ctx, s, "chunks-stored")
	if err != nil {
		return err
	}
	for _, id := range ids {
		stored := false
		for _, raw := range na.AsSlice(after["chunks"]) {
			row, _ := na.AsMap(raw)
			stored = stored || row["entityId"] == id && boolean(row["stored"])
		}
		if !stored {
			return fmt.Errorf("chunk %v is not stored in native clearance census", id)
		}
	}
	return nil
}

// checkDump asserts the planner made no dumping stockpile and that all three
// fixture chunks sit in a store whose allow list takes every fixture kind and
// steel slag: the materials yard.
func checkDump(live, fixture map[string]any) error {
	want := []string{"ChunkSlagSteel"}
	for _, raw := range na.AsSlice(fixture["defs"]) {
		want = append(want, na.AsString(raw))
	}
	takes := map[string]bool{}
	for _, raw := range na.AsSlice(live["zones"]) {
		z, _ := na.AsMap(raw)
		if z["label"] == "Dumping" {
			return fmt.Errorf("a dumping stockpile was created: %v", z)
		}
		var allow []string
		for _, def := range na.AsSlice(z["allow"]) {
			allow = append(allow, na.AsString(def))
		}
		if label := na.AsString(z["label"]); label != "" && slices.ContainsFunc(want, func(d string) bool { return !slices.Contains(allow, d) }) {
			continue
		} else if label != "" {
			takes[label] = true
		}
	}
	chunks := na.AsSlice(live["chunks"])
	if len(chunks) != 3 {
		return fmt.Errorf("native audit lost chunks: %v", chunks)
	}
	for _, raw := range chunks {
		c, _ := na.AsMap(raw)
		zone := na.AsString(c["zone"])
		if !boolean(c["stored"]) || !takes[zone] {
			return fmt.Errorf("chunk not hauled to the yard: %v (stores taking %v: %v)", c, want, takes)
		}
	}
	return nil
}
