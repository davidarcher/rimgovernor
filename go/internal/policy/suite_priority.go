package policy

import "sort"

// Suite priority: the suite queue is served most
// unhappy first. A claim's pressure is the current summed negative mood
// offset of its pawn's bedroom, space and jealousy thoughts; no history is
// kept. Thoughts only order the queue: which pawns claim a suite and the
// size each needs stay SuiteClaims' trait targets.

// suitePressureThoughts are the vanilla ThoughtDef defNames a suite
// relieves (confirmed against Assembly-CSharp ThoughtDefOf and the
// Core/Royalty ThoughtDefs XML).
var suitePressureThoughts = map[string]bool{
	"SleptInBedroom":                 true, // bedroom impressiveness memory (awful/dull stages)
	"SleptInBarracks":                true, // slept in barracks
	"NeedRoomSize":                   true, // ThoughtWorker_NeedRoomSize: confined/cramped interior
	"Jealous":                        true, // ThoughtWorker_BedroomJealous
	"Greedy":                         true, // ThoughtWorker_Greedy: bedroom impressiveness
	"TitleBedroomRequirementsNotMet": true, // Royalty
	"TitleNoPersonalBedroom":         true, // Royalty
	"SharedBed":                      true, // couple made to share a bed (ThoughtDefOf.SharedBed)
}

// SuitePressure is each pawn's summed negative offset of the
// suitePressureThoughts (0 or less); a pawn whose thoughts are unknown is
// left out.
func SuitePressure(pawns []MoodPawn) map[PawnID]float64 {
	out := map[PawnID]float64{}
	for _, p := range pawns {
		thoughts, known := p.Thoughts.Value()
		if !known {
			continue
		}
		sum := 0.0
		for _, t := range thoughts {
			if suitePressureThoughts[t.Def] && t.Offset < 0 {
				sum += t.Offset
			}
		}
		out[p.ID] = sum
	}
	return out
}

// orderSuiteClaims sorts claims most negative pressure first, pawn id
// breaking ties.
func orderSuiteClaims(claims []SuiteClaim, pressure map[PawnID]float64) {
	sort.Slice(claims, func(i, j int) bool {
		a, b := pressure[claims[i].Pawn], pressure[claims[j].Pawn]
		if a != b {
			return a < b
		}
		return claims[i].Pawn < claims[j].Pawn
	})
}
