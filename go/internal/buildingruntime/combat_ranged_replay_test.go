package buildingruntime

import (
	"fmt"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// admission is the first stop's orders as the admission batch sends them,
// every role pawn drafted (the lab-pods pattern): the recording's first
// stop precedes the draft, so it orders nothing itself.
func admission(t *testing.T, first combatReplayStop) []policy.CombatOrder {
	t.Helper()
	var pawns []domain.PawnID
	for _, r := range first.Memory.Roles {
		pawns = append(pawns, r.Pawn)
	}
	view := first.View
	view.Orderable = pawns
	orders, ask, _ := policy.DecideCombat(view, first.Reply, policy.StopEvent{}, first.Memory)
	if ask != nil {
		t.Fatalf("admission asks again: %+v", ask)
	}
	return orders
}

// noColonistInLine (#861): no attack order goes out from a cell whose
// answered line to its target crosses a colonist.
func noColonistInLine(s combatReplayStop, orders []policy.CombatOrder) error {
	cells := map[domain.PawnID]domain.Cell{}
	for _, p := range s.View.Pawns {
		if c, ok := p.Cell.Value(); ok {
			cells[p.ID] = c
		}
	}
	for _, o := range orders {
		for _, l := range s.Reply.Lines {
			if o.Kind == policy.OrderAttack && l.Hostile == o.Target && l.Cell == cells[o.Pawn] && l.ColonistInPath {
				return fmt.Errorf("%s ordered to attack %s through a colonist", o.Pawn, o.Target)
			}
		}
	}
	return nil
}

// lab-ranged (#854, #1152): four riflemen behind a seven-cell sandbag
// line, four rifle raiders 25 cells north, served with the fixture's
// layout (recorded on 71e1045c1). The hold forms at once on the layout's
// firing cells, the game's cover scores ranking them; the raiders flee.
func TestCombatReplayLabRanged(t *testing.T) {
	t.Parallel()
	stops := checkCombat(t, "testdata/combat/lab-ranged.json.gz",
		formsTactic(firstStop, policy.TacticHold),
		ordersOwnedDrafts(),
		changesOnly(),
		attacksOnPresentHostiles(),
		noAimInterrupt(),
		combatAssertion{name: "no attack through a colonist", check: func(s combatReplayStop) error {
			return noColonistInLine(s, s.Orders)
		}},
		combatAssertion{name: "a serious injury retreats", at: withOrders, check: func(s combatReplayStop) error {
			if !slices.ContainsFunc(s.Orders, func(o policy.CombatOrder) bool { return o.Reason == policy.ReasonRetreat }) {
				return fmt.Errorf("orders %+v", s.Orders)
			}
			return nil
		}},
	)
	first := stops[0]
	orders := admission(t, first)
	// #861 on the admission batch too.
	if err := noColonistInLine(first, orders); err != nil {
		t.Error(err)
	}
	// #862: every formation cell is top-scored: no scored cell the
	// formation left free, spaced off every formation cell, outscores one.
	score := map[domain.Cell]float64{}
	for _, s := range first.Reply.Scored {
		score[s.Cell] = policy.CoverScore(s)
	}
	var cells []domain.Cell
	for _, r := range first.Memory.Roles {
		if r.Cell != nil {
			cells = append(cells, *r.Cell)
		}
	}
	if len(cells) != 4 {
		t.Fatalf("formation cells %v, want the four riflemen's", cells)
	}
	moves := 0
	for _, o := range orders {
		if o.Kind == policy.OrderMove && o.Reason == policy.ReasonFormation {
			moves++
			if !slices.Contains(cells, o.Cell) {
				t.Errorf("formation move %+v off the formation's cells %v", o, cells)
			}
		}
	}
	if moves == 0 {
		t.Errorf("admission orders %+v send no formation move", orders)
	}
	spaced := func(c domain.Cell) bool {
		return !slices.ContainsFunc(cells, func(f domain.Cell) bool {
			return max(abs32(f.X-c.X), abs32(f.Z-c.Z)) <= 1
		})
	}
	for c, sc := range score {
		if slices.Contains(cells, c) || !spaced(c) {
			continue
		}
		for _, f := range cells {
			if sc > score[f] {
				t.Errorf("free cell %v scores %.3f over formation cell %v (%.3f)", c, sc, f, score[f])
			}
		}
	}
	// #863: the first attack orders name the top-scored target, which the
	// geometry ask lists first.
	if len(first.Ask.Hostiles) == 0 {
		t.Fatalf("first stop asked no hostiles")
	}
	top := first.Ask.Hostiles[0]
	attacks := 0
	for _, o := range orders {
		if o.Kind == policy.OrderAttack {
			attacks++
			if o.Target != top {
				t.Errorf("first attack %+v, want the top-scored %s", o, top)
			}
		}
	}
	if attacks == 0 {
		t.Errorf("admission orders %+v attack nobody", orders)
	}
	for _, r := range first.Memory.Roles {
		if r.Ranged && r.Target != top {
			t.Errorf("gunner %s on %s, want the top-scored %s", r.Pawn, r.Target, top)
		}
	}
}

// lab-mech (#1118, #1146, #1152): one rifleman against a scyther stunned
// at staging (recorded on 71e1045c1). No squad is viable against the
// mech, so the fight shelters from the first stop and never attacks: the
// top-scored-target rule (#863) has no attack to check here, and every
// stop's orders stay owned changes.
func TestCombatReplayLabMech(t *testing.T) {
	t.Parallel()
	stops := checkCombat(t, "testdata/combat/lab-mech.json.gz",
		formsTactic(firstStop, policy.TacticShelter),
		ordersOwnedDrafts(),
		changesOnly(),
		combatAssertion{name: "no attack on the scyther", check: func(s combatReplayStop) error {
			for _, o := range s.Orders {
				if o.Kind == policy.OrderAttack {
					return fmt.Errorf("attack %+v", o)
				}
			}
			return nil
		}},
	)
	for _, o := range admission(t, stops[0]) {
		if o.Kind == policy.OrderAttack {
			t.Errorf("admission attacks: %+v", o)
		}
	}
}
