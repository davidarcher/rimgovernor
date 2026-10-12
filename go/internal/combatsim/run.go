package combatsim

import (
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// defaultBackstopTicks is the decision cadence when a Fight sets none.
const defaultBackstopTicks = 300

// Autonomy returns the attack orders the fire-at-will holders give
// themselves: Wait_Combat's auto-attack, which starts a shot at a hostile in
// reach of the pawn's cell whenever it has no target and is not on hold fire.
// The target is the nearest in range with a line of fire, ties to the earlier
// scenario pawn (approximations.md A21).
func (a *Adapter) Autonomy() []Order {
	var out []Order
	for i := range a.sim.pawns {
		if o, ok := a.autoAttack(i); ok {
			out = append(out, o)
		}
	}
	return out
}

// autoAttack is Autonomy for pawn i.
func (a *Adapter) autoAttack(i int) (Order, bool) {
	s := a.sim
	p := &s.pawns[i]
	if !a.orderable[p.id] || a.holdFire[p.id] || !p.armed || p.dead || p.downed || p.moving || p.target >= 0 || p.stance != stanceIdle {
		return Order{}, false
	}
	best, bestD := -1, 0
	for j := range s.pawns {
		t := &s.pawns[j]
		if !t.has || t.side == p.side || t.dead || t.downed || !s.canHitFrom(i, j) {
			continue
		}
		if d := distSq(p.pos, t.pos); best < 0 || d < bestD {
			best, bestD = j, d
		}
	}
	if best < 0 {
		return Order{}, false
	}
	return Order{Pawn: p.id, Kind: OrderAttack, Target: s.pawns[best].id, Auto: true}, true
}

// arrived is the pather tick's end of a walk: the job ends and Wait_Combat
// starts on the same tick, so its auto-attack warmup (started before the
// stance tick) counts down that tick too (A36).
func (a *Adapter) arrived(i int) {
	if o, ok := a.autoAttack(i); ok {
		_ = a.sim.startAttack(i, o)
	}
}

// Remember folds the results of a stop's orders into the fight's memory the
// way the planner does (combatStopRecord): a refused attack that cannot hit
// is remembered from the shooter's cell, an unreachable move marks its cell,
// any other refusal makes the policy forget the pawn's order.
func (a *Adapter) Remember(m policy.CombatMemory, results []OrderResult) policy.CombatMemory {
	for _, r := range results {
		o := r.Order
		if r.Applied || o.Pawn == "" {
			continue
		}
		i := a.sim.pawnIndex(o.Pawn)
		switch {
		case i >= 0 && r.Refusal == RefusedCannotHit:
			m = m.RefuseHit(o, a.sim.pawns[i].pos)
		case o.Kind == policy.OrderMove && r.Refusal == RefusedUnreachable:
			m = m.RefuseCell(o.Cell).Forget(o.Pawn)
		default:
			m = m.Forget(o.Pawn)
		}
	}
	return m
}

// Decide runs DecideCombat for the stop with the stop's one geometry round
// trip, as RoundsDefensePlanner does: a geometry ask is answered from the
// sim's world and the policy decides again from the memory it was given.
func (a *Adapter) Decide(stop policy.StopEvent, memory policy.CombatMemory) ([]policy.CombatOrder, policy.CombatMemory) {
	view := a.View()
	orders, ask, next := policy.DecideCombat(view, policy.GeometryReply{}, stop, memory)
	if ask != nil {
		orders, _, next = policy.DecideCombat(view, a.Geometry(ask), stop, memory)
	}
	return orders, next
}

// FightResult is one finished RunFight.
type FightResult struct {
	// Metrics scores the fight with the shared combatmetrics definition.
	Metrics combatmetrics.Metrics
	// Resolved is true when one side was all down or dead; Winner names the
	// side left standing.
	Resolved bool
	Winner   string
	// Ticks is the sim tick the fight ended on.
	Ticks int
	// Left counts the raiders that left the map (fled).
	Left int
	// Decisions counts DecideCombat stops (the first decision and the
	// backstops included); Orders and Refused count the policy's orders and
	// the sim's refusals of them.
	Decisions, Orders, Refused int
	// Refusals counts refused orders by reason.
	Refusals map[string]int
	// Memory is the policy's memory at the end.
	Memory policy.CombatMemory
}

// RunFight plays f from the seed to resolution or f.MaxTicks, asking the
// unchanged policy.DecideCombat for orders at the first tick, at every stop
// the adapter raises and every backstop, and stepping the sim between. The
// tuning rides in the policy's memory as a search trial's does. A Fight the
// sim cannot represent fails with ErrUnsupported before any step.
func RunFight(f Fight, seed uint64, tuning policy.CombatTuning) (FightResult, error) {
	if err := tuning.Validate(); err != nil {
		return FightResult{}, err
	}
	if f.MaxTicks <= 0 {
		return FightResult{}, errors.New("combatsim: Fight.MaxTicks must be positive")
	}
	backstop := f.BackstopTicks
	if backstop <= 0 {
		backstop = defaultBackstopTicks
	}
	var sim Sim
	if _, err := sim.Reset(f.Scenario, seed); err != nil {
		return FightResult{}, err
	}
	a, err := NewAdapter(f, &sim)
	if err != nil {
		return FightResult{}, err
	}
	var ai *EnemyAI
	if f.EnemyAI != nil {
		if ai, err = NewEnemyAI(&sim, *f.EnemyAI); err != nil {
			return FightResult{}, err
		}
	}
	a.scanRange() // the first scan only records who is already in range
	res := FightResult{Refusals: map[string]int{}}
	memory := policy.CombatMemory{Tuning: &tuning}
	stop, due, last := policy.StopEvent{}, true, 0
	var carried []Order
	for {
		if due {
			var orders []policy.CombatOrder
			orders, memory = a.Decide(stop, memory)
			results, simOrders := a.Apply(orders)
			memory = a.Remember(memory, results)
			carried = append(carried, simOrders...)
			res.Decisions++
			res.Orders += len(orders)
			for _, r := range results {
				if !r.Applied {
					res.Refused++
					res.Refusals[r.Refusal]++
				}
			}
			last, due = sim.tick, false
		}
		// The policy's orders apply first: the holders' Autonomy reads the
		// pawns as the orders left them, as Wait_Combat does after a new job
		// replaces it, so a pawn just told to walk does not also open fire.
		err := sim.Apply(carried)
		carried = carried[:0]
		if err != nil {
			return res, fmt.Errorf("combatsim: step at tick %d: %w", sim.tick, err)
		}
		orders := a.Autonomy()
		if ai != nil {
			orders = append(orders, ai.Orders()...)
		}
		if f.Enemy != nil {
			orders = append(orders, f.Enemy(sim.snapshot())...)
		}
		_, events, err := sim.Step(orders)
		if err != nil {
			return res, fmt.Errorf("combatsim: step at tick %d: %w", sim.tick, err)
		}
		if f.Trace != nil {
			f.Trace(events)
		}
		if ai != nil {
			ai.Observe()
		}
		res.Winner, res.Resolved = sim.Resolved()
		if res.Resolved || sim.tick >= f.MaxTicks {
			break
		}
		if stop, due = a.Stop(events); !due && sim.tick-last >= backstop {
			stop, due = policy.StopEvent{}, true
		}
	}
	res.Ticks = sim.tick
	for i := range sim.pawns {
		if sim.pawns[i].left {
			res.Left++
		}
	}
	res.Memory = memory
	res.Metrics = sim.Metrics(f.Name)
	return res, nil
}
