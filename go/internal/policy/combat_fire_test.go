package policy

import (
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A ranged attack along a blocked line (a wall, #912) retargets to a
// hostile with a clear line, or is dropped; an attack native refused
// cannot_hit is not re-sent from the same cell, and is forgotten once the
// shooter moves.
func TestClearLinesBlockedAndRefusedHits(t *testing.T) {
	from := domain.Cell{X: 5, Z: 5}
	view := CombatView{
		Pawns:   []CombatPawnState{{ID: "s1", Cell: domain.Known(from)}, {ID: "h1"}, {ID: "h2"}},
		Threats: []SquadThreatFacts{{ID: "h1"}, {ID: "h2"}},
	}
	roles := []CombatRole{{Pawn: "s1", Target: "h1", Ranged: true}}
	attack := CombatOrder{Pawn: "s1", Kind: OrderAttack, Target: "h1", Reason: ReasonFormation}
	walled := GeometryReply{Lines: []SightLine{{Cell: from, Hostile: "h1"}, {Cell: from, Hostile: "h2"}}}
	if out, _, _ := clearLines(view, []CombatOrder{attack}, walled, roles, CombatMemory{}); len(out) != 0 {
		t.Fatalf("attack through a wall sent: %+v", out)
	}
	open := GeometryReply{Lines: []SightLine{{Cell: from, Hostile: "h1"}, {Cell: from, Hostile: "h2", LineOfFire: true}}}
	out, got, _ := clearLines(view, []CombatOrder{attack}, open, roles, CombatMemory{})
	if len(out) != 1 || out[0].Target != "h2" || got[0].Target != "h2" {
		t.Fatalf("no retarget to the open line: %+v %+v", out, got)
	}
	// Refused cannot_hit from this cell: not re-sent, even with no lines.
	m := CombatMemory{}.RefuseHit(attack, from)
	if out, _, _ := clearLines(view, []CombatOrder{attack}, GeometryReply{}, roles, m); len(out) != 0 {
		t.Fatalf("refused attack re-sent: %+v", out)
	}
	if kept := keepHitRefusals(view, m.CannotHit); len(kept) != 1 {
		t.Fatalf("refusal dropped in place: %+v", kept)
	}
	view.Pawns[0].Cell = domain.Known(domain.Cell{X: 6, Z: 5})
	if kept := keepHitRefusals(view, m.CannotHit); len(kept) != 0 {
		t.Fatalf("refusal kept after the shooter moved: %+v", kept)
	}
}

// Six packed firing cells for three riflemen: each takes a cell a tile
// apart from the others.
func TestDecideCombatSpacesFiringCells(t *testing.T) {
	view := holdView()
	var firing []domain.Cell
	for x := int32(6); x <= 11; x++ {
		firing = append(firing, domain.Cell{X: x, Z: 23})
	}
	view.Layout = domain.Known(CombatLayout{Firing: firing, Toward: domain.North})
	_, memory := decideStop(t, view, StopEvent{}, CombatMemory{})
	if len(memory.Roles) != 3 {
		t.Fatalf("%+v", memory.Roles)
	}
	for i, a := range memory.Roles {
		for _, b := range memory.Roles[i+1:] {
			if adjacent8(*a.Cell, *b.Cell) {
				t.Fatalf("%s at %v next to %s at %v", a.Pawn, *a.Cell, b.Pawn, *b.Cell)
			}
		}
	}
	// With no room to space, the rest pack rather than stand idle.
	if got := spaceCells([]domain.Cell{{X: 1}, {X: 2}, {X: 3}}, 1); !reflect.DeepEqual(got, []domain.Cell{{X: 1}, {X: 3}, {X: 2}}) {
		t.Fatalf("%v", got)
	}
}

// An attack whose line crosses a colonist retargets to the top-scored
// hostile with a clear line, or is dropped when there is none; the
// formation's geometry ask names the shooters' cells, and a reaction stop
// with new attacks asks for their lines alone.
func TestDecideCombatNoAttackThroughColonist(t *testing.T) {
	view := threatView()
	_, ask, _ := DecideCombat(view, GeometryReply{}, StopEvent{}, CombatMemory{})
	if ask == nil || ask.Propose != RoleCoverBehindLine || !reflect.DeepEqual(ask.Cells[len(ask.Cells)-3:], shooterCells(view.sorted())) {
		t.Fatalf("formation ask %+v", ask)
	}
	a, b := domain.Cell{X: 9, Z: 23}, domain.Cell{X: 8, Z: 23}
	lines := []SightLine{
		{Cell: a, Hostile: "h5", LineOfFire: true, ColonistInPath: true},
		{Cell: a, Hostile: "h4", LineOfFire: true},
		{Cell: b, Hostile: "h5", LineOfFire: true, ColonistInPath: true},
		{Cell: b, Hostile: "h4", LineOfFire: true, ColonistInPath: true},
		{Cell: b, Hostile: "h3", LineOfFire: false},
	}
	orders, again, memory := DecideCombat(view, GeometryReply{Answered: true, Lines: lines}, StopEvent{}, CombatMemory{})
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderAttack, Target: "h4", Reason: ReasonFormation},
		{Pawn: "c", Kind: OrderAttack, Target: "h5", Reason: ReasonFormation},
	}
	if again != nil || !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	if memory.Roles[0].Target != "h4" {
		t.Fatalf("retarget not kept: %+v", memory.Roles)
	}
	// Next stop, b is still to order: its attack asks for lines only.
	view.Tick = 160
	for i := range view.Pawns[:3] {
		view.Pawns[i].Target = memory.Roles[i].Target
	}
	view.Pawns[1].Target = ""
	_, ask, _ = DecideCombat(view, GeometryReply{}, StopEvent{}, memory)
	if ask == nil || ask.Propose != "" || len(ask.Cells) != 3 || ask.Hostiles[0] != "h5" {
		t.Fatalf("line ask %+v", ask)
	}
	if orders, _, _ = DecideCombat(view, GeometryReply{Answered: true, Lines: lines}, StopEvent{}, memory); len(orders) != 0 {
		t.Fatalf("an attack through a colonist: %+v", orders)
	}
}

// A squad role whose target left the view (fled, despawned) gets no
// attack: native refuses it as not_found every stop (#904).
func TestDecideCombatNoAttackOnMissingTarget(t *testing.T) {
	view := threatView()
	memory := CombatMemory{Tactic: TacticSquad, Formed: 50, Roles: []CombatRole{
		{Pawn: "a", Target: "gone"},
	}}
	orders, memory := decideStop(t, view, StopEvent{}, memory)
	for _, o := range orders {
		if o.Kind == OrderAttack && o.Target == "gone" {
			t.Fatalf("%+v", orders)
		}
	}
	if memory.Roles[0].Target != "" {
		t.Fatalf("role kept missing target: %+v", memory.Roles)
	}
}

// Lab pods (#967): raiders dropped inside a walled room, the gunner in the
// corridor outside with every line blocked. Its walled lines become
// refused hits, the next stop asks firing cells around it, and the answer
// moves it to the nearest free proposal with a clear line.
func TestClearLinesWalledGunnerRepositions(t *testing.T) {
	from := domain.Cell{X: 5, Z: 5}
	h1, h2 := domain.Cell{X: 12, Z: 5}, domain.Cell{X: 12, Z: 7}
	view := CombatView{
		Pawns: []CombatPawnState{
			{ID: "s1", Cell: domain.Known(from)},
			{ID: "s2", Cell: domain.Known(domain.Cell{X: 6, Z: 6})},
			{ID: "h1", Cell: domain.Known(h1)}, {ID: "h2", Cell: domain.Known(h2)},
		},
		Threats:   []SquadThreatFacts{{ID: "h1"}, {ID: "h2"}},
		Orderable: []domain.PawnID{"s1", "s2"},
		Defenders: []SquadDefenderFacts{combatRifleman("s1"), combatRifleman("s2")},
	}
	roles := []CombatRole{{Pawn: "s1", Target: "h1", Ranged: true, Cell: &from}}
	attack := []CombatOrder{{Pawn: "s1", Kind: OrderAttack, Target: "h1", Reason: ReasonFormation}}
	lines := func(c domain.Cell, clear ...domain.PawnID) []SightLine {
		return []SightLine{
			{Cell: c, Hostile: "h1", LineOfFire: slices.Contains(clear, "h1")},
			{Cell: c, Hostile: "h2", LineOfFire: slices.Contains(clear, "h2")},
		}
	}
	// Every line walled: no order, both lines remembered.
	out, _, refused := clearLines(view, attack, GeometryReply{Answered: true, Lines: lines(from)}, roles, CombatMemory{})
	if len(out) != 0 || len(refused) != 2 {
		t.Fatalf("walled: %+v %+v", out, refused)
	}
	m := CombatMemory{CannotHit: refused}
	ask := lineAsk(view, attack, m)
	if ask == nil || ask.Propose != RoleFiringCells || ask.From != from || !reflect.DeepEqual(ask.Targets, []domain.Cell{h1, h2}) {
		t.Fatalf("no firing-cells ask: %+v", ask)
	}
	if ask := lineAsk(view, attack, CombatMemory{}); ask == nil || ask.Propose != "" {
		t.Fatalf("firing cells asked with a line untried: %+v", ask)
	}
	door, far, taken, shut := domain.Cell{X: 7, Z: 4}, domain.Cell{X: 9, Z: 9}, domain.Cell{X: 6, Z: 6}, domain.Cell{X: 5, Z: 4}
	for _, tc := range []struct {
		name  string
		reply GeometryReply
		want  []CombatOrder
	}{
		{"nearest clear free cell", GeometryReply{
			Role: RoleFiringCells, Proposals: []domain.Cell{far, shut, taken, door},
			Lines: slices.Concat(lines(from), lines(far, "h1"), lines(shut), lines(taken, "h1"), lines(door, "h2")),
		}, []CombatOrder{{Pawn: "s1", Kind: OrderMove, Cell: door, Reason: ReasonFormation}}},
		{"no clear proposal", GeometryReply{
			Role: RoleFiringCells, Proposals: []domain.Cell{shut},
			Lines: slices.Concat(lines(from), lines(shut)),
		}, nil},
		{"lines only, no proposals", GeometryReply{Lines: lines(from)}, nil},
	} {
		tc.reply.Answered = true
		out, got, _ := clearLines(view, attack, tc.reply, roles, m)
		if !reflect.DeepEqual(out, tc.want) {
			t.Fatalf("%s: %+v want %+v", tc.name, out, tc.want)
		}
		if tc.want != nil && (*got[0].Cell != door || got[0].Target != "h2") {
			t.Fatalf("%s: role not moved: %+v", tc.name, got[0])
		}
	}
}

// loneThreat keeps h1 as the only threat of a threatView.
func loneThreat(view CombatView) CombatView {
	view.Threats, view.Positional = view.Threats[:1], view.Positional[:1]
	return view
}

// A gunner whose target is in melee with our blocker gets stop and
// hold-fire; it holds while the melee lasts and gets fire-at-will back
// when it ends, then its attack. h1 is the only hostile, so no other is
// in range to shoot instead (#978).
func TestDecideCombatHoldsFireOnBlockerMelee(t *testing.T) {
	view := loneThreat(threatView())
	cell := func(x int32) *domain.Cell { return &domain.Cell{X: x, Z: 23} }
	memory := CombatMemory{Tactic: TacticHold, Formed: 50, Roles: []CombatRole{
		{Pawn: "a", Cell: cell(9), Target: "h1", Ranged: true},
		{Pawn: "b", Cell: cell(8), Target: "h1", Duty: DutyBlocker},
	}}
	// h1 and b fight; a is shooting h1.
	view.Pawns[0].Target, view.Pawns[0].Stance, view.Pawns[0].FireMode = "h1", StanceCooldown, FireAtWill
	view.Pawns[1].Target, view.Pawns[1].Stance = "h1", StanceMelee
	view.Pawns[2].Target, view.Pawns[2].FireMode = "h5", FireAtWill
	view.Pawns[3].Target, view.Pawns[3].Stance = "b", StanceMelee
	orders, memory := decideStop(t, view, StopEvent{Kind: "melee_contact"}, memory)
	want := []CombatOrder{
		{Pawn: "a", Kind: OrderStop, Reason: ReasonHoldFire},
		{Pawn: "a", Kind: OrderFireMode, FireMode: HoldFire, Reason: ReasonHoldFire},
	}
	if !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	// Held while the melee lasts.
	view.Tick = 160
	view.Pawns[0].Target, view.Pawns[0].Stance, view.Pawns[0].FireMode = "", StanceIdle, HoldFire
	if orders, memory = decideStop(t, view, StopEvent{}, memory); len(orders) != 0 {
		t.Fatalf("%+v", orders)
	}
	// Melee over: fire at will again, and the attack at the next stop.
	view.Tick = 220
	view.Pawns[1].Stance, view.Pawns[3].Stance = StanceIdle, StanceIdle
	orders, memory = decideStop(t, view, StopEvent{}, memory)
	if want := []CombatOrder{{Pawn: "a", Kind: OrderFireMode, FireMode: FireAtWill, Reason: ReasonHoldFire}}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
	view.Tick = 280
	view.Pawns[0].FireMode = FireAtWill
	orders, _ = decideStop(t, view, StopEvent{}, memory)
	if want := []CombatOrder{{Pawn: "a", Kind: OrderAttack, Target: "h1", Reason: ReasonFormation}}; !reflect.DeepEqual(orders, want) {
		t.Fatalf("%+v", orders)
	}
}

// A melee raider reports cooldown between swings: hold fire stays on while
// it stands next to our blocker, and a gunner mid-aim at another hostile
// keeps its fire mode (#903).
func TestDecideCombatHoldFireSteadyBetweenSwings(t *testing.T) {
	view := loneThreat(threatView())
	cell := func(x int32) *domain.Cell { return &domain.Cell{X: x, Z: 23} }
	memory := CombatMemory{Tactic: TacticHold, Formed: 50, Roles: []CombatRole{
		{Pawn: "a", Cell: cell(9), Target: "h1", Ranged: true},
		{Pawn: "b", Cell: cell(8), Target: "h1", Duty: DutyBlocker},
	}}
	view.Pawns[0].Stance, view.Pawns[0].FireMode = StanceIdle, HoldFire
	view.Pawns[1].Target, view.Pawns[1].Stance = "h1", StanceMelee
	view.Pawns[2].Target, view.Pawns[2].FireMode = "h5", FireAtWill
	view.Pawns[3].Target, view.Pawns[3].Stance, view.Pawns[3].Cell = "b", StanceCooldown, domain.Known(domain.Cell{X: 8, Z: 22})
	if orders, _ := decideStop(t, view, StopEvent{}, memory); len(orders) != 0 {
		t.Fatalf("held gunner flipped between swings: %+v", orders)
	}
	// a aiming at h5 while h1 fights b: no hold fire mid-aim.
	view.Pawns[1].Stance = StanceMelee
	view.Pawns[0].Target, view.Pawns[0].Stance, view.Pawns[0].FireMode = "h5", StanceWarmup, FireAtWill
	if orders, _ := decideStop(t, view, StopEvent{}, memory); len(orders) != 0 {
		t.Fatalf("gunner flipped mid-aim: %+v", orders)
	}
}
