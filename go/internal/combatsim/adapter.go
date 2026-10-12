package combatsim

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Fight is a Scenario plus what the policy reads about its pawns that the
// simulator does not model. The adapter builds the live parts of the view
// (cells, downed and dead, stance, target, weapon, health) from sim state;
// everything here is the caller's, read from the native catalog and frame,
// and anything left out reaches the policy as an unknown fact, never a
// default.
type Fight struct {
	// Name labels the Metrics row.
	Name     string
	Scenario Scenario
	// Orderable are the colonist combatants the fight's plan owns: the only
	// pawns an order may name. Nil means every colonist combatant.
	Orderable []domain.PawnID
	// Layout is the stored defense layout the view holds, unknown without one.
	Layout domain.Fact[policy.CombatLayout]
	// Facts are per-pawn static facts by pawn id.
	Facts map[domain.PawnID]PawnFacts
	// EnemyAI, when set, drives the hostile ranged pawns with the ported
	// raider decisions (enemy.go); RunFight builds a fresh EnemyAI from it for
	// each run, so the Fight can be reused across seeds.
	EnemyAI *EnemyConfig
	// Enemy, when set, also gives orders each Step from the sim state, for
	// scripted opponents. The adapter itself never decides for a hostile.
	Enemy func(State) []Order
	// Trace, when set, receives the events of every Step in order, before the
	// next one overwrites them (a calibration read of shots, hits and
	// arrivals; the slice aliases the sim's buffer).
	Trace func([]Event)
	// BackstopTicks is how many ticks pass without a stop before the policy
	// is asked again, the native tick budget's role. Zero means 120 (two
	// game seconds), a simulator parameter and not a native value.
	BackstopTicks int
	// MaxTicks ends an unresolved fight; it must be positive for RunFight.
	MaxTicks int
}

// PawnFacts are the view facts about one pawn that the simulator neither
// holds nor derives. A zero Fact is unknown.
type PawnFacts struct {
	// Kind is the pawn kind def.
	Kind string
	// FrontLine is the pawn's place in policy.FrontLine's split.
	FrontLine bool
	// Armor is the worn sharp armor rating; MeleePower the MeleeDPS stat
	// scaled by health.
	Armor, MeleePower domain.Fact[float64]
	// LordJobClass and LordToilClass are the hostile's lord evidence, which
	// the hold-the-line gate needs to know how the raid arrives.
	LordJobClass, LordToilClass domain.Fact[string]
}

// Refusal reasons, the strings of the bridge's combat order refusals the
// policy's memory reacts to (bridge.CombatRefusal*); this package does not
// import the bridge.
const (
	RefusedNotDrafted   = "not_drafted"
	RefusedNotFound     = "not_found"
	RefusedUnreachable  = "unreachable"
	RefusedCannotHit    = "cannot_hit"
	RefusedNativeRefuse = "native_refused"
)

// stopEnteredRange is the combat stop kind the native range scan raises.
const stopEnteredRange policy.CombatStopKind = "entered_range"

// holdJob is the vanilla job a hold_position order starts.
const holdJob = "Wait_Combat"

// OrderResult is one policy order's outcome, as the native batch answers it.
type OrderResult struct {
	Order   policy.CombatOrder
	Applied bool
	Refusal string
}

// Adapter lets the unchanged policy.DecideCombat drive a Sim: it builds the
// CombatView, the StopEvent and the GeometryReply from sim state and applies
// the policy's orders to sim pawns. It reads the sim's current world on every
// call, so geometry follows pawns that move; nothing is cached from an
// earlier answer. An Adapter is not safe for concurrent use.
type Adapter struct {
	f         Fight
	sim       *Sim
	orderable map[domain.PawnID]bool
	hold      map[domain.PawnID]bool // hold_position: Wait_Combat in place
	holdFire  map[domain.PawnID]bool // fire mode Hold
	inRange   map[domain.PawnID]bool // hostiles that reached a colonist
	inOurs    map[domain.PawnID]bool // hostiles a colonist reached
	scanned   bool
	// waiting marks the pawns that could search for a target at the last
	// Autonomy (Autonomy's edge detection, A41).
	waiting []bool
}

// NewAdapter wraps a Sim that has been Reset from f.Scenario. It returns
// ErrUnsupported when the fight names pawns the scenario lacks or orders
// pawns that cannot fight.
func NewAdapter(f Fight, sim *Sim) (*Adapter, error) {
	a := &Adapter{f: f, sim: sim, orderable: map[domain.PawnID]bool{}, hold: map[domain.PawnID]bool{},
		holdFire: map[domain.PawnID]bool{}, inRange: map[domain.PawnID]bool{}, inOurs: map[domain.PawnID]bool{}}
	sim.onArrive = a.arrived
	for id := range f.Facts {
		if sim.pawnIndex(id) < 0 {
			return nil, unsupported("facts name pawn %q, which is not in the scenario", id)
		}
	}
	for i := range sim.pawns {
		p := &sim.pawns[i]
		if !p.has {
			continue
		}
		pf := f.Facts[p.id]
		_, melee := pf.MeleePower.Value()
		_, armor := pf.Armor.Value()
		switch {
		case pf.Kind == "":
			return nil, unsupported("pawn %q has no kind fact", p.id)
		case !melee:
			return nil, unsupported("pawn %q has no melee power fact", p.id)
		case p.side == combatmetrics.Colonist && !armor:
			return nil, unsupported("colonist %q has no armor fact", p.id)
		}
	}
	if f.Orderable == nil {
		for i := range sim.pawns {
			if p := &sim.pawns[i]; p.has && p.side == combatmetrics.Colonist {
				a.orderable[p.id] = true
			}
		}
		return a, nil
	}
	for _, id := range f.Orderable {
		i := sim.pawnIndex(id)
		if i < 0 || !sim.pawns[i].has || sim.pawns[i].side != combatmetrics.Colonist {
			return nil, unsupported("orderable pawn %q is not a colonist combatant", id)
		}
		a.orderable[id] = true
	}
	return a, nil
}

func (a *Adapter) colonist(p *pawn) bool { return p.has && p.side == combatmetrics.Colonist }

func fact(v float64) domain.Fact[float64] { return domain.Known(v) }

// stance maps the sim's stance to the mirror's: a burst's gaps are
// Stance_Cooldown in the game.
func (p *pawn) mirrorStance() policy.CombatStance {
	switch {
	case p.stance == stanceWarmup:
		return policy.StanceWarmup
	case p.stance == stanceBurst || p.stance == stanceCooldown:
		return policy.StanceCooldown
	case p.moving:
		return policy.StanceMoving
	}
	return policy.StanceIdle
}

// rangedDPS is NativeDefenseStats.RangedDps: projectile damage times burst
// over one cycle of warmup, cooldown and burst gaps.
func (p *pawn) rangedDPS() float64 {
	w := &p.weapon
	cycle := w.WarmupSeconds*p.sh.aim + w.CooldownSeconds + float64(w.BurstShotCount-1)*float64(w.TicksBetweenBurstShots)/60
	if cycle <= 0 {
		return 0
	}
	return float64(w.DamageAmount*w.BurstShotCount) / cycle
}

// healthFraction is the share of the damage budget left, 1 unhurt and 0 at
// death (approximations.md A20).
func (p *pawn) healthFraction() float64 {
	return max(0, 1-p.damage/p.killAt)
}

// View is the fight as the policy sees it now. Pawns lists every combatant;
// Defenders, Threats and Positional list those still alive.
func (a *Adapter) View() policy.CombatView {
	s := a.sim
	v := policy.CombatView{Tick: domain.Tick(s.tick), Layout: a.f.Layout}
	var orderable []domain.PawnID
	colonists := 0
	for i := range s.pawns {
		p := &s.pawns[i]
		if a.colonist(p) && !p.dead {
			colonists++
		}
	}
	v.Population = domain.Known(colonists)
	for i := range s.pawns {
		p := &s.pawns[i]
		if !p.has {
			continue
		}
		ps := policy.CombatPawnState{
			ID: p.id, Cell: domain.Known(p.pos), Downed: p.downed, Dead: p.dead,
			Stance: p.mirrorStance(), MoveSpeed: p.speed, Kind: a.f.Facts[p.id].Kind,
			Health: domain.Known(p.healthFraction()),
		}
		if a.colonist(p) && a.orderable[p.id] {
			ps.FireMode = policy.FireAtWill
			if a.holdFire[p.id] {
				ps.FireMode = policy.HoldFire
			}
		}
		switch {
		case a.hold[p.id]:
			ps.Job = holdJob
		case p.target >= 0:
			ps.Target = s.pawns[p.target].id
		}
		if p.armed {
			w := &p.weapon
			ps.Weapon, ps.WeaponRange = w.Name, w.Range
			ps.WeaponFacts = policy.WeaponDef{Ranged: true, Range: w.Range, Reach: w.Range, DPS: p.rangedDPS(), AP: w.ArmorPenetration}
		}
		v.Pawns = append(v.Pawns, ps)
		if p.dead {
			continue
		}
		facts := a.f.Facts[p.id]
		if a.colonist(p) {
			v.Defenders = append(v.Defenders, a.defender(p, facts))
			if a.orderable[p.id] {
				orderable = append(orderable, p.id)
			}
			continue
		}
		v.Threats = append(v.Threats, policy.SquadThreatFacts{
			ID: policy.PawnID(p.id), Dead: domain.Known(false), Downed: domain.Known(p.downed),
			Humanlike: domain.Known(true), Animal: domain.Known(false), BodySize: fact(p.bodySize),
			Manhunter: domain.Known(false), Hunting: domain.Known(false), RangedEquipped: domain.Known(p.armed),
			MeleePower: facts.MeleePower,
		})
		v.Positional = append(v.Positional, policy.DefensiveThreatFacts{
			ID: policy.PawnID(p.id), Dead: domain.Known(false), Downed: domain.Known(p.downed), Humanlike: domain.Known(true),
			LordJobClass: facts.LordJobClass, LordToilClass: facts.LordToilClass,
			NearestColonistDistance: a.nearestColonist(p), Position: domain.Known(p.pos),
		})
	}
	slices.Sort(orderable)
	v.Orderable = orderable
	return v
}

func (a *Adapter) defender(p *pawn, facts PawnFacts) policy.SquadDefenderFacts {
	d := policy.SquadDefenderFacts{
		ID: p.id, Dead: domain.Known(false), Downed: domain.Known(p.downed),
		Drafted: domain.Known(a.orderable[p.id]), MentalState: domain.Known(false), PlayerForced: domain.Known(false),
		DraftOwned: domain.Known(false), QueuedJobs: domain.Known(uint32(0)),
		ViolenceCapable: domain.Known(true), NeedsTend: domain.Known(false),
		HealthFraction: fact(p.healthFraction()),
		RangedEquipped: domain.Known(p.armed), MeleeEquipped: domain.Known(false), Armed: domain.Known(p.armed),
		FrontLine: facts.FrontLine, Armor: facts.Armor, MeleePower: facts.MeleePower,
	}
	if p.armed {
		d.RangedDPS = fact(p.rangedDPS())
	} else {
		d.RangedDPS = fact(0)
	}
	return d
}

// nearestColonist is the Chebyshev distance to the nearest living colonist
// combatant (NativePawnObservationTools.Distance); unknown with none.
func (a *Adapter) nearestColonist(from *pawn) domain.Fact[float64] {
	best := -1
	for i := range a.sim.pawns {
		p := &a.sim.pawns[i]
		if !a.colonist(p) || p.dead {
			continue
		}
		d := max(abs32(p.pos.X-from.pos.X), abs32(p.pos.Z-from.pos.Z))
		if best < 0 || d < best {
			best = d
		}
	}
	if best < 0 {
		return domain.Unknown[float64]()
	}
	return fact(float64(best))
}

// Stop is the stop the events of a Step raise, and whether there is one. A
// colonist downed or killed raises "downed" (a hit on a colonist that is
// seriousHit raises "serious_injury" unless the same step also downs one); the
// first hostile to come within
// reach of a colonist, or a colonist's weapon to reach it, raises
// "entered_range" (once per hostile each way, the first scan only recording
// the state at the start). Other native stop kinds are not raised: there are
// no shields, lords, melee, explosives or mental states in the sim, and the
// vital-part serious trigger cannot fire for bullets (approximations.md A22).
func (a *Adapter) Stop(events []Event) (policy.StopEvent, bool) {
	var stop policy.StopEvent
	found := false
	var serious policy.StopEvent
	seriousFound := false
	for _, e := range events {
		switch e.Kind {
		case EventHit:
			// Pawn_HealthTracker.PostApplyDamage: a serious injury to a
			// colonist (A22).
			if i := a.sim.pawnIndex(e.Target); !seriousFound && i >= 0 && a.colonist(&a.sim.pawns[i]) && seriousHit(e) {
				serious, seriousFound = policy.StopEvent{Kind: policy.StopSeriousInjury, Pawn: e.Target}, true
			}
		case EventDowned, EventDied:
			if i := a.sim.pawnIndex(e.Pawn); i >= 0 && a.colonist(&a.sim.pawns[i]) {
				stop, found = policy.StopEvent{Kind: policy.StopDowned, Pawn: e.Pawn}, true
			}
		}
	}
	// A hit that downs the pawn notes the downing first (the game's
	// CheckForStateChange runs inside PostApplyDamage, before the serious
	// check), and the first event since the tick boundary wins.
	if !found && seriousFound {
		stop, found = serious, true
	}
	if h, ok := a.scanRange(); ok && !found {
		return policy.StopEvent{Kind: stopEnteredRange, Pawn: h}, true
	}
	return stop, found
}

// seriousHit is SupervisedPlayCombatStops.SeriousWhy for a hit on a colonist:
// one blow of at least SeriousSingleHitDamage, summary health crossing from
// above SeriousSummaryHealthFloor to at or under it, or the total bleed rate
// crossing from under SeriousBleedRateFloor to at or over it. The vital-part
// trigger needs DamageInfo.HitPart, which a projectile's DamageInfo leaves null
// (Bullet.Impact passes no part and Apply works on a copy), so it never fires
// for the sim's ranged hits.
func seriousHit(e Event) bool {
	health, floor := float32(e.Health), policy.SeriousSummaryHealthFloor
	bleed, bleedFloor := float32(e.Bleed), policy.SeriousBleedRateFloor
	return e.Amount >= float64(policy.SeriousSingleHitDamage) ||
		(float32(e.HealthBefore) > floor && health <= floor) ||
		(float32(e.BleedBefore) < bleedFloor && bleed >= bleedFloor)
}

// scanRange is SupervisedPlayCombatStops.ScanCombatContact's range half for
// ranged pawns: distance between cells against the weapon range, no sight.
func (a *Adapter) scanRange() (domain.PawnID, bool) {
	s := a.sim
	baseline := !a.scanned
	a.scanned = true
	var first domain.PawnID
	raised := false
	reach := func(x, y *pawn) bool {
		if !x.armed {
			return false
		}
		dx, dz := float64(x.pos.X-y.pos.X), float64(x.pos.Z-y.pos.Z)
		return dx*dx+dz*dz <= x.weapon.Range*x.weapon.Range
	}
	for i := range s.pawns {
		h := &s.pawns[i]
		if !h.has || h.side != combatmetrics.Hostile || h.dead || h.downed {
			continue
		}
		for _, mark := range []struct {
			seen map[domain.PawnID]bool
			hits func(c *pawn) bool
		}{
			{a.inRange, func(c *pawn) bool { return reach(h, c) }},
			{a.inOurs, func(c *pawn) bool { return reach(c, h) }},
		} {
			if mark.seen[h.id] {
				continue
			}
			for j := range s.pawns {
				if c := &s.pawns[j]; a.colonist(c) && !c.dead && !c.downed && mark.hits(c) {
					mark.seen[h.id] = true
					if !baseline && !raised {
						first, raised = h.id, true
					}
					break
				}
			}
		}
	}
	return first, raised
}

// Apply validates the policy's orders the way the native batch does and
// returns each one's result plus the sim orders to apply at the next Step.
// A refused order changes nothing. An order kind the ranged slice does not
// execute (rescue, repair, doors, mortars, drugs, permits, casts, animals) is
// refused native_refused rather than dropped.
func (a *Adapter) Apply(orders []policy.CombatOrder) ([]OrderResult, []Order) {
	results := make([]OrderResult, 0, len(orders))
	var out []Order
	for _, o := range orders {
		sim, refusal := a.apply(o)
		results = append(results, OrderResult{Order: o, Applied: refusal == "", Refusal: refusal})
		if refusal == "" && sim != nil {
			out = append(out, *sim)
		}
	}
	return results, out
}

func (a *Adapter) apply(o policy.CombatOrder) (*Order, string) {
	s := a.sim
	i := s.pawnIndex(o.Pawn)
	switch {
	case i < 0 || s.pawns[i].dead:
		return nil, RefusedNotFound
	case !a.orderable[o.Pawn]:
		return nil, RefusedNotDrafted
	}
	p := &s.pawns[i]
	if p.downed {
		return nil, RefusedNotDrafted // a downed pawn is undrafted
	}
	switch o.Kind {
	case policy.OrderMove:
		if !s.g.standable(o.Cell) {
			return nil, RefusedUnreachable
		}
		if o.Cell != p.pos {
			if _, ok := s.findPath(p, o.Cell); !ok {
				return nil, RefusedUnreachable
			}
		}
		delete(a.hold, o.Pawn)
		return &Order{Pawn: o.Pawn, Kind: OrderMove, To: o.Cell}, ""
	case policy.OrderStop:
		delete(a.hold, o.Pawn)
		return &Order{Pawn: o.Pawn, Kind: OrderStop}, ""
	case policy.OrderHoldPosition:
		a.hold[o.Pawn] = true
		return &Order{Pawn: o.Pawn, Kind: OrderStop}, ""
	case policy.OrderFireMode:
		if o.FireMode == policy.HoldFire {
			a.holdFire[o.Pawn] = true
		} else {
			delete(a.holdFire, o.Pawn)
		}
		return nil, ""
	case policy.OrderAttack:
		ti := s.pawnIndex(o.Target)
		switch {
		case ti < 0 || s.pawns[ti].dead:
			return nil, RefusedNotFound
		case !p.armed || !s.pawns[ti].has || s.pawns[ti].side == p.side:
			return nil, RefusedCannotHit
		case !s.pawns[ti].downed && !s.canHitFrom(i, ti):
			return nil, RefusedCannotHit
		}
		delete(a.hold, o.Pawn)
		return &Order{Pawn: o.Pawn, Kind: OrderAttack, Target: o.Target}, ""
	}
	return nil, RefusedNativeRefuse
}
