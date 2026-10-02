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
	firstAttacksTopScored(t, first, orders)
}

// firstAttacksTopScored is #863: the first attack orders name the
// top-scored target, which the geometry ask lists first, and every gunner
// takes it.
func firstAttacksTopScored(t *testing.T, first combatReplayStop, orders []policy.CombatOrder) {
	t.Helper()
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

// lab-mech-line (#1184): lab-ranged's riflemen and held line against two
// lancers. The squad takes the mechs on (lab-mech is the shelter case):
// the hold forms at once and its first attacks name the top-scored
// target (#863).
func TestCombatReplayLabMechLine(t *testing.T) {
	t.Parallel()
	stops := checkCombat(t, "testdata/combat/lab-mech-line.json.gz",
		formsTactic(firstStop, policy.TacticHold),
		ordersOwnedDrafts(),
		changesOnly(),
		attacksOnPresentHostiles(),
		noAimInterrupt(),
	)
	firstAttacksTopScored(t, stops[0], admission(t, stops[0]))
}

// lab-mech (#1118, #1146, #1152): one rifleman against a scyther stunned
// at staging (recorded on 71e1045c1). No squad is viable against the
// mech, so the fight shelters from the first stop and never attacks: the
// top-scored-target rule (#863) is checked on lab-mech-line, and every
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

// lab-ranged-shield (#866, #1153): lab-ranged plus a fifth colonist, a
// longsword fighter in a charged shield belt. The belt makes it the
// fight's tank: its formation cell lies between the gunners and the
// approach, within two cells ahead of a gunner (the sandbags fill the
// front cells) and nearer every raider than that gunner.
func TestCombatReplayLabRangedShield(t *testing.T) {
	t.Parallel()
	stops := checkCombat(t, "testdata/combat/lab-ranged-shield.json.gz",
		formsTactic(firstStop, policy.TacticHold),
		ordersOwnedDrafts(),
		changesOnly(),
		combatAssertion{name: "no attack through a colonist", check: func(s combatReplayStop) error {
			return noColonistInLine(s, s.Orders)
		}},
	)
	first := stops[0]
	var shielded []domain.PawnID
	for _, p := range first.View.Pawns {
		if c, ok := p.Shield.Value(); ok && c > 0 {
			shielded = append(shielded, p.ID)
		}
	}
	if len(shielded) != 1 {
		t.Fatalf("shielded pawns %v, want the one belted brawler", shielded)
	}
	cells := map[domain.PawnID]domain.Cell{}
	for _, p := range first.View.Pawns {
		if c, ok := p.Cell.Value(); ok {
			cells[p.ID] = c
		}
	}
	var hostiles []domain.Cell
	for _, h := range first.Ask.Hostiles {
		if c, ok := cells[h]; ok {
			hostiles = append(hostiles, c)
		}
	}
	if len(hostiles) == 0 {
		t.Fatal("no raider cell at the first stop")
	}
	var tank *domain.Cell
	var gunners []domain.Cell
	for _, r := range first.Memory.Roles {
		switch {
		case r.Pawn == shielded[0]:
			if r.Duty != policy.DutyTank || r.Ranged {
				t.Errorf("shielded pawn's role %+v, want a tank", r)
			}
			tank = r.Cell
		case r.Ranged && r.Cell != nil:
			gunners = append(gunners, *r.Cell)
		}
	}
	if tank == nil {
		t.Fatalf("shielded pawn has no formation cell: roles %+v", first.Memory.Roles)
	}
	dist2 := func(a, b domain.Cell) int32 { return (a.X-b.X)*(a.X-b.X) + (a.Z-b.Z)*(a.Z-b.Z) }
	between := slices.ContainsFunc(gunners, func(g domain.Cell) bool {
		if max(abs32(g.X-tank.X), abs32(g.Z-tank.Z)) > 2 {
			return false
		}
		return !slices.ContainsFunc(hostiles, func(h domain.Cell) bool { return dist2(*tank, h) >= dist2(g, h) })
	})
	if !between {
		t.Errorf("tank cell %v is not in front of a gunner %v toward the raiders %v", *tank, gunners, hostiles)
	}
}
