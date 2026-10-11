package combatsim

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The hostile side's decisions for ranged raiders: a port of the think-tree
// nodes and the raid lord a LordJob_AssaultColony raider runs under. Sources
// (all checked against AssemblyCSharpSHA256, see rules_version.go):
//   - Verse.AI.AttackTargetFinder: BestAttackTarget, GetAvailableShootingTargetsByScore,
//     GetShootingTargetScore, FriendlyFireConeTargetScoreOffset, CanSee.
//   - RimWorld.JobGiver_AIFightEnemy: TryGiveJob, UpdateEnemyTarget,
//     ShouldLoseTarget, FindAttackTarget; RimWorld.JobGiver_AIFightEnemies.TryFindShootingPosition;
//     RimWorld.JobGiver_AIGotoNearestHostile.TryGiveJob.
//   - Verse.AI.CastPositionFinder: TryFindCastPosition, EvaluateCell, CastPositionPreference.
//   - Verse.AI.JobDriver_Wait.CheckForAutoAttack (the Wait_Combat auto-attack).
//   - Verse.AI.Pawn_JobTracker (job expiry and the distance interval curve).
//   - RimWorld.LordJob_AssaultColony.CreateGraph, Trigger_FractionColonyDamageTaken,
//     Trigger_TicksPassed (the give-up and satisfied transitions).
//   - The AssaultColony DutyDef's JobGiver_AIFightEnemies radii (65 and 72).
const (
	// enemyAcquireRadius and enemyKeepRadius are the DutyDef's
	// targetAcquireRadius and targetKeepRadius.
	enemyAcquireRadius = 65.0
	enemyKeepRadius    = 72.0
	// enemyEngageTicks is TicksSinceEngageToLoseTarget.
	enemyEngageTicks = 400
	// ExpiryInterval_ShooterSucceeded, in ticks (inclusive).
	shooterExpiryMin, shooterExpiryMax = 450, 550
	// autoAttackInterval is JobDriver_Wait's TargetSearchInterval.
	autoAttackInterval = 4
	// recentTargetTicks is the window of the last-attacked-target bonus.
	recentTargetTicks = 300
	// The damage trigger's fraction range and minimum damage.
	fleeFractionMin, fleeFractionMax = 0.25, 0.35
	fleeMinDamage                    = 900.0
)

// EnemyConfig is what the raid lord needs that the scenario does not carry.
// The zero value is a raid that never gives up and never flees.
type EnemyConfig struct {
	// CanTimeoutOrFlee is LordJob_AssaultColony.canTimeoutOrFlee: the raid
	// gives up after AssaultTimeBeforeGiveUp ticks or once the colony has
	// taken enough damage, and every raider walks to Exit and leaves. The
	// game also needs the map exitable (TriggerFilter_MapExitable); Exit is
	// that exit.
	CanTimeoutOrFlee bool
	// Exit is where fleeing raiders leave the map; required with CanTimeoutOrFlee.
	Exit domain.Fact[domain.Cell]
	// ColonyHealthTotal is Lord.initialColonyHealthTotal, the colony's health
	// when the raid arrived; required with CanTimeoutOrFlee. The damage that
	// trips the trigger is the colonists' post-armor damage in the sim's units.
	ColonyHealthTotal domain.Fact[float64]
	// GiveUpMinTicks and GiveUpMaxTicks are AssaultTimeBeforeGiveUp, the
	// inclusive range the give-up time is drawn from; required with
	// CanTimeoutOrFlee. The caller reads them from the game data.
	GiveUpMinTicks, GiveUpMaxTicks int
}

type jobKind uint8

const (
	jobNone        jobKind = iota
	jobWait                // Wait_Combat
	jobGoto                // Goto a shooting position
	jobGotoNearest         // Goto the nearest hostile (JobGiver_AIGotoNearestHostile)
)

// enemyPawn is one raider's think state: Pawn_JobTracker's current job and
// Pawn_MindState's enemyTarget and lastEngageTargetTick.
type enemyPawn struct {
	job        jobKind
	start, end int         // job start tick; expiry tick (not for jobGotoNearest)
	goal       domain.Cell // jobGoto's destination
	near       int         // jobGotoNearest's target pawn
	enemy      int         // mindState.enemyTarget, -1 none
	lastEngage int
	wasBusy    bool
	nextAuto   int
}

// EnemyAI drives the hostile ranged pawns of a Sim. Orders is called once
// before each Step. All its choices draw from the sim's enemy stream and
// nothing else, so changing it never shifts the physical-outcome stream.
type EnemyAI struct {
	sim *Sim
	cfg EnemyConfig
	p   []enemyPawn

	exit     domain.Cell
	health   float64
	giveUpAt int
	fraction float64
	fleeing  bool

	reach, seen []bool
	queue       []int32
	weights     []float64
	cand        []int
}

// NewEnemyAI binds an AI to a Sim that has been Reset, and draws the raid
// lord's random thresholds from its enemy stream. It returns ErrUnsupported
// for a raid that can flee without an exit or a colony health.
func NewEnemyAI(sim *Sim, cfg EnemyConfig) (*EnemyAI, error) {
	e := &EnemyAI{sim: sim, cfg: cfg, p: make([]enemyPawn, len(sim.pawns))}
	for i := range e.p {
		e.p[i] = enemyPawn{enemy: -1, lastEngage: noTick, near: -1}
	}
	if !cfg.CanTimeoutOrFlee {
		return e, nil
	}
	exit, ok := cfg.Exit.Value()
	if !ok || !sim.g.standable(exit) {
		return nil, unsupported("a raid that can flee needs a standable exit cell")
	}
	health, ok := cfg.ColonyHealthTotal.Value()
	if !ok || !(health > 0) {
		return nil, unsupported("a raid that can flee needs the colony health total")
	}
	if cfg.GiveUpMinTicks <= 0 || cfg.GiveUpMaxTicks < cfg.GiveUpMinTicks {
		return nil, unsupported("a raid that can flee needs its give-up time range")
	}
	e.exit, e.health = exit, health
	// CreateGraph draws the give-up time, then the damage fraction.
	e.giveUpAt = e.rangeIncl(cfg.GiveUpMinTicks, cfg.GiveUpMaxTicks)
	e.fraction = fleeFractionMin + sim.streams.Enemy.Float64()*(fleeFractionMax-fleeFractionMin)
	return e, nil
}

// rangeIncl is IntRange.RandomInRange.
func (e *EnemyAI) rangeIncl(lo, hi int) int { return lo + e.sim.streams.Enemy.IntN(hi-lo+1) }

// Fleeing reports whether the raid lord has moved to the exit toil.
func (e *EnemyAI) Fleeing() bool { return e.fleeing }

// Orders returns the hostile pawns' orders for the coming Step.
func (e *EnemyAI) Orders() []Order {
	s := e.sim
	if e.cfg.CanTimeoutOrFlee && !e.fleeing && e.shouldFlee() {
		e.fleeing = true
	}
	var out []Order
	for i := range s.pawns {
		p := &s.pawns[i]
		if !p.has || p.side != combatmetrics.Hostile || p.dead || p.downed || !p.armed {
			continue
		}
		if e.fleeing {
			out = e.flee(out, i)
			continue
		}
		ep := &e.p[i]
		if ep.enemy >= 0 && p.lastAttacked == ep.enemy { // Verb: Notify_EngagedTarget on a shot at the enemy target
			ep.lastEngage = max(ep.lastEngage, p.lastAttackTick)
		}
		if e.jobDone(i) {
			out = e.think(out, i)
		}
		busy := p.stance != stanceIdle
		if ep.job == jobWait && p.target < 0 && !busy && (ep.wasBusy || s.tick >= ep.nextAuto) {
			out = e.autoAttack(out, i)
			ep.nextAuto = s.tick + autoAttackInterval
		}
		ep.wasBusy = busy
	}
	return out
}

// shouldFlee is the two transitions into LordToil_ExitMap: Trigger_TicksPassed
// and Trigger_FractionColonyDamageTaken (colony damage above the larger of the
// drawn fraction of the initial colony health and 900, counted from tick 0).
func (e *EnemyAI) shouldFlee() bool {
	if e.sim.tick >= e.giveUpAt {
		return true
	}
	dmg := 0.0
	for i := range e.sim.pawns {
		if p := &e.sim.pawns[i]; p.side == combatmetrics.Colonist && (p.has || p.left) {
			dmg += p.damage
		}
	}
	return dmg > math.Max(e.health*e.fraction, fleeMinDamage)
}

// flee is the exit toil: walk to the exit at a jog and leave the map there.
func (e *EnemyAI) flee(out []Order, i int) []Order {
	p := &e.sim.pawns[i]
	switch {
	case p.pos == e.exit:
		return append(out, Order{Pawn: p.id, Kind: OrderLeave})
	case !p.moving || p.dest != e.exit:
		return append(out, Order{Pawn: p.id, Kind: OrderMove, To: e.exit})
	}
	return out
}

// jobDone reports whether pawn i's job has ended and the think tree runs.
func (e *EnemyAI) jobDone(i int) bool {
	s := e.sim
	p, ep := &s.pawns[i], &e.p[i]
	switch ep.job {
	case jobWait:
		return s.tick >= ep.end
	case jobGoto:
		return !p.moving || s.tick >= ep.end
	case jobGotoNearest:
		t := &s.pawns[ep.near]
		return !p.moving || !t.has || t.dead || t.downed || s.tick >= ep.start+distanceExpiry(manhattan(p.pos, t.pos))
	}
	return true
}

// distanceExpiry is Pawn_JobTracker.DistanceIntervalCurve: a job scaled by the
// distance to its target expires after 30 ticks at 0 cells, 120 at 50 and 600
// at 100 (linear between, held outside).
func distanceExpiry(manhattan int) int {
	d := float64(manhattan)
	switch {
	case d <= 0:
		return 30
	case d <= 50:
		return int(30 + (120-30)*d/50)
	case d <= 100:
		return int(120 + (600-120)*(d-50)/50)
	}
	return 600
}

func manhattan(a, b domain.Cell) int { return abs32(a.X-b.X) + abs32(a.Z-b.Z) }

// think is the AssaultColony duty's think tree for a ranged raider with no
// job: JobGiver_AIFightEnemies, then (the trash-building nodes have nothing to
// attack here) JobGiver_AIGotoNearestHostile.
func (e *EnemyAI) think(out []Order, i int) []Order {
	ep := &e.p[i]
	ep.job = jobNone
	e.updateEnemyTarget(i)
	if ep.enemy >= 0 {
		if o, ok := e.fightJob(i); ok {
			return append(out, o...)
		}
	}
	return e.gotoNearest(out, i)
}

// updateEnemyTarget ports JobGiver_AIFightEnemy.UpdateEnemyTarget: keep the
// target unless ShouldLoseTarget; look for one when there is none; when there
// is one, switch to whatever the finder now returns, and (chaseTarget is
// false) drop the target when the finder returns nothing.
func (e *EnemyAI) updateEnemyTarget(i int) {
	s := e.sim
	ep := &e.p[i]
	cur := ep.enemy
	if cur >= 0 && e.shouldLoseTarget(i) {
		cur = -1
	}
	if cur < 0 {
		if cur = e.findAttackTarget(i); cur >= 0 {
			ep.lastEngage = s.tick // Notify_EngagedTarget
		}
	} else {
		switch next := e.findAttackTarget(i); {
		case next < 0:
			cur = -1
		case next != cur:
			ep.lastEngage = s.tick
			cur = next
		}
	}
	ep.enemy = cur
}

// shouldLoseTarget ports ShouldLoseTarget: the target is gone, was last engaged
// more than 400 ticks ago, cannot be reached, is beyond the keep radius or is
// no longer a threat. A downed pawn is no threat here (see approximations.md).
func (e *EnemyAI) shouldLoseTarget(i int) bool {
	s := e.sim
	p, ep := &s.pawns[i], &e.p[i]
	t := &s.pawns[ep.enemy]
	switch {
	case !t.has || t.dead || t.downed:
		return true
	case s.tick-ep.lastEngage > enemyEngageTicks:
		return true
	case float64(distSq(p.pos, t.pos)) > enemyKeepRadius*enemyKeepRadius:
		return true
	}
	return !e.flood(p.pos)[s.g.idx(t.pos)]
}

// findAttackTarget ports FindAttackTarget: AttackTargetFinder.BestAttackTarget
// with the AssaultColony acquire radius, line of sight to pawns and, when no
// target can be shot from here, reachability.
func (e *EnemyAI) findAttackTarget(i int) int {
	s := e.sim
	p := &s.pawns[i]
	cands := e.valid(i, enemyAcquireRadius, 0, true)
	for _, j := range cands {
		if s.canHitFrom(i, j) {
			return e.pickByScore(i, cands)
		}
	}
	reach := e.flood(p.pos)
	best, bd := -1, 0
	for _, j := range cands { // ClosestThing_Global: nearest, first on ties
		if d := distSq(p.pos, s.pawns[j].pos); reach[s.g.idx(s.pawns[j].pos)] && (best < 0 || d < bd) {
			best, bd = j, d
		}
	}
	return best
}

// valid lists, in scenario order, the pawns BestAttackTarget's validator lets
// through for searcher i: a live standing hostile within radius, farther than
// minDist and (unless canTakeCloser) than the weapon's minimum range, with
// line of sight. A downed pawn is never listed: the sim cannot shoot one.
func (e *EnemyAI) valid(i int, radius, minDist float64, canTakeCloser bool) []int {
	s := e.sim
	p := &s.pawns[i]
	e.cand = e.cand[:0]
	for j := range s.pawns {
		t := &s.pawns[j]
		if j == i || !t.has || t.side == p.side || t.dead || t.downed {
			continue
		}
		d2 := float64(distSq(p.pos, t.pos))
		switch {
		case d2 > radius*radius:
			continue
		case minDist > 0 && d2 < minDist*minDist:
			continue
		case !canTakeCloser && p.weapon.MinRange > 0 && d2 < p.weapon.MinRange*p.weapon.MinRange:
			continue
		case !s.CellCanSeeCell(p.pos, t.pos):
			continue
		}
		e.cand = append(e.cand, j)
	}
	return e.cand
}

// pickByScore ports GetRandomShootingTargetByScore over the pawns that can be
// shot from i's cell, drawing from the enemy stream. It returns -1 when none can.
func (e *EnemyAI) pickByScore(i int, cands []int) int {
	s := e.sim
	scores := make([]float64, len(cands))
	for k, j := range cands {
		scores[k] = math.Inf(-1)
		if s.canHitFrom(i, j) {
			scores[k] = e.score(i, j)
		}
	}
	e.weights = selectionWeights(scores, e.weights[:0])
	total := 0.0
	for _, w := range e.weights {
		total += w
	}
	if total <= 0 {
		return -1
	}
	// TryRandomElementByWeight: walk the list taking weight off a uniform draw.
	r := s.streams.Enemy.Float64() * total
	pick := -1
	for k, w := range e.weights {
		if w <= 0 {
			continue
		}
		pick = cands[k]
		if r -= w; r <= 0 {
			break
		}
	}
	return pick
}

// selectionWeights ports GetAvailableShootingTargetsByScore's weighting. A
// score of -Inf marks a pawn that cannot be shot. When the best score is at
// least 1, every pawn within 30 of it weighs InverseLerp(best-30, best, score);
// below 1 the first best pawn weighs 1 and the rest 0.
func selectionWeights(scores, out []float64) []float64 {
	best, bi := math.Inf(-1), -1
	for k, sc := range scores {
		if !math.IsInf(sc, -1) && (bi < 0 || sc > best) {
			best, bi = sc, k
		}
	}
	for k, sc := range scores {
		w := 0.0
		switch {
		case bi < 0 || math.IsInf(sc, -1):
		case best < 1:
			if k == bi {
				w = 1
			}
		case sc >= best-30:
			w = (sc - (best - 30)) / 30
		}
		out = append(out, w)
	}
	return out
}

// score ports GetShootingTargetScore for pawn i choosing target j. Target
// priority is 1 for pawns; the non-combatant, juvenile, downed and blast
// radius terms are 0 or unreachable for this slice (approximations.md).
func (e *EnemyAI) score(i, j int) float64 {
	s := e.sim
	a, b := &s.pawns[i], &s.pawns[j]
	num := 60.0
	num -= math.Min(math.Sqrt(float64(distSq(b.pos, a.pos))), 40)
	if b.armed && b.stance != stanceIdle && b.cast == i { // TargetCurrentlyAimingAt: Stance_Warmup or Stance_Cooldown
		num += 10
	}
	if a.lastAttacked == j && s.tick-a.lastAttackTick <= recentTargetTicks {
		num += 40
	}
	num -= s.CoverBlockChance(b.pos, a.pos, true) * 10
	if b.armed {
		num += a.weapon.TargetHasRangedAttackScoreOffset
	}
	return num + e.coneOffset(i, j)
}

// coneOffset ports FriendlyFireConeTargetScoreOffset for a humanlike shooter
// with an ordinary projectile: the cells a shot at j could strike (a radius of
// at least 1.5 around the target, each ray cut at the first cell that blocks
// sight) weigh every other pawn there by how likely it is to intercept: 18,
// scaled by distance, +0.6 of it when the pawn is a hostile of the shooter
// and -1 of it when a friend.
func (e *EnemyAI) coneOffset(i, j int) float64 {
	s := e.sim
	a, b := &s.pawns[i], &s.pawns[j]
	radius := math.Max(adjustedForcedMiss(a.weapon.ForcedMissRadius, distSq(b.pos, a.pos)), 1.5)
	if e.seen == nil || len(e.seen) != s.g.w*s.g.h {
		e.seen = make([]bool, s.g.w*s.g.h)
	}
	ox, oz := float64(a.pos.X)+0.5, float64(a.pos.Z)+0.5
	var visited []domain.Cell
	r := int32(math.Ceil(radius))
	for dz := -r; dz <= r; dz++ {
		for dx := -r; dx <= r; dx++ {
			if float64(dx*dx+dz*dz) > radius*radius {
				continue
			}
			dest := domain.Cell{X: b.pos.X + dx, Z: b.pos.Z + dz}
			if !s.g.inBounds(dest) {
				continue
			}
			open := true
			visit := func(c domain.Cell) bool {
				if open = s.g.canBeSeenOver(c); !open {
					return false
				}
				if ci := s.g.idx(c); !e.seen[ci] {
					e.seen[ci] = true
					visited = append(visited, c)
				}
				return true
			}
			pointsOnLine(a.pos, dest, visit)
			if open {
				visit(dest)
			}
		}
	}
	total := 0.0
	for _, c := range visited {
		e.seen[s.g.idx(c)] = false
		f := interceptFactor(ox, oz, c)
		if f <= 0 {
			continue
		}
		for k := range s.pawns {
			t := &s.pawns[k]
			if k == j || !t.has || t.dead || t.pos != c {
				continue
			}
			v := 18.0
			if k == i {
				v = 40
			}
			v *= f
			if t.side != a.side {
				v *= 0.6
			} else {
				v = -v
			}
			total += v
		}
	}
	return total
}

// adjustedForcedMiss ports VerbUtility.CalculateAdjustedForcedMiss for a shot
// whose squared length is d2.
func adjustedForcedMiss(forcedMiss float64, d2 int) float64 {
	switch {
	case d2 < 9:
		return 0
	case d2 < 25:
		return forcedMiss * 0.5
	case d2 < 49:
		return forcedMiss * 0.8
	}
	return forcedMiss
}

// startWait gives the pawn a Wait_Combat of 450-550 ticks. Pawn.pather.StopDead
// halts a pawn that was walking.
func (e *EnemyAI) startWait(i int) []Order {
	s := e.sim
	p, ep := &s.pawns[i], &e.p[i]
	ep.job, ep.start, ep.end = jobWait, s.tick, s.tick+e.rangeIncl(shooterExpiryMin, shooterExpiryMax)
	ep.nextAuto = s.tick
	if p.moving {
		return []Order{{Pawn: p.id, Kind: OrderStop}}
	}
	return nil
}

// fightJob is the rest of JobGiver_AIFightEnemy.TryGiveJob once there is an
// enemy target and a ranged verb: wait where cover or closeness already
// allows a shot, else walk to the position CastPositionFinder picks. ok is
// false when it finds none, so the think tree falls through.
func (e *EnemyAI) fightJob(i int) (orders []Order, ok bool) {
	s := e.sim
	p, ep := &s.pawns[i], &e.p[i]
	t := &s.pawns[ep.enemy]
	cover := s.CoverBlockChance(p.pos, t.pos, p.cover) > 0.01
	reserve := e.canReserve(p.pos, i)
	hit := s.canHitFrom(i, ep.enemy)
	near := distSq(p.pos, t.pos) < 25
	if (cover && reserve && hit) || (near && hit) {
		return e.startWait(i), true
	}
	dest, found := e.castPosition(i, ep.enemy)
	if !found {
		return nil, false
	}
	if dest == p.pos {
		return e.startWait(i), true
	}
	ep.job, ep.start, ep.end, ep.goal = jobGoto, s.tick, s.tick+e.rangeIncl(shooterExpiryMin, shooterExpiryMax), dest
	return []Order{{Pawn: p.id, Kind: OrderMove, To: dest}}, true
}

// gotoNearest ports JobGiver_AIGotoNearestHostile: walk toward the nearest
// reachable standing hostile, nothing when already touching it.
func (e *EnemyAI) gotoNearest(out []Order, i int) []Order {
	s := e.sim
	p, ep := &s.pawns[i], &e.p[i]
	reach := e.flood(p.pos)
	best, bd := -1, 0
	for j := range s.pawns {
		t := &s.pawns[j]
		if !t.has || t.side == p.side || t.dead || t.downed || !reach[s.g.idx(t.pos)] {
			continue
		}
		if d := distSq(p.pos, t.pos); best < 0 || d < bd {
			best, bd = j, d
		}
	}
	if best < 0 || max(abs32(p.pos.X-s.pawns[best].pos.X), abs32(p.pos.Z-s.pawns[best].pos.Z)) <= 1 {
		return out
	}
	ep.job, ep.start, ep.near = jobGotoNearest, s.tick, best
	return append(out, Order{Pawn: p.id, Kind: OrderMove, To: s.pawns[best].pos})
}

// autoAttack is JobDriver_Wait.CheckForAutoAttack for a raider on Wait_Combat:
// start one burst at the scored-random target among those it can hit from
// where it stands.
func (e *EnemyAI) autoAttack(out []Order, i int) []Order {
	s := e.sim
	p := &s.pawns[i]
	cands := e.valid(i, p.weapon.Range, p.weapon.MinRange, false)
	if j := e.pickByScore(i, cands); j >= 0 {
		return append(out, Order{Pawn: p.id, Kind: OrderAttack, Target: s.pawns[j].id, Once: true})
	}
	return out
}

// canReserve stands in for PawnDestinationReservationManager.CanReserve: the
// cell is free of other pawns and is no other raider's destination.
func (e *EnemyAI) canReserve(c domain.Cell, i int) bool {
	s := e.sim
	for k := range s.pawns {
		if k == i {
			continue
		}
		if o := &s.pawns[k]; (o.has || o.downed) && !o.dead && o.pos == c {
			return false
		}
		if ep := &e.p[k]; ep.job == jobGoto && ep.goal == c {
			return false
		}
	}
	return true
}

// flood marks every cell pawn pathing could reach from `from`, with the
// pathfinder's neighbour rules; pawns do not block it (Reachability ignores
// them). The slice is reused by the next call.
func (e *EnemyAI) flood(from domain.Cell) []bool {
	g := &e.sim.g
	n := g.w * g.h
	if len(e.reach) != n {
		e.reach = make([]bool, n)
	}
	clear(e.reach)
	if !g.walkable(from) {
		return e.reach
	}
	q := append(e.queue[:0], int32(g.idx(from)))
	e.reach[q[0]] = true
	for h := 0; h < len(q); h++ {
		cc := g.cell(int(q[h]))
		for k, off := range adjacent {
			nb := add(cc, off)
			if !g.walkable(nb) {
				continue
			}
			if k >= 4 && (!g.walkable(domain.Cell{X: nb.X, Z: cc.Z}) || !g.walkable(domain.Cell{X: cc.X, Z: nb.Z})) {
				continue
			}
			if ni := g.idx(nb); !e.reach[ni] {
				e.reach[ni] = true
				q = append(q, int32(ni))
			}
		}
	}
	e.queue = q
	return e.reach
}

// castPosition ports CastPositionFinder.TryFindCastPosition for pawn i's
// attack verb against pawn j (maxRangeFromTarget = the weapon's range, cover
// wanted when the range exceeds 5): the best cell within range of the target
// from which it can be hit, preferring cover, nearness and about 0.8 of the
// range. Cells on the caster's side of the line through the target
// perpendicular to the caster are tried first, and the other side only when
// none beats 0.33. ok is false when no cell qualifies.
func (e *EnemyAI) castPosition(i, j int) (domain.Cell, bool) {
	s := e.sim
	g := &s.g
	p, t := &s.pawns[i], &s.pawns[j]
	w := &p.weapon
	reach := e.flood(p.pos)
	rng := w.Range
	optimal := (rng * 0.8) * (rng * 0.8)
	span := int32(math.Ceil(rng))
	minX, maxX := max(t.pos.X-span, 0), min(t.pos.X+span, int32(g.w-1))
	minZ, maxZ := max(t.pos.Z-span, 0), min(t.pos.Z+span, int32(g.h-1))
	var best domain.Cell
	found, bestPref := false, 0.001

	eval := func(c domain.Cell) {
		if float64(distSq(c, t.pos)) > rng*rng || !g.walkable(c) || !reach[g.idx(c)] {
			return
		}
		pref := e.castPreference(c, i, j, optimal, rng > 5)
		if pref < bestPref || !s.cellCanHit(w, c, t.pos) || !e.canReserve(c, i) {
			return
		}
		best, found, bestPref = c, true, pref
	}

	eval(p.pos)
	if bestPref >= 1 {
		return p.pos, true
	}
	// CellLine.Between(target, caster), then the perpendicular through the
	// target, in single precision as the game computes it (a caster in the
	// target's row makes the slope infinite and every side test false).
	slope := float32(1e8)
	if t.pos.X != p.pos.X {
		slope = float32(p.pos.Z-t.pos.Z) / float32(p.pos.X-t.pos.X)
	}
	perp := -1 / slope
	icept := float32(t.pos.Z) - float32(t.pos.X)*perp
	above := func(c domain.Cell) bool { return float32(c.Z) > perp*float32(c.X)+icept }
	side := above(p.pos)
	sweep := func(same bool) {
		for z := minZ; z <= maxZ; z++ {
			for x := minX; x <= maxX; x++ {
				if c := (domain.Cell{X: x, Z: z}); (above(c) == side) == same {
					eval(c)
				}
			}
		}
	}
	sweep(true)
	if found && bestPref > 0.33 {
		return best, true
	}
	sweep(false)
	if found {
		return best, true
	}
	return p.pos, false
}

// castPreference ports CastPositionFinder.CastPositionPreference for a
// humanlike caster (aiAvoidCover false) with no avoid grid. The game also
// scales by a static field left over from an earlier request (the distance
// from the caster to the cell, set only when maxRangeFromCaster is used);
// AIFightEnemies never sets it, so that factor is not applied.
func (e *EnemyAI) castPreference(c domain.Cell, i, j int, optimal float64, wantCover bool) float64 {
	s := e.sim
	p, t := &s.pawns[i], &s.pawns[j]
	num := 0.3
	if wantCover {
		num += s.CoverBlockChance(c, t.pos, true) * 0.55
	}
	num *= math.Pow(0.967, math.Sqrt(float64(distSq(p.pos, c))))
	rt := float64(distSq(c, t.pos))
	f := 0.7 + 0.3*(1-math.Abs(rt-optimal)/optimal)
	if rt < 25 {
		f *= 0.5
	}
	num *= f
	if s.g.thing(c).Passability == PassThroughOnly {
		num *= 0.4
	}
	return num
}
