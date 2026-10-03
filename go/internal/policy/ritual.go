package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainRituals holds each of the ideoligion's ritual precepts when it is
// due (#1660, epic #1653): it picks the spot, the organizer and the
// attendees, holds the attendees off the Sleep timetable (HeldOffSleep, the
// way the bestowing ceremony does, #1602) and begins the ritual through the
// Ritual `begin` verb (#1659). Cadence, cooldown, required buildings and role
// slots are the catalog's ritual defs (IdeologyDefs.Rituals); no ritual,
// building or role name is listed here.
const MaintainRituals GoalID = "MaintainRituals"

// RitualSite is a finished building a ritual may be held at: its ThingDef
// and the cell the game addresses it by.
type RitualSite struct {
	ID, Def string
	Cell    domain.Cell
}

// RitualSlotFill is the pawns that fill one role slot of a ritual's behavior,
// by the catalog's slot id, in priority order.
type RitualSlotFill struct {
	Slot  string
	Pawns []PawnID
}

// RitualPlan is one ritual precept ready to begin: Ritual is the precept's
// load id (HeldRitual.ID), Def the ritual pattern. Sites are the cells of the
// finished required buildings, in building id order: the game offers the
// begin command at the building its obligation targets, so the planner tries
// them in turn. Organizer leads the first filled slot; Slots fill the
// behavior's role slots and Spectators attend without a role.
type RitualPlan struct {
	Ritual, Def string
	Sites       []domain.Cell
	Organizer   PawnID
	Slots       []RitualSlotFill
	Spectators  []PawnID
}

// Attendees are every pawn the ritual gathers: the organizer, the slot
// fillers and the spectators.
func (p RitualPlan) Attendees() []PawnID {
	out := []PawnID{p.Organizer}
	for _, slot := range p.Slots {
		for _, pawn := range slot.Pawns {
			if pawn != p.Organizer {
				out = append(out, pawn)
			}
		}
	}
	return append(out, p.Spectators...)
}

// RitualCalm is whether the colony is calm enough to hold a ritual: known no
// hostile threat and no critical patient. Unknown stays unknown.
func RitualCalm(hostiles, patients domain.Fact[int64]) domain.Fact[bool] {
	h, hk := hostiles.Value()
	p, pk := patients.Value()
	if !hk || !pk {
		return domain.Unknown[bool]()
	}
	return domain.Known(h == 0 && p == 0)
}

// ritualDue reports whether the held ritual is due at tick. A running ritual
// is not. An active obligation (the game's own, raised by the pattern's
// triggers) is due. Otherwise the pattern must allow a free start, the repeat
// penalty must be off and the pattern's own cooldown (the minimum of its free
// start interval, in days) must have passed since the raw lastFinishedTick.
func ritualDue(def RitualDef, held HeldRitual, tick domain.Tick) bool {
	if held.Running {
		return false
	}
	if held.ActiveObligations > 0 {
		return true
	}
	if !def.CanStartAnytime && !def.AlwaysStartAnytime || held.RepeatPenaltyActive {
		return false
	}
	return float64(int64(tick)-held.LastFinishedTick) >= def.IntervalDaysMin*ticksPerDay
}

// PlanRituals are the rituals to begin now: each due held ritual (ritualDue)
// that has a finished building site and pawns to fill its required slots,
// sorted by precept id. Nothing is planned while the colony is not known
// calm (RitualCalm). A pawn attends one ritual per call: a pawn a ritual
// takes is not offered to a later one. Attendees are available believers of
// the ideoligion: a slot bound to a role precept takes that role's holders
// (up to the slot's maximum), a required slot with no role precept takes the
// most certain unassigned believer, optional unbound slots stay empty and
// every other believer attends as a spectator. Whether the game accepts a
// pawn in a slot is its own check when the begin applies.
// Unknown without the ideoligion, the pawn rows, the sites or the calm
// reading. A held ritual whose pattern the catalog lacks, or that names no
// required building, is not planned: nothing says where to hold it.
func PlanRituals(ideology domain.Fact[Ideoligion], pawns domain.Fact[[]WorkPawn], sites domain.Fact[[]RitualSite], tick domain.Tick, calm domain.Fact[bool]) domain.Fact[[]RitualPlan] {
	ideo, ik := ideology.Value()
	rows, pk := pawns.Value()
	known, sk := sites.Value()
	quiet, ck := calm.Value()
	if !ik || !pk || !sk || !ck {
		return domain.Unknown[[]RitualPlan]()
	}
	out := []RitualPlan{}
	if !quiet {
		return domain.Known(out)
	}
	type believer struct {
		id        PawnID
		certainty float64
	}
	var pool []believer
	for _, row := range rows {
		available, ak := row.Available.Value()
		inputs, nk := row.PolicyInputs.Value()
		certainty, _ := inputs.IdeoCertainty.Value()
		if ak && available && nk && inputs.Ideo == ideo.Facts.IdeoID {
			pool = append(pool, believer{row.ID, certainty})
		}
	}
	sort.Slice(pool, func(a, b int) bool {
		if pool[a].certainty != pool[b].certainty {
			return pool[a].certainty > pool[b].certainty
		}
		return pool[a].id < pool[b].id
	})
	rituals := slices.Clone(ideo.Facts.Rituals)
	sort.Slice(rituals, func(a, b int) bool { return rituals[a].ID < rituals[b].ID })
	taken := map[PawnID]bool{}
	for _, held := range rituals {
		def, ok := ideo.Defs.Rituals[held.Pattern]
		if !ok || len(def.RequiredBuildings) == 0 || !ritualDue(def, held, tick) {
			continue
		}
		var at []RitualSite
		for _, site := range known {
			if slices.Contains(def.RequiredBuildings, site.Def) {
				at = append(at, site)
			}
		}
		sort.Slice(at, func(a, b int) bool { return at[a].ID < at[b].ID })
		plan := RitualPlan{Ritual: held.ID, Def: held.Pattern}
		for _, site := range at {
			if !slices.Contains(plan.Sites, site.Cell) {
				plan.Sites = append(plan.Sites, site.Cell)
			}
		}
		if len(plan.Sites) == 0 {
			continue
		}
		used := map[PawnID]bool{}
		fillable := true
		for _, slot := range def.Roles {
			fill := RitualSlotFill{Slot: slot.ID}
			if slot.Precept != "" {
				for _, role := range ideo.Facts.Roles {
					if role.Def != slot.Precept || !role.Active {
						continue
					}
					for _, holder := range role.Pawns {
						pawn := PawnID(holder)
						if len(fill.Pawns) < slot.MaxCount && !taken[pawn] && !used[pawn] && slices.ContainsFunc(pool, func(b believer) bool { return b.id == pawn }) {
							fill.Pawns = append(fill.Pawns, pawn)
							used[pawn] = true
						}
					}
				}
			} else if slot.Required {
				for _, b := range pool {
					if !taken[b.id] && !used[b.id] {
						fill.Pawns = append(fill.Pawns, b.id)
						used[b.id] = true
						break
					}
				}
			}
			if len(fill.Pawns) == 0 {
				if slot.Required {
					fillable = false
					break
				}
				continue
			}
			plan.Slots = append(plan.Slots, fill)
		}
		if !fillable {
			continue
		}
		for _, b := range pool {
			if !taken[b.id] && !used[b.id] {
				plan.Spectators = append(plan.Spectators, b.id)
			}
		}
		if len(plan.Slots) > 0 {
			plan.Organizer = plan.Slots[0].Pawns[0]
		} else if len(plan.Spectators) > 0 {
			plan.Organizer, plan.Spectators = plan.Spectators[0], plan.Spectators[1:]
		} else {
			continue
		}
		for _, pawn := range plan.Attendees() {
			taken[pawn] = true
		}
		out = append(out, plan)
	}
	return domain.Known(out)
}

// RitualsOwed measures MaintainRituals: known false when no ritual can begin
// now, unknown while its inputs are.
func RitualsOwed(plans domain.Fact[[]RitualPlan]) domain.Fact[bool] {
	rows, ok := plans.Value()
	if !ok {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(rows) > 0)
}

// HeldOffSleep is the colonists a pending bestowing ceremony (CeremonyHold)
// or a ritual about to begin (the attendees of every plan) needs awake: the
// schedule planners keep them off the Sleep timetable (PlanSchedulesHeld).
func HeldOffSleep(f RoutineFacts) map[PawnID]bool {
	hold := CeremonyHoldOf(f.Royalty)
	plans, _ := f.RitualPlans.Value()
	for _, plan := range plans {
		if hold == nil {
			hold = map[PawnID]bool{}
		}
		for _, pawn := range plan.Attendees() {
			hold[pawn] = true
		}
	}
	return hold
}
