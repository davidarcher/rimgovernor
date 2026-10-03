package medical

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// medical/surgery-elective-rich and medical/surgery-elective-poor (#1848,
// epic #1829): the same hospital, doctors and stock run twice, once in a
// colony holding 6000 gold and once in one stripped of every loose item but
// wood, medicine, the prosthetic leg, the two bionic parts and eight meals
// (test/medical_management_setup condition elective, wealth rich or poor).
// The third colonist is missing a leg with a SimpleProstheticLeg stocked (the
// one served operation); a BionicEye and a BionicArm are stocked for healthy
// colonists (the electives). The pair diverges only through the colonists'
// personal shares (#1843): the rich colony installs an elective, the poor one
// installs none and MaintainSurgery recovers once the leg is served.
//
// Shared asserts: served operations win (no elective intent exists before the
// leg's bill, and the leg is installed by the end), and one elective at a time
// (the first elective intent seen is the only one, and no colonist ends with
// two queued bills).
func init() {
	for _, c := range []struct {
		name, wealth, scope string
	}{
		{"medical/surgery-elective-rich", "rich",
			"Elective share gate (#1848): in a rich colony the served leg restore is queued first, then exactly one stocked bionic (eye or arm) is queued as an elective and a native doctor installs it."},
		{"medical/surgery-elective-poor", "poor",
			"Elective share gate (#1848): in a poor colony the served leg restore is queued and installed, no stocked bionic is ever queued or installed (the shares do not cover them), and MaintainSurgery recovers."},
	} {
		wealth := c.wealth
		cases.Register(cases.Case{
			Name:  c.name,
			Scope: c.scope,
			Start: cases.Fixture{Op: "test/medical_management_setup", On: cases.LabStart(),
				Args: map[string]any{"disease": false, "condition": "elective", "wealth": wealth}},
			Service: true,
			Budget:  12 * time.Minute,
			Run:     func(ctx context.Context, s cases.Session) error { return surgeryElective(ctx, s, wealth) },
		})
	}
}

// electiveRecipes are the stocked bionics' install recipes.
var electiveRecipes = map[string]string{"InstallBionicEye": "BionicEye", "InstallBionicArm": "BionicArm"}

// electiveIntents are the distinct elective intents in the journal history.
func electiveIntents(surgeries []domain.Surgery) []domain.Surgery {
	seen := map[string]bool{}
	var out []domain.Surgery
	for _, s := range surgeries {
		key := fmt.Sprintf("%s/%d/%s", s.Pawn(), s.Part(), s.Recipe())
		if _, ok := electiveRecipes[s.Recipe()]; ok && !seen[key] {
			seen[key] = true
			out = append(out, s)
		}
	}
	return out
}

// hasInstalled is whether the pawn wears an added part that spawns the item.
func hasInstalled(health map[string]any, item string) bool {
	for _, raw := range na.AsSlice(health["installedParts"]) {
		part, _ := na.AsMap(raw)
		if na.AsString(part["spawnThingDefName"]) == item {
			return true
		}
	}
	return false
}

func surgeryElective(ctx context.Context, s cases.Session, wealth string) error {
	prepared, report := s.Prepared(), s.Report()
	leg := na.AsString(prepared["surgical"])
	legPart, ok := prepared["part"].(float64)
	if leg == "" || !ok {
		return fmt.Errorf("medical_management_setup reply lacks surgical/part: %#v", prepared)
	}
	report["wealth"], report["wealth_items"] = wealth, prepared["wealthItems"]
	pawns := []string{leg}
	for _, raw := range na.AsSlice(prepared["patients"]) {
		pawns = append(pawns, na.AsString(raw))
	}
	run, err := startSurgeryRun(ctx, s, "surgery-elective-"+wealth)
	if err != nil {
		return err
	}
	defer run.stop()
	// Served first: the leg's bill must be journaled before any elective, and
	// no elective may appear while it has not been.
	if err = run.until(ctx, 4*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
		_, served := findSurgery(surgeries, leg, int(legPart), "InstallSimpleProstheticLeg")
		if early := electiveIntents(surgeries); !served && len(early) > 0 {
			return "", false, fmt.Errorf("elective %s queued on %s before the served leg restore", early[0].Recipe(), early[0].Pawn())
		}
		return na.Signature(len(surgeries)), served, nil
	}); err != nil {
		return err
	}
	report["leg_intent"] = true
	var from domain.Tick
	follow := func(label string, limit time.Duration, done func(tick domain.Tick, surgeries []domain.Surgery) (bool, error)) error {
		return run.until(ctx, limit, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
			tick, err := run.tick(ctx)
			if err != nil {
				return "", false, err
			}
			if from == 0 {
				from = tick
			}
			ok, err := done(tick, surgeries)
			if err != nil {
				return "", false, fmt.Errorf("%s: %w", label, err)
			}
			return na.Signature(tick), ok, nil
		})
	}
	if wealth == "rich" {
		var first []domain.Surgery
		if err = run.until(ctx, 8*time.Minute, func(ctx context.Context, surgeries []domain.Surgery) (string, bool, error) {
			first = electiveIntents(surgeries)
			return na.Signature(len(surgeries)), len(first) > 0, nil
		}); err != nil {
			return fmt.Errorf("no elective was queued in the rich colony: %w", err)
		}
		// One at a time: the first review that books an elective books one.
		if len(first) != 1 {
			return fmt.Errorf("%d elective intents at first sight, want 1: %v", len(first), first)
		}
		report["elective_intent"] = map[string]any{"pawn": string(first[0].Pawn()), "part": first[0].Part(), "recipe": first[0].Recipe()}
		from = 0
		err = follow("rich served window", 4*time.Minute, func(tick domain.Tick, _ []domain.Surgery) (bool, error) {
			return tick >= from+surgeryServedTicks, nil
		})
	} else {
		// Unaffordable: no elective for a served window past the leg, and the
		// goal recovers once the leg is done.
		err = follow("poor served window", 6*time.Minute, func(tick domain.Tick, surgeries []domain.Surgery) (bool, error) {
			if got := electiveIntents(surgeries); len(got) > 0 {
				return false, fmt.Errorf("elective %s queued on %s in the poor colony", got[0].Recipe(), got[0].Pawn())
			}
			if tick < from+surgeryServedTicks {
				return false, nil
			}
			recovered, err := run.recovered(ctx, policy.MaintainSurgery)
			return recovered, err
		})
	}
	if err != nil {
		return err
	}
	run.stop()
	if _, err = s.Reattach(ctx); err != nil {
		return err
	}
	installed, queued := 0, 0
	for _, pawn := range pawns {
		health, err := pawnHealth(ctx, s, "surgery-elective-"+pawn, pawn)
		if err != nil {
			return err
		}
		if pawn == leg && partMissing(health, int(legPart)) {
			return fmt.Errorf("the served leg restore was not installed on %s", leg)
		}
		for _, part := range electiveRecipes {
			if hasInstalled(health, part) {
				installed++
			}
		}
		electiveQueued := 0
		for _, raw := range na.AsSlice(health["surgeryBills"]) {
			bill, _ := na.AsMap(raw)
			if _, ok := electiveRecipes[na.AsString(bill["recipe"])]; ok {
				electiveQueued++
			}
		}
		if electiveQueued > 1 {
			return fmt.Errorf("%s has %d queued elective bills", pawn, electiveQueued)
		}
		queued += electiveQueued
	}
	report["elective_installed"], report["elective_queued"] = installed, queued
	if queued > 1 {
		return fmt.Errorf("%d elective bills queued colony-wide, want at most 1", queued)
	}
	switch wealth {
	case "rich":
		if installed < 1 {
			return fmt.Errorf("rich colony installed no elective bionic after %d served ticks", surgeryServedTicks)
		}
	default:
		if installed != 0 || queued != 0 {
			return fmt.Errorf("poor colony installed %d and queued %d elective bionics", installed, queued)
		}
	}
	return nil
}
