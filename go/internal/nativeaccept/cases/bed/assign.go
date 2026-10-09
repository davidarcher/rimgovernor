// The bed/assign case exercises the AssignIntent on Actions/Apply. AssignActionHandler
// (integrations/rimgovernor-native/src/Bridge/Protocol/
// NativeAssignOperations.cs) drives the same
// CompAssignableToPawn.TryAssignPawn write the Assign tab uses. A
// colonist who genuinely owns one bed
// actually has their bed ownership reassigned to a different, real,
// previously-unclaimed compliant bed, observed via a real
// rimgovernor/observations_list_pawns readback (not just a result).
// TryAssignPawn is synchronous, so the case does not poll game ticks.
//
// Uses the disposable test/bed_assign_prepare fixture (BedAssignFixture.cs)
// since a deterministic pre-owned bed and a second, unclaimed, compliant
// target bed cannot be relied on from native random colony bed layout.
package bed

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "bed/assign",
		Scope: "Native CompAssignableToPawn.TryAssignPawn (AssignIntent on Actions/Apply): a colonist who " +
			"genuinely owns one bed actually has their bed ownership reassigned to a different real, previously-" +
			"unclaimed compliant bed, a drifted previous-bed expectation is refused without mutation, the real " +
			"bed-ownership change is observed via native readback (not just a result), and a resent intent " +
			"applies again.",
		// Sited on the audited baseline, not a fresh random world.
		Start:  cases.Fixture{Op: "test/bed_assign_prepare", On: cases.LabStart()},
		Quiet:  na.QuietRequired,
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	// The fixture edits pawn/building state directly outside any
	// authority.Owned() scope, so authority is granted after it ran.
	h, identity, names, prepared := s.Harness(), s.Identity(), s.Names(), s.Prepared()
	if !na.Contains(names, "rimgovernor/operations_apply") {
		return fmt.Errorf("missing rimgovernor/operations_apply in discovery")
	}
	if !na.Contains(names, "rimgovernor/observations_list_pawns") {
		return fmt.Errorf("missing rimgovernor/observations_list_pawns in discovery")
	}

	pawnID := na.AsString(prepared["pawn"])
	previousBedID := na.AsString(prepared["previousBed"])
	bedID := na.AsString(prepared["bed"])
	if pawnID == "" || previousBedID == "" || bedID == "" {
		return fmt.Errorf("prepare: missing fixture pawn/previousBed/bed ids: %#v", prepared)
	}
	report["fixture_pawn"] = pawnID
	report["fixture_previous_bed"] = previousBedID
	report["fixture_bed"] = bedID

	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}

	// ownedBed reads the pawn's current ownedBedId through
	// rimgovernor/observations_list_pawns.
	ownedBed := func(label string) (string, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope":   map[string]any{"expectedIdentity": identity},
			"filter":  map[string]any{"ids": []string{pawnID}},
			"details": map[string]any{},
		})
		if err != nil {
			return "", err
		}
		_, observed, err := na.Outcome(reply, "observed")
		if err != nil {
			return "", err
		}
		rows := na.AsSlice(observed["pawns"])
		if len(rows) != 1 {
			return "", fmt.Errorf("%s: expected exactly one observed pawn, got %#v", label, observed)
		}
		row, _ := na.AsMap(rows[0])
		pawn, _ := na.AsMap(row["pawn"])
		if na.AsString(pawn["id"]) != pawnID {
			return "", fmt.Errorf("%s: unexpected pawn row: %#v", label, row)
		}
		return na.RefID(row["ownedBed"]), nil
	}

	// apply sends one AssignIntent and returns the action's result.
	apply := func(label, key, expectedPrevious string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{
			"key": key, "assign": map[string]any{"pawnId": pawnID, "thingId": bedID, "expectedPrevious": map[string]any{"entityId": expectedPrevious}},
		}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}

	before, err := ownedBed("pawn-before")
	if err != nil {
		return err
	}
	if before != previousBedID {
		return fmt.Errorf("pawn-before: expected the fixture colonist to own %q, got %q", previousBedID, before)
	}
	report["pawn_before_owned_bed"] = before

	// Refusal: a previous-bed expectation that no longer holds is refused
	// and leaves ownership alone.
	drifted, err := apply("apply-drifted-previous", "bed-drifted", "Thing_NotThePreviousBed")
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(drifted["refused"]); !ok {
		return fmt.Errorf("apply-drifted-previous: expected a refusal, got %#v", drifted)
	}
	if owned, err := ownedBed("pawn-after-refusal"); err != nil {
		return err
	} else if owned != previousBedID {
		return fmt.Errorf("pawn-after-refusal: expected an unchanged owned bed, got %q", owned)
	}

	// Apply, then resend: both apply, the second as the ownership stands.
	for _, label := range []string{"apply-bed", "resend-bed"} {
		result, err := apply(label, "bed-assign", previousBedID)
		if err != nil {
			return err
		}
		receipt, _ := na.AsMap(result["applied"])
		applied, _ := na.AsMap(receipt["applied"])
		observed, _ := na.AsMap(applied["observed"])
		effect, ok := na.AsMap(observed["assign"])
		if !ok || na.AsString(effect["pawnId"]) != pawnID || na.AsString(effect["thingId"]) != bedID {
			return fmt.Errorf("%s: expected applied bed evidence, got %#v", label, result)
		}
		if assigned, _ := na.AsBool(effect["assigned"]); !assigned {
			return fmt.Errorf("%s: expected assigned=true, got %#v", label, effect)
		}
		after, err := ownedBed("pawn-after-" + label)
		if err != nil {
			return err
		}
		if after != bedID {
			return fmt.Errorf("pawn-after-%s: expected the native colonist to now own %q, got %q", label, bedID, after)
		}
		report["pawn_after_owned_bed"] = after
	}
	report["bed_reassigned"] = true

	// Swap: another colonist's plain claim on the now-owned bed is
	// refused as already assigned; flagged as a swap it evicts the owner.
	reply, err := h.Wire(ctx, "list-colonists", "observations_list_pawns", map[string]any{
		"scope":   map[string]any{"expectedIdentity": identity},
		"filter":  map[string]any{"colonist": true, "downed": false, "drafted": false},
		"details": map[string]any{},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	otherID, otherBed := "", ""
	for _, raw := range na.AsSlice(observed["pawns"]) {
		row, _ := na.AsMap(raw)
		pawn, _ := na.AsMap(row["pawn"])
		if id := na.AsString(pawn["id"]); id != "" && id != pawnID {
			otherID, otherBed = id, na.RefID(row["ownedBed"])
			break
		}
	}
	if otherID == "" {
		return fmt.Errorf("list-colonists: no second colonist to swap in: %#v", observed)
	}
	report["swap_pawn"] = otherID
	claim := func(label, key string, swap bool) (map[string]any, error) {
		previous := map[string]any{"clear": map[string]any{}}
		if otherBed != "" {
			previous = map[string]any{"entityId": otherBed}
		}
		intent := map[string]any{"pawnId": otherID, "thingId": bedID, "expectedPrevious": previous}
		if swap {
			intent["swap"] = true
		}
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": key, "assign": intent}}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}
	plain, err := claim("apply-owned-plain", "bed-owned-plain", false)
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(plain["refused"]); !ok {
		return fmt.Errorf("apply-owned-plain: expected an already-assigned refusal, got %#v", plain)
	}
	swapped, err := claim("apply-owned-swap", "bed-owned-swap", true)
	if err != nil {
		return err
	}
	if _, ok := na.AsMap(swapped["applied"]); !ok {
		return fmt.Errorf("apply-owned-swap: expected the swap to apply, got %#v", swapped)
	}
	if owned, err := ownedBed("pawn-after-swap"); err != nil {
		return err
	} else if owned == bedID {
		return fmt.Errorf("pawn-after-swap: expected the evicted colonist to lose %q", bedID)
	}
	report["swap_evicted"] = true

	return nil
}
