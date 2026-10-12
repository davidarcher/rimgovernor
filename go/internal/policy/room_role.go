package policy

import (
	"fmt"
	"slices"
	"sort"
)

// RoomPiece is one station standing in a room, as the role workers see it:
// its definition and the instance state the definition cannot decide.
type RoomPiece struct {
	Def string
	// State is the instance state a role worker tests: for a humanlike bed
	// CondBedFree (also the meaning of ""), CondBedMedical, CondBedPrisoner
	// or CondBedPrisonerMedical; CondIdeoAltar for an altar that carries an
	// ideo structure meme. Other stations leave it CondAlways.
	State RoleCondition
	// Owners are the pawns a bed is assigned to; empty for an unassigned bed.
	Owners []BedOwner
}

// BedOwner is what RoomRoleWorker_Bedroom.IsBedroom reads of a bed's owner.
type BedOwner struct {
	Pawn     string
	Juvenile bool
	// Cluster is the pawn's love cluster (Pawn.GetLoveCluster), the pawn
	// included; read only for adults.
	Cluster []string
	// Parents are the pawn's mother and father, read only for juveniles.
	Parents []string
}

// RoleEffect names a gameplay effect a native room role carries.
type RoleEffect string

const (
	// EffectSleepMood: the sleeping mood and room ownership of a bedroom or
	// barracks.
	EffectSleepMood RoleEffect = "sleep_mood"
	// EffectPatientMood: the hospital's patient mood.
	EffectPatientMood RoleEffect = "patient_mood"
	// EffectPrisonMood: the prison's prisoner mood.
	EffectPrisonMood RoleEffect = "prison_mood"
	// EffectThrone: the throne room's title requirement.
	EffectThrone RoleEffect = "throne"
	// EffectWorship: the worship room's ritual requirement.
	EffectWorship RoleEffect = "worship"
	// EffectBenchSpeed: a work table's rate times its NotInRoomRoleFactor
	// (StatPart_WorkTableRoomRole).
	EffectBenchSpeed RoleEffect = "bench_speed"
)

// roleEffects are the roles that carry a gameplay effect in the game, found by
// reading every consumer of Room.Role in the decompiled 1.6 (mood thought
// stages, room ownership, title and ritual requirements). The game offers no
// list of consumers, so this is a DefTables entry; every other role (dining,
// recreation, storeroom, barn, nursery, classroom, playroom, tomb, ...) has
// no native gameplay consumer and is ignored. The workbench penalty is not
// here: it comes from each bench's own NeedRole.
var roleEffects = map[RoomRole]RoleEffect{
	RoomRoleBedroom:        EffectSleepMood,
	RoomRoleBarracks:       EffectSleepMood,
	RoomRoleHospital:       EffectPatientMood,
	RoomRolePrisonCell:     EffectPrisonMood,
	RoomRolePrisonBarracks: EffectPrisonMood,
	RoomRoleThroneRoom:     EffectThrone,
	RoomRoleWorshipRoom:    EffectWorship,
}

// genericRoleScore is the generic Room role's constant score
// (RoomRoleWorker_Room.GetScore): any role scoring above it wins.
const genericRoleScore = 0.99

// LostEffect is a role effect a room does not get.
type LostEffect struct {
	Effect RoleEffect
	// Role is the role the effect needs.
	Role RoomRole
	// Got is the role the room is scored as instead (the mood effects only).
	Got RoomRole
	// Def is the bench that is slowed (EffectBenchSpeed only).
	Def string
	// Factor is the rate multiplier applied (EffectBenchSpeed only).
	Factor float64
}

// RolePrediction is a room's predicted native role and every role's score.
type RolePrediction struct {
	Role   RoomRole
	Scores map[RoomRole]float64
}

// PredictRole mirrors Room.UpdateRoomStatsAndRole for a proper room holding
// pieces: the role whose worker scores highest wins and the generic Room
// scores 0.99. Weights and which stations earn them come from the catalog
// (RoomRoleWorker.GetScore per building); the bed roles, whose workers test
// the whole set of beds, follow their decompiled logic below. A tie breaks
// to the lexically smaller role name: the game takes the first role in its
// def order, which the catalog does not carry. A piece whose definition is
// not in the catalog is an error, never a silent zero.
func PredictRole(catalog StationCatalog, pieces []RoomPiece) (RolePrediction, error) {
	beds, rest, err := splitBeds(catalog, pieces)
	if err != nil {
		return RolePrediction{}, err
	}
	scores := map[RoomRole]float64{RoomRoleRoom: genericRoleScore}
	add := map[RoomRole]float64{}
	flat := map[RoomRole]float64{}
	floor := map[RoomRole]float64{}
	for _, p := range rest {
		for _, c := range catalog.Stations[p.Def].Contributions {
			if !contributes(c, p) {
				continue
			}
			if c.Additive {
				add[c.Role] += c.Weight
				floor[c.Role] = max(floor[c.Role], c.Floor)
			} else {
				flat[c.Role] = max(flat[c.Role], c.Weight)
			}
		}
	}
	for role, v := range add {
		scores[role] = max(v, floor[role])
	}
	for role, v := range flat {
		scores[role] = max(scores[role], v)
	}
	for role, v := range bedScores(beds) {
		scores[role] = v
	}
	best, bestScore := RoomRoleRoom, scores[RoomRoleRoom]
	roles := make([]RoomRole, 0, len(scores))
	for role := range scores {
		roles = append(roles, role)
	}
	slices.Sort(roles)
	for _, role := range roles {
		if scores[role] > bestScore {
			best, bestScore = role, scores[role]
		}
	}
	return RolePrediction{Role: best, Scores: scores}, nil
}

// contributes reports whether a piece earns a contribution: the definition
// alone for CondAlways and CondBindable (a trait of the def), else the
// piece's instance state.
func contributes(c RoleContribution, p RoomPiece) bool {
	switch c.When {
	case CondAlways, CondBindable:
		return true
	}
	return p.State == c.When
}

// bed is a humanlike bed as the bed role workers see it.
type bed struct {
	owners []BedOwner
	// counts is bed_countsForBedroomOrBarracks, baby is a bed too small for a
	// child (nursery), emptyCounts is bed_emptyCountsForBarracks.
	counts, baby, emptyCounts bool
	medical, prisoner         bool
}

// splitBeds separates the humanlike beds, which the bed roles score as a set,
// from every other piece. An animal bed contributes through the catalog
// (Barn) like any other piece.
func splitBeds(catalog StationCatalog, pieces []RoomPiece) ([]bed, []RoomPiece, error) {
	var beds []bed
	var rest []RoomPiece
	for _, p := range pieces {
		st, ok := catalog.Stations[p.Def]
		if !ok {
			return nil, nil, fmt.Errorf("room piece %q is not in the station catalog", p.Def)
		}
		if !humanlikeBed(st) {
			rest = append(rest, p)
			continue
		}
		b := bed{owners: p.Owners, emptyCounts: st.EmptyCountsForBarracks}
		switch p.State {
		case CondAlways, CondBedFree:
		case CondBedMedical:
			b.medical = true
		case CondBedPrisoner:
			b.prisoner = true
		case CondBedPrisonerMedical:
			b.medical, b.prisoner = true, true
		default:
			return nil, nil, fmt.Errorf("bed %q has state %q, not a bed state", p.Def, p.State)
		}
		for _, c := range st.Contributions {
			switch c.Role {
			case RoomRoleBedroom:
				b.counts = true
			case RoomRoleNursery:
				b.baby = true
			}
		}
		beds = append(beds, b)
	}
	return beds, rest, nil
}

// humanlikeBed: the catalog gives a humanlike bed the hospital and prison
// contributions and no other building those.
func humanlikeBed(st Station) bool {
	for _, c := range st.Contributions {
		if c.When == CondBedMedical || c.When == CondBedPrisoner {
			return true
		}
	}
	return false
}

// bedScores are the six bed-set workers (Bedroom, Barracks, Nursery,
// Hospital, PrisonCell, PrisonBarracks), each as its GetScore decompiles. The
// hospital worker stops at the first medical bed and so, scanning in an
// order the room does not give, a prisoner bed after it goes unseen; here
// any prisoner bed vetoes the hospital.
func bedScores(beds []bed) map[RoomRole]float64 {
	out := map[RoomRole]float64{}
	if len(beds) == 0 {
		return out
	}
	// Bedroom and Barracks read only beds that count for them.
	var counted []bed
	bedroomVeto, barracksVeto, free := false, false, 0
	for _, b := range beds {
		if !b.counts {
			continue
		}
		counted = append(counted, b)
		bedroomVeto = bedroomVeto || b.medical || b.prisoner
		barracksVeto = barracksVeto || b.prisoner
		if !b.medical {
			free++
		}
	}
	if len(counted) > 0 {
		single := isBedroom(counted)
		if !bedroomVeto && single {
			out[RoomRoleBedroom] = 100000
		}
		if !barracksVeto && !single {
			out[RoomRoleBarracks] = float64(free) * 100100
		}
	}
	hospital, anyPrisoner, nonPrisoner := false, false, false
	var cell, medicalCell int
	for _, b := range beds {
		anyPrisoner = anyPrisoner || b.prisoner
		nonPrisoner = nonPrisoner || !b.prisoner
		hospital = hospital || b.medical && !b.prisoner
		if b.prisoner && b.medical {
			medicalCell++
		} else if b.prisoner {
			cell++
		}
	}
	if hospital && !anyPrisoner {
		out[RoomRoleHospital] = 100000
	}
	if !nonPrisoner {
		switch {
		case cell == 1:
			out[RoomRolePrisonCell] = 170000
		case medicalCell == 1:
			out[RoomRolePrisonCell] = 100000
		}
		if cell+medicalCell > 1 {
			out[RoomRolePrisonBarracks] = float64(cell)*100100 + float64(medicalCell)*50001
		}
	}
	// Nursery: only baby beds, no prisoner bed, at least two (MinBabyBeds).
	babies, nursery := 0, true
	for _, b := range beds {
		if b.medical {
			continue
		}
		if b.prisoner || !b.baby {
			nursery = false
			break
		}
		babies++
	}
	if nursery && babies >= 2 {
		out[RoomRoleNursery] = float64(babies) * 100200
	}
	return out
}

// isBedroom is RoomRoleWorker_Bedroom.IsBedroom: one unassigned bed, one
// assigned bed, or owners that are one love cluster of adults with only their
// own children.
func isBedroom(beds []bed) bool {
	var adults, children []BedOwner
	var cluster []string
	empty, assigned, outsiders := 0, 0, 0
	for _, b := range beds {
		if len(b.owners) == 0 && b.emptyCounts {
			empty++
			continue
		}
		if len(b.owners) > 0 {
			assigned++
		}
		outsider := false
		for _, o := range b.owners {
			if o.Juvenile {
				children = append(children, o)
				continue
			}
			adults = append(adults, o)
			if cluster == nil {
				cluster = o.Cluster
			}
			if !slices.Contains(cluster, o.Pawn) {
				outsider = true
			}
		}
		if outsider {
			outsiders++
		}
	}
	switch {
	case empty == 1 && assigned == 0, empty == 0 && assigned == 1:
		return true
	case empty > 0:
		return false
	case len(adults) == 0:
		return true
	case outsiders > 0:
		return false
	}
	for _, child := range children {
		if !slices.ContainsFunc(adults, func(a BedOwner) bool { return slices.Contains(child.Parents, a.Pawn) }) {
			return false
		}
	}
	return true
}

// LostEffects are the role effects a room holding contents does not get when
// the game scores it other than intended: the intended role's own effect
// (sleep mood, patient mood, prison mood, throne, worship) when the predicted
// role differs, and every bench whose NeedRole names a role other than the
// predicted one, at that need's factor (the x0.8 workbench penalty). An
// intended role with no native gameplay consumer loses nothing by itself.
func LostEffects(catalog StationCatalog, intended RoomRole, pieces []RoomPiece) ([]LostEffect, error) {
	pred, err := PredictRole(catalog, pieces)
	if err != nil {
		return nil, err
	}
	var out []LostEffect
	if effect, ok := roleEffects[intended]; ok && pred.Role != intended {
		out = append(out, LostEffect{Effect: effect, Role: intended, Got: pred.Role})
	}
	for _, p := range pieces {
		for _, n := range catalog.Stations[p.Def].Needs {
			if n.Kind == NeedRole && n.Role != pred.Role {
				out = append(out, LostEffect{Effect: EffectBenchSpeed, Role: n.Role, Def: p.Def, Factor: n.Factor})
			}
		}
	}
	return out, nil
}

func roleEffectRoles() []string {
	out := make([]string, 0, len(roleEffects))
	for role := range roleEffects {
		out = append(out, string(role))
	}
	sort.Strings(out)
	return out
}
