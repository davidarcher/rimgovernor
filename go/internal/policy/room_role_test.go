package policy

import "testing"

func contrib(role RoomRole, when RoleCondition) RoleContribution {
	s, ok := RoomRoleScore(role, when)
	if !ok {
		panic("no score for " + string(role))
	}
	return s
}

func humanBed(medical bool, baby bool) Station {
	cs := []RoleContribution{
		contrib(RoomRoleBedroom, CondBedFree), contrib(RoomRoleBarracks, CondBedFree),
		contrib(RoomRoleHospital, CondBedMedical),
		contrib(RoomRolePrisonCell, CondBedPrisoner), contrib(RoomRolePrisonCell, CondBedPrisonerMedical),
		contrib(RoomRolePrisonBarracks, CondBedPrisoner), contrib(RoomRolePrisonBarracks, CondBedPrisonerMedical),
	}
	if baby {
		cs = append(cs, contrib(RoomRoleNursery, CondBedFree))
	}
	return Station{Contributions: cs, EmptyCountsForBarracks: true}
}

func roleTestCatalog() StationCatalog {
	return StationCatalog{Stations: map[string]Station{
		"Bed":   humanBed(false, false),
		"Crib":  humanBed(false, true),
		"Stove": {Contributions: []RoleContribution{contrib(RoomRoleKitchen, CondAlways)}, Needs: []EnvNeed{{Kind: NeedRole, Role: RoomRoleKitchen, Factor: 0.8}}},
		"Bench": {Contributions: []RoleContribution{contrib(RoomRoleWorkshop, CondAlways)}, Needs: []EnvNeed{{Kind: NeedRole, Role: RoomRoleWorkshop, Factor: 0.8}}},
		"Table": {Contributions: []RoleContribution{contrib(RoomRoleDiningRoom, CondAlways)}},
		"Shelf": {Contributions: []RoleContribution{contrib(RoomRoleStoreroom, CondAlways)}},
		"Altar": {Contributions: []RoleContribution{contrib(RoomRoleWorshipRoom, CondIdeoAltar)}},
		"Pen":   {Contributions: []RoleContribution{contrib(RoomRoleBarn, CondAlways)}},
	}}
}

func piece(def string, state RoleCondition) RoomPiece { return RoomPiece{Def: def, State: state} }

func TestPredictRole(t *testing.T) {
	cat := roleTestCatalog()
	free := func(n int) []RoomPiece {
		out := make([]RoomPiece, n)
		for i := range out {
			out[i] = piece("Bed", CondBedFree)
		}
		return out
	}
	cases := []struct {
		name   string
		pieces []RoomPiece
		want   RoomRole
	}{
		{"empty room is generic", nil, RoomRoleRoom},
		{"hospital bed in kitchen", []RoomPiece{piece("Stove", ""), piece("Bed", CondBedMedical)}, RoomRoleHospital},
		{"one stove one bench kitchen by a point", []RoomPiece{piece("Stove", ""), piece("Bench", "")}, RoomRoleKitchen},
		{"two benches flip it", []RoomPiece{piece("Stove", ""), piece("Bench", ""), piece("Bench", "")}, RoomRoleWorkshop},
		{"medical plus two normal beds is barracks", append(free(2), piece("Bed", CondBedMedical)), RoomRoleBarracks},
		{"bed in dining room is bedroom", []RoomPiece{piece("Table", ""), piece("Table", ""), piece("Bed", "")}, RoomRoleBedroom},
		{"two unassigned beds are barracks", free(2), RoomRoleBarracks},
		{"storeroom beats generic", []RoomPiece{piece("Shelf", "")}, RoomRoleStoreroom},
		{"one free prisoner bed is a cell even with a stove", []RoomPiece{piece("Stove", ""), piece("Bed", CondBedPrisoner)}, RoomRolePrisonCell},
		{"two prisoner beds are prison barracks", []RoomPiece{piece("Bed", CondBedPrisoner), piece("Bed", CondBedPrisoner)}, RoomRolePrisonBarracks},
		{"prisoner bed beside a colonist bed is neither prison role", []RoomPiece{piece("Bed", CondBedPrisoner), piece("Bed", "")}, RoomRoleRoom},
		{"two cribs are a nursery", []RoomPiece{piece("Crib", ""), piece("Crib", "")}, RoomRoleNursery},
		{"one crib is not", []RoomPiece{piece("Crib", "")}, RoomRoleBedroom},
		{"crib among adult beds is barracks", []RoomPiece{piece("Crib", ""), piece("Crib", ""), piece("Bed", "")}, RoomRoleBarracks},
		{"altar with meme", []RoomPiece{piece("Altar", CondIdeoAltar)}, RoomRoleWorshipRoom},
		{"altar without meme", []RoomPiece{piece("Altar", "")}, RoomRoleRoom},
		{"animal bed is barn", []RoomPiece{piece("Pen", "")}, RoomRoleBarn},
	}
	for _, c := range cases {
		got, err := PredictRole(cat, c.pieces)
		if err != nil {
			t.Fatalf("%s: %v", c.name, err)
		}
		if got.Role != c.want {
			t.Errorf("%s: role = %s (%v), want %s", c.name, got.Role, got.Scores, c.want)
		}
	}
	if _, err := PredictRole(cat, []RoomPiece{piece("Nope", "")}); err == nil {
		t.Error("unknown def predicted without error")
	}
	if _, err := PredictRole(cat, []RoomPiece{piece("Bed", CondIdeoAltar)}); err == nil {
		t.Error("bed with an altar state accepted")
	}
}

func TestPredictRoleBedroomOwners(t *testing.T) {
	cat := roleTestCatalog()
	adult := func(p string, cluster ...string) BedOwner { return BedOwner{Pawn: p, Cluster: cluster} }
	bedOf := func(o ...BedOwner) RoomPiece { return RoomPiece{Def: "Bed", Owners: o} }
	a, b, c := adult("a", "a", "b"), adult("b", "a", "b"), adult("c", "c")
	kid := BedOwner{Pawn: "k", Juvenile: true, Parents: []string{"a"}}
	stranger := BedOwner{Pawn: "s", Juvenile: true, Parents: []string{"x"}}
	cases := []struct {
		name   string
		pieces []RoomPiece
		want   RoomRole
	}{
		{"couple in two beds", []RoomPiece{bedOf(a), bedOf(b)}, RoomRoleBedroom},
		{"couple with own child", []RoomPiece{bedOf(a), bedOf(b), bedOf(kid)}, RoomRoleBedroom},
		{"couple with stranger's child", []RoomPiece{bedOf(a), bedOf(b), bedOf(stranger)}, RoomRoleBarracks},
		{"unrelated adults", []RoomPiece{bedOf(a), bedOf(c)}, RoomRoleBarracks},
		{"assigned bed beside empty bed", []RoomPiece{bedOf(a), {Def: "Bed"}}, RoomRoleBarracks},
	}
	for _, tc := range cases {
		got, err := PredictRole(cat, tc.pieces)
		if err != nil {
			t.Fatal(err)
		}
		if got.Role != tc.want {
			t.Errorf("%s: role = %s (%v), want %s", tc.name, got.Role, got.Scores, tc.want)
		}
	}
}

func TestLostEffects(t *testing.T) {
	cat := roleTestCatalog()
	got, err := LostEffects(cat, RoomRoleKitchen, []RoomPiece{piece("Stove", ""), piece("Bed", CondBedMedical)})
	if err != nil {
		t.Fatal(err)
	}
	want := []LostEffect{{Effect: EffectBenchSpeed, Role: RoomRoleKitchen, Def: "Stove", Factor: 0.8}}
	if len(got) != 1 || got[0] != want[0] {
		t.Errorf("hospital bed in kitchen lost %v, want %v", got, want)
	}
	// An intended bedroom that scores barracks loses its sleep mood.
	got, err = LostEffects(cat, RoomRoleBedroom, []RoomPiece{piece("Bed", ""), piece("Bed", "")})
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 1 || got[0].Effect != EffectSleepMood || got[0].Role != RoomRoleBedroom {
		t.Errorf("bedroom scored barracks lost %v", got)
	}
	// A bench in its own role, a role with no consumer: nothing lost.
	for _, c := range []struct {
		intended RoomRole
		pieces   []RoomPiece
	}{
		{RoomRoleKitchen, []RoomPiece{piece("Stove", ""), piece("Shelf", "")}},
		{RoomRoleDiningRoom, []RoomPiece{piece("Table", ""), piece("Shelf", ""), piece("Shelf", "")}},
		{RoomRoleBedroom, []RoomPiece{piece("Bed", ""), piece("Table", "")}},
	} {
		got, err := LostEffects(cat, c.intended, c.pieces)
		if err != nil || len(got) != 0 {
			t.Errorf("%s lost %v (%v)", c.intended, got, err)
		}
	}
}
