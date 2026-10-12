package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ObservedRoomContents builds GuardNativeRoles' input from what the colony
// observes: for each planned room standing enclosed on its region, the
// catalog stations among the buildings with a cell in the census room, each
// humanlike bed carrying the state and owners the sleeping census gives it.
//
// A room is left out, never guessed, when it holds a bed the sleeping census
// does not name or whose medical or prisoner state is unknown, or a station
// whose role contribution turns on an ideo altar's memes (the census does not
// read them). The census carries no pawn age or parents, so a bed owner is
// read as an adult whose love cluster is the closure of the partner
// relations; a child owner can make a bedroom read as a mismatch.
func ObservedRoomContents(catalog StationCatalog, plan LayoutPlan, rooms RoomObservation, buildings []CurrentBuilding, sleeping SleepingObservation) []RoomContents {
	beds := map[string]SleepingBed{}
	for _, b := range sleeping.Beds {
		beds[b.ID] = b
	}
	clusters := loveClusters(sleeping)
	var out []RoomContents
	for _, planned := range plan.AllRooms() {
		census, standing := CensusRoomIn(planned, rooms)
		if !standing {
			continue
		}
		inRoom := map[domain.Cell]bool{}
		for _, c := range census.Cells {
			inRoom[c] = true
		}
		var pieces []RoomPiece
		known := true
		for _, b := range buildings {
			def := b.Building.Definition()
			st, isStation := catalog.Stations[def]
			if !isStation || !slices.ContainsFunc(b.Cells, func(c domain.Cell) bool { return inRoom[c] }) {
				continue
			}
			if slices.ContainsFunc(st.Contributions, func(c RoleContribution) bool { return c.When == CondIdeoAltar }) {
				known = false
				break
			}
			piece := RoomPiece{Def: def}
			if humanlikeBed(st) {
				bed, ok := beds[b.ID]
				state, stateKnown := bedPieceState(bed)
				if !ok || !stateKnown {
					known = false
					break
				}
				piece.State = state
				for _, o := range bed.Owners {
					cluster := clusters[o]
					if cluster == nil {
						cluster = []string{string(o)}
					}
					piece.Owners = append(piece.Owners, BedOwner{Pawn: string(o), Cluster: cluster})
				}
			}
			pieces = append(pieces, piece)
		}
		if known {
			out = append(out, RoomContents{Room: planned.ID, Pieces: pieces})
		}
	}
	return out
}

// bedPieceState is the RoomPiece state of a humanlike bed; false while its
// medical or prisoner flag is unknown.
func bedPieceState(b SleepingBed) (RoleCondition, bool) {
	medical, mk := b.Medical.Value()
	prisoner, pk := b.Prisoners.Value()
	switch {
	case !mk || !pk:
		return CondAlways, false
	case medical && prisoner:
		return CondBedPrisonerMedical, true
	case medical:
		return CondBedMedical, true
	case prisoner:
		return CondBedPrisoner, true
	}
	return CondBedFree, true
}

// loveClusters maps each pawn with a partner to its love cluster
// (Pawn.GetLoveCluster): the pawn and everyone reachable through partner
// relations, sorted. A pawn with no partner is absent.
func loveClusters(sleeping SleepingObservation) map[PawnID][]string {
	adj := map[PawnID][]PawnID{}
	for _, group := range [][]SleepingPerson{sleeping.People, sleeping.Slaves, sleeping.Guests} {
		for _, p := range group {
			for _, q := range p.Partners {
				adj[p.ID] = append(adj[p.ID], q)
				adj[q] = append(adj[q], p.ID)
			}
		}
	}
	out := map[PawnID][]string{}
	for start := range adj {
		if _, done := out[start]; done {
			continue
		}
		seen := map[PawnID]bool{start: true}
		stack := []PawnID{start}
		for len(stack) > 0 {
			p := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			for _, q := range adj[p] {
				if !seen[q] {
					seen[q] = true
					stack = append(stack, q)
				}
			}
		}
		cluster := make([]string, 0, len(seen))
		for p := range seen {
			cluster = append(cluster, string(p))
		}
		slices.Sort(cluster)
		for p := range seen {
			out[p] = cluster
		}
	}
	return out
}
