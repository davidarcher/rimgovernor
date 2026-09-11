package policy

import (
	"errors"
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type PawnID string
type EmergencyPawn struct {
	ID                                PawnID
	Dead, Downed, Bleeding, NeedsTend domain.Fact[bool]
}
type ThreatKind uint8

const (
	Hostile ThreatKind = iota + 1
	HuntingPredator
	IgnoredHunter
	NearbyPredator
	NearbyDowned
)

type EmergencyThreat struct {
	ID           PawnID
	Kind         ThreatKind
	Dead, Downed domain.Fact[bool]
}
type EmergencyFacts struct {
	ColonistsComplete, ThreatsComplete domain.Fact[bool]
	Colonists                          []EmergencyPawn
	Threats                            []EmergencyThreat
}

// EmergencySnapshot owns its inputs and carries no mutation or authority API.
type EmergencySnapshot struct {
	current domain.GenerationSnapshot
	tick    domain.Tick
	facts   EmergencyFacts
	valid   bool
}
type EmergencyReason string

const (
	EmergencyUnknownFacts    EmergencyReason = "unknown_facts"
	EmergencyUnsafeThreat    EmergencyReason = "unsafe_threat"
	EmergencyCriticalMedical EmergencyReason = "critical_medical"
	EmergencyStaleFacts      EmergencyReason = "stale_facts"
)

type EmergencyHold struct {
	Reason EmergencyReason
	Pawn   PawnID
}
type EmergencyDecision struct {
	Clear bool
	Holds []EmergencyHold
}

func NewEmergencySnapshot(current domain.GenerationSnapshot, tick domain.Tick, facts EmergencyFacts) (EmergencySnapshot, error) {
	if err := current.Validate(); err != nil {
		return EmergencySnapshot{}, err
	}
	if tick < 0 || len(facts.Colonists) > 256 || len(facts.Threats) > 256 {
		return EmergencySnapshot{}, errors.New("invalid emergency tick or census size")
	}
	validID := func(id PawnID) bool {
		return utf8.ValidString(string(id)) && len(id) <= 256 && strings.TrimSpace(string(id)) != "" && !strings.ContainsRune(string(id), 0)
	}
	people := map[PawnID]bool{}
	for _, pawn := range facts.Colonists {
		if !validID(pawn.ID) || people[pawn.ID] {
			return EmergencySnapshot{}, errors.New("invalid or duplicate emergency pawn")
		}
		people[pawn.ID] = true
	}
	seen := map[struct {
		id   PawnID
		kind ThreatKind
	}]bool{}
	for _, threat := range facts.Threats {
		key := struct {
			id   PawnID
			kind ThreatKind
		}{threat.ID, threat.Kind}
		if !validID(threat.ID) || threat.Kind < Hostile || threat.Kind > NearbyDowned || seen[key] {
			return EmergencySnapshot{}, errors.New("invalid or duplicate emergency threat")
		}
		seen[key] = true
	}
	facts.Colonists = append([]EmergencyPawn(nil), facts.Colonists...)
	facts.Threats = append([]EmergencyThreat(nil), facts.Threats...)
	return EmergencySnapshot{current: current, tick: tick, facts: facts, valid: true}, nil
}

// EvaluateEmergency clears only known complete, current facts. It does not decide
// how to treat, fight or advance time, and never grants execution authority.
func EvaluateEmergency(snapshot EmergencySnapshot, current domain.GenerationSnapshot, minimumTick domain.Tick) EmergencyDecision {
	if !snapshot.valid || current.Validate() != nil || !snapshot.current.Matches(current) || minimumTick < 0 || snapshot.tick < minimumTick {
		return EmergencyDecision{Holds: []EmergencyHold{{Reason: EmergencyStaleFacts}}}
	}
	holds := map[EmergencyHold]bool{}
	hold := func(reason EmergencyReason, pawn PawnID) { holds[EmergencyHold{reason, pawn}] = true }
	for _, complete := range []domain.Fact[bool]{snapshot.facts.ColonistsComplete, snapshot.facts.ThreatsComplete} {
		if yes, known := complete.Value(); !known || !yes {
			hold(EmergencyUnknownFacts, "")
		}
	}
	// A pawn may occur in several native categories; disagreeing known status is
	// uncertainty, not permission to choose whichever category appears safest.
	type status struct{ dead, downed domain.Fact[bool] }
	statuses := map[PawnID]status{}
	merge := func(id PawnID, dead, downed domain.Fact[bool]) {
		prior := statuses[id]
		combine := func(old, new domain.Fact[bool]) domain.Fact[bool] {
			a, ak := old.Value()
			b, bk := new.Value()
			if ak && bk && a != b {
				hold(EmergencyUnknownFacts, id)
			}
			if ak {
				return old
			}
			return new
		}
		statuses[id] = status{combine(prior.dead, dead), combine(prior.downed, downed)}
	}
	for _, pawn := range snapshot.facts.Colonists {
		merge(pawn.ID, pawn.Dead, pawn.Downed)
		dead, known := pawn.Dead.Value()
		if dead && known {
			continue
		}
		if !known {
			hold(EmergencyUnknownFacts, pawn.ID)
		}
		for _, health := range []domain.Fact[bool]{pawn.Downed, pawn.Bleeding, pawn.NeedsTend} {
			bad, known := health.Value()
			if !known {
				hold(EmergencyUnknownFacts, pawn.ID)
			} else if bad {
				hold(EmergencyCriticalMedical, pawn.ID)
			}
		}
	}
	for _, threat := range snapshot.facts.Threats {
		merge(threat.ID, threat.Dead, threat.Downed)
		dead, deadKnown := threat.Dead.Value()
		downed, downedKnown := threat.Downed.Value()
		if (deadKnown && dead) || (downedKnown && downed) {
			continue
		}
		if !deadKnown || !downedKnown {
			hold(EmergencyUnknownFacts, threat.ID)
		}
		if threat.Kind == Hostile || threat.Kind == HuntingPredator {
			hold(EmergencyUnsafeThreat, threat.ID)
		}
	}
	result := EmergencyDecision{Clear: len(holds) == 0, Holds: make([]EmergencyHold, 0, len(holds))}
	for h := range holds {
		result.Holds = append(result.Holds, h)
	}
	sort.Slice(result.Holds, func(i, j int) bool {
		a, b := result.Holds[i], result.Holds[j]
		if a.Reason != b.Reason {
			return a.Reason < b.Reason
		}
		return a.Pawn < b.Pawn
	})
	return result
}
