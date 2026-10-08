package combatlab

import (
	"context"
	"fmt"
	"slices"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "combatlab/combat_drug",
		Scope: "The combat_drug order (#1311), a native op contract no snapshot can prove: on lab-open with colonist 0 carrying go-juice, colonist 1 " +
			"carrying nothing and colonist 2 carrying go-juice while already GoJuiceHigh, all drafted, one combat.orders call orders combat_drug for each; " +
			"colonist 0's applies as Ingest, colonist 1 refuses no_drug and colonist 2 already_high, and 300 ticks later colonist 0 is GoJuiceHigh.",
		Start:       cases.Lab{Colonists: 3},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Crew:        cases.Crew{Size: 3}, Run: runCombatDrug,
	})
}

func runCombatDrug(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	staged, err := Stage(ctx, h, "lab-open", func(f *Fixture, _, _ int) {
		for i := range f.Pawns {
			if f.Pawns[i].Side != Colonist {
				continue
			}
			switch f.Pawns[i].Index {
			case 0:
				f.Pawns[i].Inventory = []string{"GoJuice"}
			case 2:
				f.Pawns[i].Inventory, f.Pawns[i].Hediffs = []string{"GoJuice"}, []string{"GoJuiceHigh"}
			}
		}
	})
	if err != nil {
		return err
	}
	colonists := staged.Colonists()
	if len(colonists) != 3 {
		return fmt.Errorf("staged %d colonists, want 3", len(colonists))
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "combat-drug-acquire", identity); err != nil {
		return err
	}
	if err := draftAll(ctx, h, identity, "draft", colonists); err != nil {
		return err
	}
	var orders []any
	for _, id := range colonists {
		orders = append(orders, map[string]any{"pawn": map[string]any{"entityId": id}, "combatDrug": "GoJuice"})
	}
	results, err := issue(ctx, h, identity, "combat-drug", orders)
	if err != nil {
		return err
	}
	report["results"] = results
	if len(results) != 3 {
		return fmt.Errorf("%d results, want 3: %v", len(results), results)
	}
	if applied, _ := na.AsBool(results[0]["applied"]); !applied || na.AsString(results[0]["jobDef"]) != "Ingest" {
		return fmt.Errorf("colonist 0: want applied Ingest, got %v", results[0])
	}
	for i, want := range map[int]string{1: "no_drug", 2: "already_high"} {
		if applied, _ := na.AsBool(results[i]["applied"]); applied || na.AsString(results[i]["refusal"]) != want {
			return fmt.Errorf("colonist %d: want refusal %s, got %v", i, want, results[i])
		}
	}
	after, err := tickReadN(ctx, h, 300)
	if err != nil {
		return err
	}
	p := after.pawns[colonists[0]]
	report["after"] = p
	hediffs, _ := p["hediffs"].([]any)
	if !slices.ContainsFunc(hediffs, func(v any) bool { return na.AsString(v) == "GoJuiceHigh" }) {
		return fmt.Errorf("colonist 0 not GoJuiceHigh 300 ticks after the order: %v", p)
	}
	return nil
}
