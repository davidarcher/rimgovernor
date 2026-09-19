package policy

import (
	"errors"
	"math"
	"slices"
)

// RecreationCensus contains native building-backed kinds and the native
// tolerance/bored state for each colonist with a joy need. Vectors follow Kinds.
// Nil means unavailable, never a complete empty census.
type RecreationCensus struct {
	Kinds   []string
	Pawns   []JoyTolerance
	Methods []JoyBuildingMethod
}

type JoyTolerance struct {
	Pawn      PawnID
	Tolerance []float64
	Bored     []bool
}

type JoyBuildingMethod struct {
	Definition, Kind string
	PowerW           float64
}

var RecreationDefinitions = []string{"TubeTelevision", "BilliardsTable", "ChessTable", "HorseshoesPin"}

func (v ComfortObservation) validateJoy() error {
	j := v.Joy
	if j == nil {
		return nil
	}
	bad := func() error { return errors.New("invalid recreation kind census") }
	if len(j.Kinds) > 16 || len(j.Pawns) > 256 || len(j.Pawns)*len(j.Kinds) > 2048 || len(j.Methods) > 4 {
		return bad()
	}
	kinds := map[string]bool{}
	for _, k := range j.Kinds {
		if !foodID(k) || kinds[k] {
			return bad()
		}
		kinds[k] = true
	}
	available := map[string]bool{}
	for _, f := range v.Recreation {
		if !kinds[f.Kind] {
			return bad()
		}
		available[f.Kind] = true
	}
	if len(available) != len(kinds) {
		return bad()
	}
	pawns := map[PawnID]bool{}
	for _, p := range j.Pawns {
		if !slices.Contains(v.People, p.Pawn) || pawns[p.Pawn] || len(p.Tolerance) != len(j.Kinds) || len(p.Bored) != len(j.Kinds) {
			return bad()
		}
		pawns[p.Pawn] = true
		for _, t := range p.Tolerance {
			if math.IsNaN(t) || math.IsInf(t, 0) || t < 0 || t > 1 {
				return bad()
			}
		}
	}
	methods := map[string]bool{}
	for _, m := range j.Methods {
		if !slices.Contains(RecreationDefinitions, m.Definition) || methods[m.Definition] || !foodID(m.Kind) || math.IsNaN(m.PowerW) || math.IsInf(m.PowerW, 0) || m.PowerW < 0 {
			return bad()
		}
		methods[m.Definition] = true
	}
	return nil
}

// A second reachable kind is maintenance for a multi-colonist colony, or
// for a lone colonist bored with their only kind. Two kinds cap this slice:
// tolerance must not cause unbounded construction when all kinds are bored.
func recreationVarietyMissing(v ComfortObservation) int {
	if v.Joy == nil {
		return 0
	}
	missing := 0
	for _, p := range v.Joy.Pawns {
		kinds := map[string]bool{}
		for _, f := range v.Recreation {
			if slices.Contains(f.AccessibleTo, p.Pawn) {
				kinds[f.Kind] = true
			}
		}
		bored := len(kinds) > 0
		for i, k := range v.Joy.Kinds {
			if kinds[k] && !p.Bored[i] {
				bored = false
			}
		}
		if len(kinds) < 2 && (len(v.Joy.Pawns) > 1 || bored) {
			missing++
		}
	}
	return missing
}

// SelectRecreationVariety only adds a distinct kind. An already standing but
// inaccessible second kind is an access blocker, not permission to duplicate it.
// Native definitions supply kinds, research availability and power demand.
func SelectRecreationVariety(v ComfortObservation, available func(JoyBuildingMethod) bool) ComfortMethod {
	if v.Joy == nil {
		return ComfortNoMethod
	}
	if len(v.Joy.Kinds) >= 2 {
		return ComfortAccessBlocked
	}
	for _, definition := range RecreationDefinitions {
		for _, m := range v.Joy.Methods {
			if m.Definition == definition && !slices.Contains(v.Joy.Kinds, m.Kind) && available(m) {
				return ComfortMethod(m.Definition)
			}
		}
	}
	return ComfortAccessBlocked
}
