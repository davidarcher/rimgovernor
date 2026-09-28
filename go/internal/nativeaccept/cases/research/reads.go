// The research/reads case proves typed research-project reads, with a
// private fingerprint fixture that proves
// the typed read never mutates saved research state.
package research

import (
	"context"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:  "research/reads",
		Scope: "Private read-only research fingerprint fixture; typed read invariance before separate native getter audit. No selection, save or pawn work.",
		Start: cases.LabStart(),
		// A read-only fingerprint; the wild map is unobserved (#333).
		QuietWorld: true,
		Quiet:      na.QuietRequired,
		Budget:     5 * time.Minute,
		Run:        run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, names := s.Harness(), s.Names()
	if !na.Contains(names, "rimgovernor/observations_read_research") {
		return fmt.Errorf("missing rimgovernor/observations_read_research in discovery")
	}
	hasFingerprint := na.Contains(names, "test/research_observation_fingerprint")
	if !hasFingerprint {
		return fmt.Errorf("missing test/research_observation_fingerprint fixture in discovery")
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, before, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(before["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	beforeContext, _ := before["context"].(map[string]any)
	identity, _ := beforeContext["identity"].(map[string]any)

	fingerprintBefore, err := h.Call(ctx, "fingerprint-before", "test/research_observation_fingerprint", nil)
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(fingerprintBefore["success"]); !success {
		return fmt.Errorf("research fingerprint fixture refused")
	}

	scope := map[string]any{"scope": map[string]any{"expectedIdentity": identity}}
	full := na.Merge(scope, map[string]any{"includeLocked": true, "includeFinished": true, "includeCapability": true})
	fullReply, err := h.Wire(ctx, "full", "observations_read_research", full)
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(fullReply, "observed")
	if err != nil {
		return err
	}
	if _, present := observed["snapshot"]; !present {
		return fmt.Errorf("expected a populated research snapshot on the full read")
	}
	if err := na.RequireSnapshot(observed["snapshot"]); err != nil {
		return fmt.Errorf("research read missing a populated CAS snapshot: %w", err)
	}
	projects := na.AsSlice(observed["projects"])
	if len(projects) <= 1 {
		return fmt.Errorf("expected more than 1 research project")
	}
	repeatReply, err := h.Wire(ctx, "repeat", "observations_read_research", full)
	if err != nil {
		return err
	}
	_, repeatObserved, err := na.Outcome(repeatReply, "observed")
	if err != nil {
		return err
	}
	if !na.DeepEqual(repeatObserved, observed) {
		return fmt.Errorf("repeating the full read returned a different snapshot")
	}

	defaultsReply, err := h.Wire(ctx, "defaults", "observations_read_research", scope)
	if err != nil {
		return err
	}
	_, defaults, err := na.Outcome(defaultsReply, "observed")
	if err != nil {
		return err
	}
	if len(na.AsSlice(defaults["benches"])) != 0 || len(na.AsSlice(defaults["researchers"])) != 0 {
		return fmt.Errorf("default (unrequested) benches/researchers were unexpectedly populated")
	}

	firstProject, _ := na.AsMap(projects[0])
	firstProjectDef, _ := na.AsMap(firstProject["project"])
	needle := na.AsString(firstProjectDef["defName"])
	filteredReply, err := h.Wire(ctx, "filtered", "observations_read_research", na.Merge(full, map[string]any{"nameContains": needle}))
	if err != nil {
		return err
	}
	_, filtered, err := na.Outcome(filteredReply, "observed")
	if err != nil {
		return err
	}
	if len(na.AsSlice(filtered["projects"])) == 0 {
		return fmt.Errorf("filtered read for %q returned no projects", needle)
	}

	// Bounded search for a project row with a populated unlocks collection;
	// unlocks are only returned per-row on request.
	var unlockRows []any
	candidates := projects
	for index, raw := range candidates {
		if index >= 16 {
			break
		}
		row, _ := na.AsMap(raw)
		finished, _ := na.AsBool(row["finished"])
		if finished {
			continue
		}
		rowDef, _ := na.AsMap(row["project"])
		reply, err := h.Wire(ctx, fmt.Sprintf("unlocks-%d", index), "observations_read_research",
			na.Merge(full, map[string]any{"nameContains": na.AsString(rowDef["defName"]), "includeUnlocks": true}))
		if err != nil {
			return err
		}
		if reason, ok := na.UnavailableReason(reply); ok {
			if reason != "UNAVAILABLE_REASON_LIMIT_EXCEEDED" {
				return fmt.Errorf("unexpected unavailable reason for unlock probe: %s", reason)
			}
			continue
		}
		_, rows, err := na.Outcome(reply, "observed")
		if err != nil {
			return err
		}
		candidateRows := na.AsSlice(rows["projects"])
		hasUnlocks := false
		for _, r := range candidateRows {
			rm, _ := na.AsMap(r)
			if len(na.AsSlice(rm["unlocks"])) > 0 {
				hasUnlocks = true
			}
		}
		unlockRows = candidateRows
		if hasUnlocks {
			break
		}
	}
	hasPopulatedUnlocks := false
	for _, raw := range unlockRows {
		row, _ := na.AsMap(raw)
		if len(na.AsSlice(row["unlocks"])) > 0 {
			hasPopulatedUnlocks = true
		}
	}
	if !hasPopulatedUnlocks {
		return fmt.Errorf("no populated bounded unlock collection was verified")
	}

	fingerprintAfter, err := h.Call(ctx, "fingerprint-after", "test/research_observation_fingerprint", nil)
	if err != nil {
		return err
	}
	fpBefore, err := fingerprintState(fingerprintBefore)
	if err != nil {
		return fmt.Errorf("fingerprint-before: %w", err)
	}
	fpAfter, err := fingerprintState(fingerprintAfter)
	if err != nil {
		return fmt.Errorf("fingerprint-after: %w", err)
	}
	if !na.DeepEqual(fpBefore, fpAfter) {
		return fmt.Errorf("typed research read mutated saved research state")
	}
	report["saved_state_unchanged"] = true

	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, after, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if pausedAfter, _ := na.AsBool(after["paused"]); !pausedAfter {
		return fmt.Errorf("game unexpectedly resumed during the read pass")
	}
	if afterContext, _ := after["context"].(map[string]any); !na.DeepEqual(afterContext, beforeContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), s.Config().Headless); err != nil {
		return err
	}
	report["projects"] = len(projects)
	report["researchers"] = len(na.AsSlice(observed["researchers"]))
	report["benches"] = len(na.AsSlice(observed["benches"]))
	return nil
}

func fingerprintState(v map[string]any) (map[string]any, error) {
	if success, ok := na.AsBool(v["success"]); !ok || !success {
		return nil, fmt.Errorf("fixture did not report success=true: %#v", v["success"])
	}
	out := map[string]any{}
	for _, field := range []string{"current", "progress", "knowledge", "slots", "techprints", "tick", "paused"} {
		val, present := v[field]
		if !present {
			return nil, fmt.Errorf("fixture missing declared field %q", field)
		}
		out[field] = val
	}
	return out, nil
}
