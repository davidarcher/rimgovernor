// The presentation/roster case checks the colonist roster's dossier against a
// real headless game: with include_dossier every row carries the observation
// PawnState of its own pawn, with the families the dashboard's Colony view
// renders (needs, health, biography, equipment) read from the live colony and
// the settings family kept out. Without the flag no row carries one.
package presentation

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:   "presentation/roster",
		Scope:  "presentation_colonists with include_dossier attaches each colonist's own PawnState (mood, health summary, skills, equipment present; settings absent) and the plain roster names the same colonists with no dossier.",
		Start:  cases.DebugStart{},
		Quiet:  na.QuietIfAvailable,
		Reason: "a read-only presentation roster that also runs on a production build",
		Budget: 3 * time.Minute,
		Run:    runRoster,
	})
}

func runRoster(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity := s.Harness(), s.Identity()
	if !na.Contains(s.Names(), "rimgovernor/presentation_colonists") {
		return fmt.Errorf("missing rimgovernor/presentation_colonists in discovery")
	}
	plain, err := rosterRows(ctx, h, "colonists", map[string]any{"identity": identity, "currentMapOnly": true})
	if err != nil {
		return err
	}
	if len(plain) == 0 {
		return fmt.Errorf("colonists: fresh debug game has no spawned colonists")
	}
	for _, row := range plain {
		if _, ok := row["dossier"]; ok {
			return fmt.Errorf("colonists: %q carries a dossier without include_dossier", na.AsString(row["pawnId"]))
		}
	}
	detailed, err := rosterRows(ctx, h, "colonists-dossier", map[string]any{"identity": identity, "currentMapOnly": true, "includeDossier": true})
	if err != nil {
		return err
	}
	if len(detailed) != len(plain) {
		return fmt.Errorf("colonists-dossier: %d rows, plain roster had %d", len(detailed), len(plain))
	}
	summary := map[string]any{}
	for i, row := range detailed {
		id := na.AsString(row["pawnId"])
		if id == "" || id != na.AsString(plain[i]["pawnId"]) {
			return fmt.Errorf("colonists-dossier: row %d names %q, plain roster names %q", i, id, na.AsString(plain[i]["pawnId"]))
		}
		dossier, ok := na.AsMap(row["dossier"])
		if !ok {
			return fmt.Errorf("colonists-dossier: %q has no dossier", id)
		}
		pawn, _ := na.AsMap(dossier["pawn"])
		if na.AsString(pawn["id"]) != id {
			return fmt.Errorf("colonists-dossier: dossier pawn %q differs from row %q", na.AsString(pawn["id"]), id)
		}
		if colonist, _ := na.AsBool(dossier["colonist"]); !colonist {
			return fmt.Errorf("colonists-dossier: %q dossier is not flagged colonist", id)
		}
		needs, _ := na.AsMap(dossier["needs"])
		health, _ := na.AsMap(dossier["health"])
		biography, _ := na.AsMap(dossier["biography"])
		if _, ok := needs["mood"]; !ok {
			return fmt.Errorf("colonists-dossier: %q dossier has no mood", id)
		}
		if _, ok := needs["food"]; !ok {
			return fmt.Errorf("colonists-dossier: %q dossier has no food need", id)
		}
		if _, ok := health["summaryFraction"]; !ok {
			return fmt.Errorf("colonists-dossier: %q dossier has no health summary", id)
		}
		if len(na.AsSlice(biography["skills"])) == 0 {
			return fmt.Errorf("colonists-dossier: %q dossier has no skills", id)
		}
		if _, ok := na.AsMap(dossier["equipment"]); !ok {
			return fmt.Errorf("colonists-dossier: %q dossier has no equipment", id)
		}
		if _, ok := dossier["settings"]; ok {
			return fmt.Errorf("colonists-dossier: %q dossier carries settings", id)
		}
		if _, ok := dossier["animalState"]; ok {
			return fmt.Errorf("colonists-dossier: %q dossier carries animal state", id)
		}
		summary[id] = map[string]any{"mood": needs["mood"], "health": health["summaryFraction"], "skills": len(na.AsSlice(biography["skills"])), "traits": len(na.AsSlice(biography["traits"]))}
	}
	report["dossiers"] = summary
	return nil
}

func rosterRows(ctx context.Context, h *na.Harness, label string, request map[string]any) ([]map[string]any, error) {
	reply, err := h.Wire(ctx, label, "presentation_colonists", request)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	_, roster, err := na.Outcome(reply, "roster")
	if err != nil {
		return nil, fmt.Errorf("%s: expected a roster outcome: %w", label, err)
	}
	rows := []map[string]any{}
	for _, raw := range na.AsSlice(roster["colonists"]) {
		row, ok := na.AsMap(raw)
		if !ok {
			return nil, fmt.Errorf("%s: colonist row must be an object", label)
		}
		rows = append(rows, row)
	}
	return rows, nil
}
