package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HoldGatherings holds a vanilla party when the colony as a whole is losing
// mood a party would repay. It is a mood source with a cost (colonist time),
// not a repair: the PartySpot is kept by EnsureComfort (PartySpotDefinition),
// and this concern only decides when to start the party and who organizes it.
// Every number below is a vanilla fact, not a tuning choice:
//
//   - the def is Core's Party, the GatheringDef whose gatherSpotDefs name the
//     PartySpot;
//   - a party gives each attendee the AttendedParty memory (ThoughtDef
//     baseMoodEffect), read from the catalog, so a pawn is worth a party when
//     its ledger loss is at least that effect;
//   - the game itself refuses under four colonists and sizes the guest list to
//     0.65 of them (GatheringsUtility.AcceptableGameConditionsToStartGathering),
//     so the party is worth holding when at least that share of the colony is
//     under pressure;
//   - the cooldown is the AttendedParty memory: while any colonist still
//     carries it (ThoughtDef durationDays) the last party is still paying out,
//     so nothing is stored;
//   - a party's lord job runs 5000 to 15000 ticks
//     (LordJob_Joinable_Party.durationTicks) and, while it runs, the game
//     refuses a new gathering.
const HoldGatherings ConcernID = "HoldGatherings"

const (
	// PartyGatheringDef is the GatheringDef whose gatherSpotDefs is the PartySpot.
	PartyGatheringDef = "Party"
	// PartyThought is the memory each attendee gains when the party ends.
	PartyThought = "AttendedParty"
	// GatheringMinColonists and GatheringGuestShare are the game's own gate
	// and guest fraction (see HoldGatherings).
	GatheringMinColonists = 4
	GatheringGuestShare   = 0.65
	// GatheringMaxTicks is the longest a party runs.
	GatheringMaxTicks domain.Tick = 15000
)

// GatheringInput is what the gathering review reads. A fact left unknown
// never starts a party.
type GatheringInput struct {
	Ledger domain.Fact[MoodLedger]
	// Pawns is the mood census: the guests' state and the AttendedParty memory.
	Pawns domain.Fact[[]MoodPawn]
	// Benefit is the AttendedParty mood effect from the catalog.
	Benefit domain.Fact[float64]
	// Calm is RitualCalm: no hostile threat and no critical patient.
	Calm domain.Fact[bool]
	// Census holds the built PartySpot.
	Census domain.Fact[CurrentConstruction]
}

// GatheringPlan is one party ready to start: the organizer to name.
type GatheringPlan struct {
	Def       string
	Organizer PawnID
}

// PlanGathering returns the party to start now, or the zero plan (Organizer
// empty) when none is due. Unknown while any gate's input is unknown and no
// known gate already says no.
func PlanGathering(in GatheringInput) domain.Fact[GatheringPlan] {
	none := domain.Known(GatheringPlan{})
	pawns, pk := in.Pawns.Value()
	ledger, lk := in.Ledger.Value()
	benefit, bk := in.Benefit.Value()
	calm, ck := in.Calm.Value()
	census, sk := in.Census.Value()
	// Known gates that refuse, whatever else is unread.
	if ck && !calm {
		return none
	}
	if pk {
		alive := 0
		for _, p := range pawns {
			if dead, known := p.Dead.Value(); known && dead {
				continue
			}
			alive++
			if mental, known := p.Mental.Value(); known && mental {
				return none
			}
			if hasPartyMemory(p) {
				return none
			}
		}
		if alive < GatheringMinColonists {
			return none
		}
	}
	if sk && census.Colony && !hasPartySpot(census) {
		return none
	}
	if !pk || !lk || !bk || !ck || !sk || !census.Colony {
		return domain.Unknown[GatheringPlan]()
	}
	if !gatheringPressure(ledger, benefit) {
		return none
	}
	organizer, ok := gatheringOrganizer(pawns)
	if !ok {
		return none
	}
	return domain.Known(GatheringPlan{Def: PartyGatheringDef, Organizer: organizer})
}

// hasPartyMemory reports whether the pawn carries the AttendedParty memory.
// A pawn whose thoughts are unreadable is not counted either way.
func hasPartyMemory(p MoodPawn) bool {
	rows, known := p.Thoughts.Value()
	if !known {
		return false
	}
	for _, t := range rows {
		if t.Def == PartyThought && t.Offset > 0 {
			return true
		}
	}
	return false
}

func hasPartySpot(c CurrentConstruction) bool {
	for _, b := range c.Buildings {
		if b.Building.Definition() == PartySpotDefinition && len(b.Cells) > 0 {
			return true
		}
	}
	return false
}

// gatheringPressure is whether at least GatheringGuestShare of the ledger's
// colonists lose at least the party's own mood effect. A pawn whose thoughts
// were unreadable is not counted as under pressure.
func gatheringPressure(l MoodLedger, benefit float64) bool {
	if len(l.Pawns) == 0 {
		return false
	}
	pressed := 0
	for _, p := range l.Pawns {
		rows, known := p.Lost.Value()
		if !known {
			continue
		}
		lost := 0.0
		for _, t := range rows {
			lost -= t.Offset
		}
		if lost >= benefit {
			pressed++
		}
	}
	return float64(pressed) >= GatheringGuestShare*float64(len(l.Pawns))
}

// gatheringOrganizer is the colonist best placed to organize: known alive,
// undowned, undrafted and out of any mental state, the highest mood first,
// then the lowest id. The game's PawnCanStartOrContinueGathering decides the
// rest and native refuses otherwise.
func gatheringOrganizer(pawns []MoodPawn) (PawnID, bool) {
	var able []MoodPawn
	for _, p := range pawns {
		if _, known := p.Mood.Value(); !known {
			continue
		}
		ok := true
		for _, f := range []domain.Fact[bool]{p.Dead, p.Downed, p.Drafted, p.Mental} {
			if v, known := f.Value(); !known || v {
				ok = false
			}
		}
		if ok {
			able = append(able, p)
		}
	}
	if len(able) == 0 {
		return "", false
	}
	sort.Slice(able, func(i, j int) bool {
		a, _ := able[i].Mood.Value()
		b, _ := able[j].Mood.Value()
		if a != b {
			return a > b
		}
		return able[i].ID < able[j].ID
	})
	return able[0].ID, true
}

// GatheringRunning reports whether a party started at start may still be
// running at now: the game refuses a second one until its lord job ends, which
// is at most GatheringMaxTicks after it began.
func GatheringRunning(start, now domain.Tick) bool {
	return now >= start && now-start < GatheringMaxTicks
}

// GatheringOwed measures HoldGatherings: known false when no party is due now,
// unknown while its inputs are.
func GatheringOwed(plan domain.Fact[GatheringPlan]) domain.Fact[bool] {
	p, ok := plan.Value()
	if !ok {
		return domain.Unknown[bool]()
	}
	return domain.Known(p.Organizer != "")
}

func inspectGathering(c *roundsRun) error {
	c.owed(HoldGatherings, 3, c.f.GatheringOwed)
	return nil
}
