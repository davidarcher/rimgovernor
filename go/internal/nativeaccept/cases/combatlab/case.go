package combatlab

import (
	"context"
	"fmt"
	"sort"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// liveTicks is how long the liveness check lets the staged fight run:
// 5 game seconds, far enough for walking raiders to close several cells.
const liveTicks = 300

func init() {
	cases.Register(cases.Case{
		Name:        "combatlab/stage",
		Scope:       "Combat lab fixtures (#854): lab-open, lab-choke and lab-ranged each stage on a wiped lab with every pawn at its cell, armed as specified, hostiles under an assault lord, a second staging reads back the same digest, and lab-open's raiders close on the colonists within 300 ticks.",
		Start:       cases.Lab{Colonists: 5},
		RequiredOps: []string{na.LabStartTool, StageTool},
		QuietWorld:  true,
		Budget:      cases.LabBudget,
		Run: func(ctx context.Context, s cases.Session) error {
			h := s.Harness()
			report := map[string]any{}
			for _, name := range Names {
				first, err := Stage(ctx, h, name)
				if err != nil {
					return err
				}
				second, err := Stage(ctx, h, name)
				if err != nil {
					return err
				}
				if first.Digest == "" || first.Digest != second.Digest {
					return fmt.Errorf("%s: stagings differ, digest %q then %q: %s", name, first.Digest, second.Digest, rowDiff(first.Rows, second.Rows))
				}
				row := map[string]any{"digest": first.Digest, "faction": first.Faction, "colonists": len(first.Colonists()), "hostiles": len(first.Hostiles())}
				report[name] = row
				if name == "lab-open" {
					if row["live"], err = closesIn(ctx, s); err != nil {
						return err
					}
				}
			}
			s.Report()["fixtures"] = report
			return nil
		},
	})
}

// closesIn proves a staged fixture is a live fight, not a tableau: under
// their assault lord lab-open's melee raiders close on the colonists.
func closesIn(ctx context.Context, s cases.Session) (map[string]any, error) {
	h := s.Harness()
	before, _, err := Read(ctx, h)
	if err != nil {
		return nil, err
	}
	after, tick, err := Tick(ctx, h, liveTicks)
	if err != nil {
		return nil, err
	}
	row := map[string]any{"gapBefore": Gap(before), "gapAfter": Gap(after), "tick": tick}
	if Gap(after) < 0 || Gap(after) >= Gap(before) {
		return row, fmt.Errorf("lab-open: raiders did not close in %d ticks (squared gap %d then %d)", liveTicks, Gap(before), Gap(after))
	}
	return row, nil
}

// rowDiff names the digest rows only one of two stagings has.
func rowDiff(a, b []string) string {
	count := map[string]int{}
	for _, r := range a {
		count[r]++
	}
	for _, r := range b {
		count[r]--
	}
	var only []string
	for r, n := range count {
		if n > 0 {
			only = append(only, "first only "+r)
		} else if n < 0 {
			only = append(only, "second only "+r)
		}
	}
	sort.Strings(only)
	return strings.Join(only, "; ")
}
