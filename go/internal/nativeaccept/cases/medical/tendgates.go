package medical

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "medical/tend-gates",
		Scope: "list_pawns' tend detail carries the doctor gates NativeTendOperations.Prepare enforces " +
			"(pawn-control eligibility, WorkGiver_Tend capacities, pairwise reachability), so SelectTend stops " +
			"proposing doctors the native tend gate refuses (#657).",
		Start:  cases.Fixture{Op: "test/medical_plague_prepare"},
		Budget: time.Minute,
		Run:    tendGates,
	})
}

func tendGates(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	identity, err := na.ReadIdentity(ctx, h, "tend-gates-identity")
	if err != nil {
		return err
	}
	reply, err := h.Wire(ctx, "tend-gates-readback", "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"colonist": true, "humanlike": true, "animal": false},
		"details": map[string]any{"health": true, "needs": false, "equipment": false, "biography": false, "settings": false, "social": false, "animals": false, "tend": true},
		"page":    map[string]any{"limit": 256},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	if err = tendGatesObserve(observed); err != nil {
		return err
	}
	s.Report()["tend_gates"] = observed
	return nil
}

// tendGatesObserve checks the tend detail against the rest of each row: every
// colonist carries the gates, a spawned live undowned colonist is control
// eligible, and reachability names other returned rows only -- never the row
// itself, and symmetrically, since CanReach over a connected colony map is not
// directional.
func tendGatesObserve(observed map[string]any) error {
	reach := map[string]map[string]bool{}
	ids := []string{}
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["pawn"])
		id := na.AsString(ref["id"])
		if id == "" {
			return fmt.Errorf("pawn row without an identity: %#v", row)
		}
		doctor, ok := na.AsMap(row["tendDoctor"])
		if !ok {
			return fmt.Errorf("%s carries no tendDoctor detail; the tend gates are unobserved", id)
		}
		eligible, known := na.AsBool(doctor["controlEligible"])
		if !known {
			return fmt.Errorf("%s controlEligible unknown: %#v", id, doctor)
		}
		spawned, _ := na.AsBool(doctor["spawned"])
		drafter, _ := na.AsBool(doctor["hasDrafter"])
		dead, _ := na.AsBool(row["dead"])
		downed, _ := na.AsBool(row["downed"])
		if want := spawned && drafter && !dead && !downed && row["mentalState"] == nil; eligible != want && want {
			return fmt.Errorf("%s controlEligible = %v for a spawned live undowned colonist", id, eligible)
		}
		if _, known := na.AsBool(doctor["capacitiesOk"]); !known {
			return fmt.Errorf("%s capacitiesOk unknown: %#v", id, doctor)
		}
		if _, known := na.AsBool(doctor["workTypeDisabled"]); !known {
			return fmt.Errorf("%s workTypeDisabled unknown: %#v", id, doctor)
		}
		// A row whose reachability could not be evaluated (unspawned, or a
		// page past the pairwise bound) carries an issue instead of a list and
		// takes no part in the symmetry check.
		unevaluated := false
		for _, rawIssue := range na.AsSlice(doctor["issues"]) {
			issue, _ := na.AsMap(rawIssue)
			if na.AsString(issue["field"]) == "reachable_pawn_ids" {
				unevaluated = true
			}
		}
		if unevaluated {
			continue
		}
		ids = append(ids, id)
		reach[id] = map[string]bool{}
		for _, target := range na.AsSlice(doctor["reachablePawnIds"]) {
			other := na.AsString(target)
			if other == id {
				return fmt.Errorf("%s is listed as reaching itself", id)
			}
			reach[id][other] = true
		}
	}
	if len(ids) < 2 {
		return fmt.Errorf("expected at least two colonist rows, read %d", len(ids))
	}
	for _, a := range ids {
		for _, b := range ids {
			if a == b {
				continue
			}
			if reach[a][b] != reach[b][a] {
				return fmt.Errorf("asymmetric reachability between %s and %s", a, b)
			}
		}
	}
	return nil
}
