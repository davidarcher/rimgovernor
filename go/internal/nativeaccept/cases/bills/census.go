// The bills/census case proves the typed bench census behind
// rimgovernor/observations_read_bills and observations_read_recipes (#77)
// on a loaded save: every player bench appears once with a CAS token, each
// bill names its recipe, the census token
// agrees with the colony-facts production token for the same bench, and each
// bench's recipe list carries the availability of every recipe (what a recipe
// is, costs and needs is read from the definition catalog, #1721).
// A fixture build's test/routine_production_prepare seeds a fueled campfire
// with a food bill so a bench-less save still exercises the census.
package bills

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
		Name:   "bills/census",
		Scope:  "Typed bench/bill census and per-bench recipe catalog; read-only, no bill changes or gameplay orders.",
		Start:  cases.Fixture{Op: "test/routine_production_prepare", On: cases.LabStart()},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	for _, required := range []string{"rimgovernor/observations_read_bills", "rimgovernor/observations_read_recipes"} {
		if !na.Contains(s.Names(), required) {
			return fmt.Errorf("missing %s in discovery", required)
		}
	}
	identityReply, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, before, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	beforeContext, _ := before["context"].(map[string]any)
	identity, _ := beforeContext["identity"].(map[string]any)
	scope := map[string]any{"expectedIdentity": identity}

	billsReply, err := h.Wire(ctx, "typed-bills", "observations_read_bills", map[string]any{"scope": scope})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(billsReply, "observed")
	if err != nil {
		return err
	}
	if observedContext, _ := observed["context"].(map[string]any); !na.DeepEqual(observedContext, beforeContext) {
		return fmt.Errorf("bills context drifted mid-run")
	}
	benches := na.AsSlice(observed["benches"])
	if len(benches) == 0 {
		return fmt.Errorf("save has no player bench; the census assertion would be vacuous")
	}
	report["bench_count"] = len(benches)
	facts, err := h.Wire(ctx, "colony-facts", "observations_read_colony_facts", map[string]any{"scope": scope})
	if err != nil {
		return err
	}
	_, factsObserved, err := na.Outcome(facts, "observed")
	if err != nil {
		return err
	}
	productionTokens := map[string]string{}
	for _, section := range []string{"cooking", "butchering"} {
		for _, raw := range na.AsSlice(factsObserved[section]) {
			row, _ := na.AsMap(raw)
			snapshot, _ := na.AsMap(row["benchSnapshot"])
			productionTokens[na.RefID(row["bench"])] = na.AsString(snapshot["token"])
		}
	}
	seen := map[string]bool{}
	tokenAgreements := 0
	recipeRows := 0
	for _, raw := range benches {
		row, _ := na.AsMap(raw)
		bench, _ := na.AsMap(row["bench"])
		id := na.AsString(bench["id"])
		if id == "" || seen[id] {
			return fmt.Errorf("typed bench %q missing an id or duplicated", id)
		}
		seen[id] = true
		snapshot, _ := na.AsMap(row["snapshot"])
		token := na.AsString(snapshot["token"])
		if token == "" || na.AsString(snapshot["entityId"]) != id {
			return fmt.Errorf("bench %s: missing CAS token or mismatched entity id", id)
		}
		if expected, ok := productionTokens[id]; ok {
			if expected != token {
				return fmt.Errorf("bench %s: census token %s disagrees with colony-facts token %s", id, token, expected)
			}
			tokenAgreements++
		}
		bills := na.AsSlice(row["bills"])
		for index, rawBill := range bills {
			bill, _ := na.AsMap(rawBill)
			recipe, _ := na.AsMap(bill["recipe"])
			if na.AsString(recipe["defName"]) == "" {
				return fmt.Errorf("bench %s bill %d: recipe def name missing", id, index)
			}
			if _, ok := bill["suspended"]; !ok {
				return fmt.Errorf("bench %s bill %d: suspended fact missing", id, index)
			}
		}
		recipesReply, err := h.Wire(ctx, "typed-recipes-"+id, "observations_read_recipes", map[string]any{"scope": scope, "benchId": id})
		if err != nil {
			return err
		}
		_, catalog, err := na.Outcome(recipesReply, "observed")
		if err != nil {
			return fmt.Errorf("bench %s recipes: %w", id, err)
		}
		catalogSnapshot, _ := na.AsMap(catalog["snapshot"])
		if na.AsString(catalogSnapshot["token"]) != token || na.AsString(catalogSnapshot["entityId"]) != id {
			return fmt.Errorf("bench %s: recipe catalog snapshot disagrees with the bill census", id)
		}
		recipes := na.AsSlice(catalog["recipes"])
		if len(recipes) == 0 {
			return fmt.Errorf("bench %s: empty recipe catalog", id)
		}
		for _, rawRecipe := range recipes {
			recipe, _ := na.AsMap(rawRecipe)
			definition, _ := na.AsMap(recipe["recipe"])
			if na.AsString(definition["defName"]) == "" {
				return fmt.Errorf("bench %s: recipe without a def name", id)
			}
			for _, field := range []string{"availableNow", "availableOnBench"} {
				if _, ok := recipe[field]; !ok {
					return fmt.Errorf("bench %s recipe %s: %s missing", id, definition["defName"], field)
				}
			}
			recipeRows++
		}
	}
	report["token_agreements_with_colony_facts"] = tokenAgreements
	report["recipe_rows"] = recipeRows

	for _, c := range []struct {
		label   string
		request map[string]any
	}{
		{"blank-bench", map[string]any{"scope": scope, "benchId": " "}},
	} {
		reply, err := h.Wire(ctx, c.label, "observations_read_bills", c.request)
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
			return fmt.Errorf("%s: expected FAILURE_CODE_INVALID_REQUEST, got %q", c.label, code)
		}
	}
	unknownReply, err := h.Wire(ctx, "unknown-bench-recipes", "observations_read_recipes", map[string]any{"scope": scope, "benchId": "Thing_NoSuchBench0"})
	if err != nil {
		return err
	}
	if code, ok := na.FailureCode(unknownReply); !ok || code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("unknown bench: expected FAILURE_CODE_NOT_FOUND, got %q", code)
	}

	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, after, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if afterContext, _ := after["context"].(map[string]any); !na.DeepEqual(afterContext, beforeContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}
