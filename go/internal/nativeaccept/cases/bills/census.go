// The bills/census case proves the typed bench census behind
// rimgovernor/observations_read_bills and observations_read_recipes (#77)
// against the legacy home/bills listing on a loaded save: every player bench
// appears once with a CAS token, its bill stack matches, the census token
// agrees with the colony-facts production token for the same bench, and each
// bench's recipe catalog is complete with products and ingredient counts.
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
		Scope:  "Typed bench/bill census and per-bench recipe catalog against the legacy home/bills listing; read-only, no bill changes or gameplay orders.",
		Start:  cases.Fixture{Op: "test/routine_production_prepare", On: cases.Save{Name: "RimGovernor-tribal8-baseline"}},
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

	legacy, err := h.Call(ctx, "legacy-bills", "home/bills", map[string]any{"action": "list"})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(legacy["success"]); !success {
		return fmt.Errorf("legacy home/bills refused")
	}
	legacyBenches := map[string]map[string]any{}
	for _, raw := range na.AsSlice(legacy["benches"]) {
		row, _ := na.AsMap(raw)
		legacyBenches["Thing_"+na.AsString(row["thingId"])] = row
	}
	if len(legacyBenches) == 0 {
		return fmt.Errorf("save has no player bench; the census assertion would be vacuous")
	}
	report["legacy_bench_count"] = len(legacyBenches)

	billsReply, err := h.Wire(ctx, "typed-bills", "observations_read_bills", map[string]any{"scope": scope, "page": map[string]any{"limit": 256}})
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
	if err := na.CheckCompleteness(observed["completeness"], len(legacyBenches)); err != nil {
		return fmt.Errorf("bills completeness: %w", err)
	}
	if len(benches) != len(legacyBenches) {
		return fmt.Errorf("expected %d bench rows, found %d", len(legacyBenches), len(benches))
	}
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
			bench, _ := na.AsMap(row["bench"])
			snapshot, _ := na.AsMap(bench["snapshot"])
			productionTokens[na.AsString(bench["id"])] = na.AsString(snapshot["token"])
		}
	}
	seen := map[string]bool{}
	tokenAgreements := 0
	recipeRows := 0
	for _, raw := range benches {
		row, _ := na.AsMap(raw)
		bench, _ := na.AsMap(row["bench"])
		id := na.AsString(bench["id"])
		source, ok := legacyBenches[id]
		if !ok || seen[id] {
			return fmt.Errorf("typed bench %q missing from or duplicated against the legacy listing", id)
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
		if int(na.AsNumber(source["billCount"])) != len(bills) {
			return fmt.Errorf("bench %s: legacy lists %v bills, typed census %d", id, source["billCount"], len(bills))
		}
		if err := na.CheckCompleteness(row["completeness"], len(bills)); err != nil {
			return fmt.Errorf("bench %s bill completeness: %w", id, err)
		}
		if usable, _ := na.AsBool(row["usable"]); usable {
			if legacyUsable, _ := na.AsBool(source["usableForBills"]); !legacyUsable {
				return fmt.Errorf("bench %s: typed usable but legacy not usable for bills", id)
			}
		}
		legacyBills := na.AsSlice(source["bills"])
		for index, rawBill := range bills {
			bill, _ := na.AsMap(rawBill)
			recipe, _ := na.AsMap(bill["recipe"])
			legacyBill, _ := na.AsMap(legacyBills[index])
			if na.AsString(recipe["defName"]) != na.AsString(legacyBill["recipe"]) {
				return fmt.Errorf("bench %s bill %d: recipe %v vs legacy %v", id, index, recipe["defName"], legacyBill["recipe"])
			}
			if _, ok := bill["suspended"]; !ok {
				return fmt.Errorf("bench %s bill %d: suspended fact missing", id, index)
			}
		}
		recipesReply, err := h.Wire(ctx, "typed-recipes-"+id, "observations_read_recipes", map[string]any{"scope": scope, "benchId": id, "page": map[string]any{"limit": 256}})
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
		if err := na.CheckCompleteness(catalog["completeness"], len(recipes)); err != nil {
			return fmt.Errorf("bench %s recipe completeness: %w", id, err)
		}
		if len(recipes) == 0 {
			return fmt.Errorf("bench %s: empty recipe catalog", id)
		}
		for _, rawRecipe := range recipes {
			recipe, _ := na.AsMap(rawRecipe)
			if err := checkMealFacts(recipe); err != nil {
				return err
			}
			definition, _ := na.AsMap(recipe["recipe"])
			if na.AsString(definition["defName"]) == "" {
				return fmt.Errorf("bench %s: recipe without a def name", id)
			}
			for _, field := range []string{"availableNow", "availableOnBench", "workAmount"} {
				if _, ok := recipe[field]; !ok {
					return fmt.Errorf("bench %s recipe %s: %s missing", id, definition["defName"], field)
				}
			}
			for _, rawSlot := range na.AsSlice(recipe["ingredients"]) {
				slot, _ := na.AsMap(rawSlot)
				if complete, _ := na.AsBool(slot["complete"]); !complete || na.AsNumber(slot["required"]) <= 0 || len(na.AsSlice(slot["allowedDefNames"])) == 0 {
					return fmt.Errorf("bench %s recipe %s: ingredient slot without allowed names or a positive required count", id, definition["defName"])
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
		{"bad-page", map[string]any{"scope": scope, "page": map[string]any{"limit": 257}}},
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
