package mood

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
)

func init() {
	cases.Register(cases.Case{Name: "mood/subdue", Scope: "Ordinary melee containment through a subdue MeleeIntent on Actions/Apply: refuse non-aggro targets and ranged weapons; draft and order a blunt attack, resend applies again, and the colonist ends living, downed or recovered, without prisoner conversion.", Start: cases.Fixture{Op: "test/subdue_prepare", ArgsFrom: startersite.ArgsFor(7), On: cases.LabStart()}, Budget: 2 * time.Minute, Run: runSubdue})
}

func runSubdue(ctx context.Context, s cases.Session) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	pawn, target := na.AsString(prepared["pawn"]), na.AsString(prepared["target"])
	intent := map[string]any{"melee": map[string]any{"pawnId": pawn, "targetId": target, "subdue": true}}
	read := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{pawn}}})
		if err != nil {
			return nil, err
		}
		return na.PawnRow(reply, identity, pawn)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "grant", identity); err != nil {
		return err
	}
	for _, scenario := range []struct{ name, reason string }{{"normal", "standing aggressive colonist"}, {"ranged", "ranged weapons"}} {
		if _, err := h.Call(ctx, "stage-"+scenario.name, "test/subdue_stage", map[string]any{"pawnId": pawn, "targetId": target, "scenario": scenario.name}); err != nil {
			return err
		}
		before, err := read("before-" + scenario.name)
		if err != nil {
			return err
		}
		result, err := na.ApplyOne(ctx, h, "apply-"+scenario.name, identity, "subdue-"+scenario.name, intent)
		if err != nil {
			return err
		}
		if err := na.Refused(scenario.name, result, scenario.reason); err != nil {
			return err
		}
		after, err := read("after-refusal-" + scenario.name)
		if err != nil {
			return err
		}
		if err := na.SameControl(before, after); err != nil {
			return err
		}
	}
	if _, err := h.Call(ctx, "stage-legal", "test/subdue_stage", map[string]any{"pawnId": pawn, "targetId": target, "scenario": "legal"}); err != nil {
		return err
	}
	result, err := na.ApplyOne(ctx, h, "apply-legal", identity, "subdue-legal", intent)
	if err != nil {
		return err
	}
	job, err := na.AppliedJob("subdue", result)
	if err != nil {
		return err
	}
	if issued, _ := na.AsBool(job["issued"]); !issued || na.AsString(job["jobDef"]) != "AttackMelee" {
		return fmt.Errorf("subdue was not ordered: %#v", result)
	}
	selected, err := h.Call(ctx, "blunt-preference", "test/subdue_inspect", map[string]any{"targetId": target, "pawnId": pawn})
	if err != nil {
		return err
	}
	if blunt, known := na.AsBool(selected["blunt"]); !known || !blunt {
		return fmt.Errorf("subdue did not prefer a legal blunt attack: %#v", selected)
	}
	// A resend while the attack runs applies again without a second order.
	again, err := na.ApplyOne(ctx, h, "apply-legal-again", identity, "subdue-legal-again", intent)
	if err != nil {
		return err
	}
	if job, err := na.AppliedJob("subdue resend", again); err != nil {
		return err
	} else if issued, _ := na.AsBool(job["issued"]); issued {
		return fmt.Errorf("subdue resend issued a second job: %#v", again)
	}
	var facts map[string]any
	if _, err := na.RunUntil(ctx, h, "subdue-contained", 6000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "subdue-inspect", "test/subdue_inspect", map[string]any{"targetId": target, "pawnId": pawn})
		if err != nil {
			return "", false, err
		}
		facts = got
		alive, _ := na.AsBool(got["alive"])
		if !alive {
			return "", false, fmt.Errorf("subdue killed the colonist: %#v", got)
		}
		downed, _ := na.AsBool(got["downed"])
		aggro, _ := na.AsBool(got["aggro"])
		return fmt.Sprint(downed, aggro), downed || !aggro, nil
	}); err != nil {
		return err
	}
	if prisoner, known := na.AsBool(facts["prisoner"]); !known || prisoner {
		return fmt.Errorf("containment failed: %#v", facts)
	}
	s.Report()["containment"] = facts
	return nil
}
