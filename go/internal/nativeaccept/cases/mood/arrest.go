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
	cases.Register(cases.Case{Name: "mood/ancient-arrest", Scope: "The Arrest GiveJobIntent delivers a standing neutral Faction.OfAncients pawn to its exact prisoner bed under owned draft; ordinary colonists still require a legal mental state.", Start: cases.Fixture{On: cases.LabStart(), Op: "test/arrest_prepare", ArgsFrom: startersite.ArgsFor(7)}, Keep: []string{string(na.NeedRest)}, Budget: 2 * time.Minute, Run: func(ctx context.Context, s cases.Session) error { return runArrestTarget(ctx, s, "ancient") }})
	cases.Register(cases.Case{
		Name:   "mood/arrest",
		Scope:  "Vanilla Arrest custody through the Arrest GiveJobIntent on Actions/Apply: refuse normal targets, unarmed arresters, hostile Berserk and non-prisoner beds; require owned draft; a resend applies again; a living sad-wander target ends in the exact prisoner bed with its mental state ended.",
		Start:  cases.Fixture{On: cases.LabStart(), Op: "test/arrest_prepare", ArgsFrom: startersite.ArgsFor(7)},
		Keep:   []string{string(na.NeedRest)},
		Budget: 2 * time.Minute,
		Run:    runArrest,
	})
}

func runArrest(ctx context.Context, s cases.Session) error { return runArrestTarget(ctx, s, "legal") }

func runArrestTarget(ctx context.Context, s cases.Session, legalFixture string) error {
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	pawnID, targetID := na.AsString(prepared["pawn"]), na.AsString(prepared["target"])
	bedID, ordinaryBedID := na.AsString(prepared["bed"]), na.AsString(prepared["ordinaryBed"])
	if pawnID == "" || targetID == "" || bedID == "" || ordinaryBedID == "" {
		return fmt.Errorf("arrest fixture lacks identities: %#v", prepared)
	}
	read := func(label string) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{pawnID}}})
		if err != nil {
			return nil, err
		}
		return na.PawnRow(reply, identity, pawnID)
	}
	scenarios := []struct {
		name, fixture, reason string
		draft                 bool
	}{
		{"normal", "normal", "not living in a mental state", true},
		{"unarmed", "unarmed", "unarmed or incapable of violence", true},
		{"berserk", "berserk", "native arrest eligibility", true},
		{"ordinary-bed", "legal", "prisoner bed", true},
		{"unowned", "legal", "eligible drafted pawn", false},
		{"legal", legalFixture, "", true},
	}
	for _, scenario := range scenarios {
		staged, err := h.Call(ctx, "stage-"+scenario.name, "test/arrest_stage", map[string]any{"pawnId": pawnID, "targetId": targetID, "scenario": scenario.fixture})
		if err != nil {
			return err
		}
		if ok, _ := na.AsBool(staged["success"]); !ok {
			return fmt.Errorf("stage %s: %#v", scenario.name, staged)
		}
		if _, err := na.GrantAuto(ctx, h.WireFunc(), "grant-"+scenario.name, identity); err != nil {
			return err
		}
		row, err := read("before-" + scenario.name)
		if err != nil {
			return err
		}
		if scenario.draft {
			if _, err := na.ApplyDraft(ctx, h, "draft-"+scenario.name, identity, "draft-"+scenario.name, pawnID, true); err != nil {
				return err
			}
			row, err = read("drafted-" + scenario.name)
			if err != nil {
				return err
			}
		}
		bed := bedID
		if scenario.name == "ordinary-bed" {
			bed = ordinaryBedID
		}
		intent := na.GiveJob(pawnID, "Arrest", targetID, bed)
		executed, err := na.ApplyOne(ctx, h, "apply-"+scenario.name, identity, "arrest-"+scenario.name, intent)
		if err != nil {
			return err
		}
		if scenario.reason != "" {
			if err := na.Refused(scenario.name, executed, scenario.reason); err != nil {
				return err
			}
			s.Report()["refused_"+scenario.name] = executed
			after, err := read("after-refusal-" + scenario.name)
			if err != nil {
				return err
			}
			if err := na.SameControl(row, after); err != nil {
				return fmt.Errorf("refusal mutated arrester: %w", err)
			}
			continue
		}
		job, err := na.AppliedJob("arrest", executed)
		if err != nil {
			return err
		}
		if issued, _ := na.AsBool(job["issued"]); !issued || na.AsString(job["jobDef"]) != "Arrest" {
			return fmt.Errorf("arrest was not issued: %#v", executed)
		}
		// A resend while the arrest runs applies again without a second order.
		again, err := na.ApplyOne(ctx, h, "apply-legal-again", identity, "arrest-legal-again", intent)
		if err != nil {
			return err
		}
		if job, err := na.AppliedJob("arrest resend", again); err != nil {
			return err
		} else if issued, _ := na.AsBool(job["issued"]); issued {
			return fmt.Errorf("arrest resend issued a second job: %#v", again)
		}
	}
	var inspected map[string]any
	if _, err := na.RunUntil(ctx, h, "arrest-custody", 6000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		got, err := h.Call(ctx, "custody-inspect", "test/arrest_inspect", map[string]any{"targetId": targetID})
		if err != nil {
			return "", false, err
		}
		inspected = got
		if alive, _ := na.AsBool(got["alive"]); !alive || na.AsNumber(got["deadColonists"]) != 0 {
			return "", false, fmt.Errorf("arrest cost a life: %#v", got)
		}
		mental, _ := na.AsBool(got["mental"])
		prisoner, _ := na.AsBool(got["prisoner"])
		return fmt.Sprint(mental, prisoner, got["bed"]), !mental && prisoner && na.AsString(got["bed"]) == bedID, nil
	}); err != nil {
		return err
	}
	s.Report()["custody"] = inspected
	return nil
}
