// Package combatsim is a deterministic, pure-Go combat simulator: a
// mechanics-faithful port of the game's grid rules (line of sight and lean,
// cover, per-cell movement cost) and of ordinary ranged resolution (hit
// chance, warmup, burst and cooldown, projectile flight and interception,
// armor, an injury approximation) behind a Reset/Step seam. Adapter and
// RunFight let the unchanged policy.DecideCombat drive a fight: they build its
// CombatView, StopEvent and GeometryReply from sim state and apply its orders.
// EnemyAI (Fight.EnemyAI) drives the hostile ranged raiders: target scoring
// and choice, positioning, and the raid's flee triggers, all drawn from the
// enemy stream. Melee comes in a later child of epic #2830. Every
// simplification is listed in approximations.md.
//
// Time is the game tick. Reset(scenario, seed) builds the world and the two
// simulator-owned random streams (physical outcomes and enemy choice); Step
// applies orders and advances Scenario.StepTicks ticks, returning the new
// state and the events of those ticks. The simulator never reads or clones
// native RNG state, and nothing it holds is persisted. A scenario missing
// required state fails Reset with ErrUnsupported; no value is guessed.
//
// Definition values (speeds, cover and path costs) arrive in the Scenario,
// read from the native catalog by the caller; this package holds none. Rules
// ported from the game name their source and are versioned by
// AssemblyCSharpSHA256.
//
// Not modeled yet, by design: snow, sand and fire path costs, terrain-tag speed
// factors, downed/crawling/restrained/carrying movement, door closing and
// door-open delays, flying. Pawns share cells and only a standing hostile
// blocks a walker (A35); the rest of PatherTick is a port.
package combatsim

import (
	"math/rand/v2"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Urgency is a job's LocomotionUrgency; the zero value is Jog, the game's default.
type Urgency uint8

const (
	Jog Urgency = iota
	Sprint
	Walk
	Amble
)

// PawnSpec is a pawn at Reset.
type PawnSpec struct {
	ID  domain.PawnID
	Pos domain.Cell
	// MoveSpeed is the pawn's MoveSpeed stat in cells per second. Unknown marks
	// the scenario unsupported.
	MoveSpeed domain.Fact[float64]
	// Side is combatmetrics.Colonist or combatmetrics.Hostile; required when
	// Combat is set.
	Side string
	// Combat makes the pawn a combatant: it can be shot and, with a Weapon,
	// shoot. Nil marks a non-combatant that shots pass through and orders
	// cannot target.
	Combat *CombatSpec
}

// Scenario is the simulator input. Reset copies it; the simulator never
// mutates it.
type Scenario struct {
	Width, Height int
	// Terrain and Things are palettes; the id grids index them. A TerrainIDs
	// value is an index into Terrain; a ThingIDs value k>0 is Things[k-1] and 0
	// is no thing. Each cell holds at most one thing, the one that dominates
	// cover and sight.
	Terrain    []TerrainDef
	Things     []ThingDef
	TerrainIDs []uint8 // Width*Height, row-major by z; nil means all Terrain[0]
	ThingIDs   []uint8 // Width*Height; nil means no things
	Roofed     []bool  // Width*Height; nil means no roofs
	// OpenDoors lists door cells that start open.
	OpenDoors []domain.Cell
	// WeatherMoveSpeed is the weather's move speed multiplier outside roofs.
	// Unknown marks the scenario unsupported.
	WeatherMoveSpeed domain.Fact[float64]
	// WeatherAccuracy is the weather's shooting accuracy multiplier
	// (CurWeatherAccuracyMultiplier), applied unless both shooter and target
	// are roofed. FriendlyFireChanceFactor is the difficulty's
	// friendlyFireChanceFactor. Both are required once any pawn has Combat set;
	// unknown marks the scenario unsupported.
	WeatherAccuracy          domain.Fact[float64]
	FriendlyFireChanceFactor domain.Fact[float64]
	// StepTicks is the game ticks one Step advances; it must be positive.
	StepTicks int
	Pawns     []PawnSpec
}

// OrderKind selects an Order.
type OrderKind uint8

const (
	// OrderMove walks the pawn to To.
	OrderMove OrderKind = iota
	// OrderStop halts the pawn where it stands.
	OrderStop
	// OrderAttack makes the pawn shoot Target until it is down, dead or the
	// pawn is given another order. It stands and waits while Target is out of
	// range or sight.
	OrderAttack
	// OrderLeave takes the pawn off the map (the enemy AI orders it on its
	// exit cell). The pawn stops being a combatant: shots pass through it and
	// it is no longer a target.
	OrderLeave
)

// Order is one pawn command applied at the start of a Step.
type Order struct {
	Pawn    domain.PawnID
	Kind    OrderKind
	To      domain.Cell
	Urgency Urgency
	// Target is the pawn an OrderAttack shoots at.
	Target domain.PawnID
	// Once makes an OrderAttack one burst: the pawn drops its target when the
	// burst's cooldown ends, as a verb started with Pawn.TryStartAttack does,
	// so whoever chooses targets chooses again.
	Once bool
	// Auto makes an OrderAttack JobDriver_Wait.CheckForAutoAttack's
	// Pawn.TryStartAttack: the verb starts the moment the order lands (the
	// tick it is applied at), so its warmup counts down from the next tick. An
	// attack without Auto is JobDriver_AttackStatic's: the order starts the job
	// and the job's first tick starts the verb, one tick later.
	Auto bool
}

// EventKind names what happened.
type EventKind uint8

const (
	// EventMoveStarted: a path was found; ETA is the tick the pawn arrives.
	EventMoveStarted EventKind = iota + 1
	// EventMoved: the pawn entered Cell.
	EventMoved
	// EventArrived: the pawn reached its destination Cell.
	EventArrived
	// EventBlocked: another pawn stands on the pawn's next Cell; it waits.
	EventBlocked
	// EventPathFailed: no path to Cell (not standable, out of bounds or unreachable).
	EventPathFailed
	// EventDoorOpened: the pawn approached the closed door at Cell.
	EventDoorOpened
	// EventStopped: a stop order halted the pawn at Cell.
	EventStopped
	// EventShot: Pawn fired at Target; Outcome says where the shot goes.
	EventShot
	// EventHit: a projectile of Pawn hit Target (the victim, which may not be
	// the pawn aimed at) for Amount damage after armor.
	EventHit
	// EventDowned and EventDied: Pawn is down or dead; Target is the
	// instigator of the hit that did it.
	EventDowned
	EventDied
	// EventAttackEnded: Pawn's attack order ended because Target is down or dead.
	EventAttackEnded
	// EventLeftMap: Pawn left the map (OrderLeave); Cell is where it stood.
	EventLeftMap
)

// ShotOutcome is what a fired shot is already decided to do.
type ShotOutcome uint8

const (
	// ShotFlying: aimed at the target; it hits it unless a pawn intercepts it
	// (an EventHit follows when the projectile lands).
	ShotFlying ShotOutcome = iota + 1
	// ShotWild: missed wide; nothing is hit.
	ShotWild
	// ShotCover: absorbed by cover next to the target; nothing is hit.
	ShotCover
)

// Event is one thing that happened during a Step.
type Event struct {
	Tick int
	Kind EventKind
	Pawn domain.PawnID
	Cell domain.Cell
	// ETA is set on EventMoveStarted: the arrival tick, and on an EventShot
	// whose Outcome is ShotFlying: the tick its projectile lands.
	ETA int
	// Target, Amount and Outcome are set by the combat events.
	Target  domain.PawnID
	Amount  float64
	Outcome ShotOutcome
}

// PawnState is a pawn's observable state.
type PawnState struct {
	ID     domain.PawnID
	Pos    domain.Cell
	Moving bool
	Dest   domain.Cell
	// Damage is the post-armor damage taken; Downed and Dead are the injury
	// approximation's verdicts (Dead implies Downed).
	Damage float64
	Downed bool
	Dead   bool
}

// State is the world after a Step. Pawns is in scenario order. Both State and
// the event slice Step returns alias buffers reused by the next Step; copy
// what must outlive it.
type State struct {
	Tick  int
	Pawns []PawnState
}

// Streams are the simulator-owned random streams, seeded from the Reset seed.
// Physical draws decide physical outcomes (hits, damage); Enemy draws decide
// enemy choice. They are separate so a change in enemy behavior does not
// shift physical draws.
type Streams struct {
	Physical *rand.Rand
	Enemy    *rand.Rand
	// Phase draws the hash phases the game derives from thing ids the sim does
	// not model (A9): which tick of the 15-tick cycle a projectile is ticked on.
	Phase *rand.Rand
}

// Stream selectors for the PCG second word: ASCII "physical", "enemy" and
// "phase".
const (
	physicalStream uint64 = 0x706879736963616c
	enemyStream    uint64 = 0x656e656d79
	phaseStream    uint64 = 0x7068617365
)

// Sim is a combat simulator. The zero value is ready for Reset; a Sim is not
// safe for concurrent use.
type Sim struct {
	g       grid
	tick    int
	step    int
	weather float64
	pawns   []pawn
	// onArrive runs in the pather tick that ends pawn i's walk (the adapter's
	// Wait_Combat auto-attack).
	onArrive func(i int)
	cells    cellPawns // who stands where; pawns share cells (A35)
	applied  bool      // Apply ran since the last Step: events are already reset
	streams  Streams

	events []Event
	state  State

	pending []projectile // in flight, in launch order
	ledger  []combatmetrics.Hit
	ff      float64 // FriendlyFireChanceFactor
	wxAcc   float64 // WeatherAccuracy

	// scratch reused across calls
	leanSource, leanDest []domain.Cell
	search               search
}

// Reset builds the world from sc and seeds the random streams from seed. It
// returns ErrUnsupported (wrapped, with the reason) for a scenario it cannot
// represent.
func (s *Sim) Reset(sc Scenario, seed uint64) (State, error) {
	if err := s.load(sc); err != nil {
		return State{}, err
	}
	s.tick = 0
	s.streams = Streams{
		Physical: rand.New(rand.NewPCG(seed, physicalStream)),
		Enemy:    rand.New(rand.NewPCG(seed, enemyStream)),
		Phase:    rand.New(rand.NewPCG(seed, phaseStream)),
	}
	s.events, s.applied = s.events[:0], false
	s.pending = s.pending[:0]
	s.ledger = s.ledger[:0]
	return s.snapshot(), nil
}

// Streams returns the random streams of the current run.
func (s *Sim) Streams() Streams { return s.streams }

// Tick is the current game tick (0 after Reset).
func (s *Sim) Tick() int { return s.tick }

// Apply applies orders at the current tick without advancing it; events they
// raise join the next Step's. An order naming no pawn is an error and applies
// nothing after it. A caller that derives orders from the pawns' state (the
// fire-at-will holders' Autonomy) applies the orders already decided first, so
// it reads a pawn that has just been told to walk as walking.
func (s *Sim) Apply(orders []Order) error {
	if !s.applied {
		s.events, s.applied = s.events[:0], true
	}
	for _, o := range orders {
		if err := s.apply(o); err != nil {
			return err
		}
	}
	return nil
}

// Step applies orders, advances StepTicks ticks and returns the new state and
// the events of those ticks in order. An order naming no pawn is an error and
// applies nothing after it.
func (s *Sim) Step(orders []Order) (State, []Event, error) {
	err := s.Apply(orders)
	s.applied = false
	if err != nil {
		return State{}, nil, err
	}
	for range s.step {
		s.tick++
		for i := range s.pawns {
			s.pawns[i].tick(s, i)
			if s.pawns[i].arrived {
				s.pawns[i].arrived = false
				if s.onArrive != nil {
					s.onArrive(i)
				}
			}
			s.combatTick(i)
		}
		s.landProjectiles()
	}
	return s.snapshot(), s.events, nil
}

func (s *Sim) snapshot() State {
	s.state.Tick = s.tick
	s.state.Pawns = s.state.Pawns[:0]
	for i := range s.pawns {
		p := &s.pawns[i]
		s.state.Pawns = append(s.state.Pawns, PawnState{
			ID: p.id, Pos: p.pos, Moving: p.moving, Dest: p.dest,
			Damage: p.damage, Downed: p.downed, Dead: p.dead,
		})
	}
	return s.state
}

func (s *Sim) emit(e Event) {
	e.Tick = s.tick
	s.events = append(s.events, e)
}

func (s *Sim) pawnIndex(id domain.PawnID) int {
	for i := range s.pawns {
		if s.pawns[i].id == id {
			return i
		}
	}
	return -1
}

func (s *Sim) load(sc Scenario) error {
	n := sc.Width * sc.Height
	switch {
	case sc.Width <= 0 || sc.Height <= 0:
		return unsupported("map size %dx%d", sc.Width, sc.Height)
	case sc.StepTicks <= 0:
		return unsupported("StepTicks %d must be positive", sc.StepTicks)
	case len(sc.Terrain) == 0 || len(sc.Terrain) > 255 || len(sc.Things) > 255:
		return unsupported("palette sizes terrain %d things %d", len(sc.Terrain), len(sc.Things))
	case sc.TerrainIDs != nil && len(sc.TerrainIDs) != n,
		sc.ThingIDs != nil && len(sc.ThingIDs) != n,
		sc.Roofed != nil && len(sc.Roofed) != n:
		return unsupported("a cell grid does not match the %d-cell map", n)
	}
	weather, ok := sc.WeatherMoveSpeed.Value()
	if !ok || !(weather > 0) {
		return unsupported("weather move speed multiplier unknown or not positive")
	}
	g := &s.g
	g.w, g.h = sc.Width, sc.Height
	g.terrain = append(g.terrain[:0], sc.Terrain...)
	g.things = append(append(g.things[:0], ThingDef{}), sc.Things...)
	g.terrainI = fill(g.terrainI, sc.TerrainIDs, n)
	g.thingI = fill(g.thingI, sc.ThingIDs, n)
	for i := range n {
		if int(g.terrainI[i]) >= len(g.terrain) || int(g.thingI[i]) >= len(g.things) {
			return unsupported("cell %d names a palette entry that does not exist", i)
		}
	}
	g.roofed = append(g.roofed[:0], make([]bool, n)...)
	copy(g.roofed, sc.Roofed)
	g.doorOpen = append(g.doorOpen[:0], make([]bool, n)...)
	for _, c := range sc.OpenDoors {
		if !g.inBounds(c) || !g.thing(c).Door {
			return unsupported("open door at %v is not a door cell", c)
		}
		g.doorOpen[g.idx(c)] = true
	}
	s.weather = weather
	s.step = sc.StepTicks
	s.cells.head = append(s.cells.head[:0], make([]int32, n)...)
	s.pawns = s.pawns[:0]
	for i, ps := range sc.Pawns {
		speed, known := ps.MoveSpeed.Value()
		switch {
		case !known || speed < 0:
			return unsupported("pawn %q move speed unknown or negative", ps.ID)
		case !g.standable(ps.Pos):
			return unsupported("pawn %q starts on %v, which is not standable", ps.ID, ps.Pos)
		case s.pawnIndex(ps.ID) >= 0:
			return unsupported("duplicate pawn id %q", ps.ID)
		}
		np := pawn{id: ps.ID, pos: ps.Pos, speed: speed, dest: ps.Pos, side: ps.Side, target: -1}
		if ps.Combat != nil {
			if err := np.loadCombat(ps); err != nil {
				return err
			}
		}
		s.pawns = append(s.pawns, np)
		s.enter(g.idx(ps.Pos), i)
	}
	return s.loadCombatWorld(sc)
}

func fill(dst, src []uint8, n int) []uint8 {
	dst = append(dst[:0], make([]uint8, n)...)
	copy(dst, src)
	return dst
}
