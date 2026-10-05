// lifecycle/new_colony_team is the native acceptance for the built-in
// team-composition policy (#2024, epic #2019): three seeds each start a
// four-colonist colony whose team meets the policy (no hard-rejected trait, no
// colonist incapable of Construction or Hauling, coverage of Plants, Cooking,
// Construction, Medicine and Mining, two Shooting-passion and one
// Melee-passion colonist), and the first seed started a second time gives the
// same team. Each start needs a fresh main menu, so each opens its own game
// (four launches). Permanent health conditions are rejected natively but not
// observable through the pawn list, so they are covered by the probe
// native-team-policy, not here.
package lifecycle

import (
	"context"
	"fmt"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const teamColonistCount = 4

func init() {
	cases.Register(cases.Case{
		Name: "lifecycle/new_colony_team",
		Scope: "Team-composition policy (#2024): three seeds give four-colonist teams with no rejected trait, Construction and Hauling capability on everyone, " +
			"Plants/Cooking/Construction/Medicine/Mining coverage, two Shooting-passion and one Melee-passion colonist; the first seed twice gives the same team; " +
			"Permanent health rejection is covered by the native-team-policy probe.",
		Start:  cases.Owned{},
		Reason: "each start needs a fresh main menu process, so every seed opens and retires its own game",
		NoKeep: true,
		Budget: 45 * time.Minute,
		Run:    runNewColonyTeam,
	})
}

func teamSpec(seed string) map[string]any {
	spec := newColonySpec()
	spec["colonistCount"], spec["seed"], spec["saveName"] = teamColonistCount, seed, seed
	return spec
}

func runNewColonyTeam(ctx context.Context, s cases.Session) error {
	cfg, report := s.Config(), s.Report()
	seeds := []string{"team-accept-a", "team-accept-b", "team-accept-c", "team-accept-a"}
	var first string
	for i, seed := range seeds {
		label := fmt.Sprintf("%s-%d", seed, i)
		team, err := teamLaunch(ctx, cfg, report, label, seed)
		if err != nil {
			return err
		}
		if err := checkTeamPolicy(team); err != nil {
			return fmt.Errorf("%s: %w", label, err)
		}
		digest := team.digest()
		report["team_"+label] = digest
		if i == 0 {
			first = digest
		} else if i == len(seeds)-1 && digest != first {
			return fmt.Errorf("seed %s twice gave different teams:\nfirst:  %s\nsecond: %s", seed, first, digest)
		}
	}
	return nil
}

type teamPawn struct {
	name      string
	traits    []string // "Def:degree"
	levels    map[string]int
	passions  map[string]string
	disabled  map[string]bool // skill totally disabled
	incapable map[string]bool // incapable work types
}

type teamRead struct {
	pawns []teamPawn
}

func (t *teamRead) digest() string {
	var rows []string
	for _, p := range t.pawns {
		var skills []string
		for k, v := range p.levels {
			skills = append(skills, fmt.Sprintf("%s=%d/%s", k, v, p.passions[k]))
		}
		sort.Strings(skills)
		traits := append([]string(nil), p.traits...)
		sort.Strings(traits)
		rows = append(rows, fmt.Sprintf("%s{%s}[%s]", p.name, strings.Join(traits, ","), strings.Join(skills, ",")))
	}
	sort.Strings(rows)
	return strings.Join(rows, ";")
}

func teamLaunch(ctx context.Context, cfg *na.Config, report na.Report, label, seed string) (*teamRead, error) {
	reuse, err := na.OpenReusableGame(ctx, cfg)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	defer func() {
		retireCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer cancel()
		_ = reuse.Retire(retireCtx, label+" launch complete")
	}()
	h, err := reuse.Session(ctx)
	if err != nil {
		return nil, err
	}
	id := fmt.Sprintf("team-%s-%d", label, time.Now().UnixNano())
	started := time.Now()
	if _, _, err := newColonyRun(ctx, h, id, teamSpec(seed), label); err != nil {
		return nil, err
	}
	row := map[string]any{"finish_ms": time.Since(started).Milliseconds()}
	report["team_run_"+label] = row

	identity, err := na.ReadIdentity(ctx, h, label+"-identity")
	if err != nil {
		return nil, err
	}
	reply, err := h.Wire(ctx, label+"-pawns", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"colonist": true},
		"details": map[string]any{"biography": true},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, fmt.Errorf("pawns: %w", err)
	}
	team := &teamRead{}
	for _, raw := range na.AsSlice(observed["pawns"]) {
		pawn, _ := na.AsMap(raw)
		ref, _ := na.AsMap(pawn["pawn"])
		bio, _ := na.AsMap(pawn["biography"])
		p := teamPawn{name: na.AsString(ref["label"]), levels: map[string]int{}, passions: map[string]string{},
			disabled: map[string]bool{}, incapable: map[string]bool{}}
		for _, rawSkill := range na.AsSlice(bio["skills"]) {
			skill, _ := na.AsMap(rawSkill)
			def := na.AsString(skill["defName"])
			p.levels[def] = int(na.AsNumber(skill["level"]))
			p.passions[def] = strings.TrimPrefix(na.AsString(skill["passion"]), "PASSION_")
			if disabled, _ := skill["disabled"].(bool); disabled {
				p.disabled[def] = true
			}
		}
		for _, rawTrait := range na.AsSlice(bio["traits"]) {
			trait, _ := na.AsMap(rawTrait)
			p.traits = append(p.traits, fmt.Sprintf("%s:%d", na.AsString(trait["defName"]), int(na.AsNumber(trait["degree"]))))
		}
		for _, w := range na.AsSlice(bio["incapableWorkTypes"]) {
			p.incapable[na.AsString(w)] = true
		}
		team.pawns = append(team.pawns, p)
	}
	if len(team.pawns) != teamColonistCount {
		return nil, fmt.Errorf("%s: pawn list has %d colonists, want %d", label, len(team.pawns), teamColonistCount)
	}
	row["team"] = team.digest()
	return team, nil
}

// checkTeamPolicy asserts the observable parts of the policy on a read team.
func checkTeamPolicy(t *teamRead) error {
	rejected := map[string]bool{"Pyromaniac:0": true, "Nerves:-2": true, "Wimp:0": true, "Industriousness:-2": true, "DrugDesire:2": true}
	workTypes := map[string]string{"Plants": "Growing", "Cooking": "Cooking", "Construction": "Construction", "Medicine": "Doctor", "Mining": "Mining"}
	covered := map[string]bool{}
	shooters, melee := 0, 0
	for _, p := range t.pawns {
		for _, trait := range p.traits {
			if rejected[trait] {
				return fmt.Errorf("%s has the rejected trait %s", p.name, trait)
			}
		}
		if p.incapable["Construction"] || p.incapable["Hauling"] {
			return fmt.Errorf("%s is incapable of Construction or Hauling (incapable: %v)", p.name, p.incapable)
		}
		for skill, work := range workTypes {
			if !p.disabled[skill] && !p.incapable[work] {
				covered[skill] = true
			}
		}
		if p.passions["Shooting"] == "MINOR" || p.passions["Shooting"] == "MAJOR" {
			shooters++
		}
		if p.passions["Melee"] == "MINOR" || p.passions["Melee"] == "MAJOR" {
			melee++
		}
	}
	for skill := range workTypes {
		if !covered[skill] {
			return fmt.Errorf("no colonist is capable of %s", skill)
		}
	}
	if shooters < 2 || melee < 1 {
		return fmt.Errorf("combat passions: %d Shooting (want 2), %d Melee (want 1)", shooters, melee)
	}
	return nil
}
