package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func hospitalPatients(ids ...PawnID) domain.Fact[[]CarePawn] {
	rows := []CarePawn{}
	for _, id := range ids {
		rows = append(rows, CarePawn{ID: id, Dead: domain.Known(false), NeedsRest: domain.Known(true), NeedsTend: domain.Known(true), BadConditions: domain.Known(true)})
	}
	return domain.Known(rows)
}

func hospitalBed(id string, medical bool, owners ...PawnID) SleepingBed {
	return SleepingBed{ID: id, Definition: "SleepingSpot", Humanlike: domain.Known(true), Medical: domain.Known(medical), Prisoners: domain.Known(false), Owners: owners}
}

func hospitalRooms(role RoomRole, beds ...string) domain.Fact[RoomObservation] {
	return domain.Known(RoomObservation{Rooms: []Room{{ID: "room", Role: domain.Known(role), Beds: beds}}})
}

func TestHospitalIsHostedByBedroomsAndBarracks(t *testing.T) {
	f, err := Facility(RoomRoleHospital)
	if err != nil || f.Status != FacilityImplemented {
		t.Fatal(f, err)
	}
	for _, role := range []RoomRole{RoomRoleHospital, RoomRoleBedroom, RoomRoleBarracks, RoomRoleRoom} {
		if !f.Hosts(role) {
			t.Fatal("expected hosting role", role)
		}
	}
	if f.Hosts(RoomRoleKitchen) || f.Hosts(RoomRolePrisonCell) {
		t.Fatal("kitchen or prison cell hosts a hospital bed")
	}
}

func TestSelectHospitalBedNoDemandAndExisting(t *testing.T) {
	// A bad condition that needs no medical rest (a scar) keeps
	// MaintainMedicalReserves's deficit but asks for no ward.
	healthy := domain.Known([]CarePawn{{ID: "a", Dead: domain.Known(false), NeedsRest: domain.Known(false), NeedsTend: domain.Known(false), BadConditions: domain.Known(true)}})
	choice, err := SelectHospitalBed(HospitalRequest{Patients: healthy})
	if err != nil || choice.Method != HospitalNoDemand {
		t.Fatal(choice, err)
	}
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: domain.Known(SleepingObservation{Beds: []SleepingBed{hospitalBed("bed1", true)}}), Rooms: hospitalRooms(RoomRoleBarracks, "bed1")})
	if err != nil || choice.Method != HospitalExisting || choice.Needed != 1 {
		t.Fatal(choice, err)
	}
	// A medical bed outside every hosting room does not count.
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: domain.Known(SleepingObservation{Beds: []SleepingBed{hospitalBed("bed1", true)}}), Rooms: hospitalRooms(RoomRoleKitchen, "bed1"), Definitions: []BenchDefinition{{Name: "Bed", Available: domain.Known(true)}}})
	if err != nil || choice.Method != HospitalBuild || choice.Definition != "Bed" {
		t.Fatal(choice, err)
	}
}

func TestSelectHospitalBedConvertPrefersSpareThenPatientOwned(t *testing.T) {
	beds := domain.Known(SleepingObservation{Beds: []SleepingBed{hospitalBed("bed3", false, "b"), hospitalBed("bed2", false), hospitalBed("bed1", false, "a")}})
	rooms := hospitalRooms(RoomRoleBarracks, "bed1", "bed2", "bed3")
	choice, err := SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: beds, Rooms: rooms})
	if err != nil || choice.Method != HospitalConvert || choice.Bed != "bed2" {
		t.Fatal(choice, err)
	}
	beds = domain.Known(SleepingObservation{Beds: []SleepingBed{hospitalBed("bed3", false, "b"), hospitalBed("bed1", false, "a")}})
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: beds, Rooms: rooms})
	if err != nil || choice.Method != HospitalConvert || choice.Bed != "bed1" {
		t.Fatal(choice, err)
	}
	// Only a healthy colonist's bed remains: build rather than evict.
	beds = domain.Known(SleepingObservation{Beds: []SleepingBed{hospitalBed("bed3", false, "b")}})
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: beds, Rooms: rooms, Definitions: []BenchDefinition{{Name: "Bed", Available: domain.Known(false)}, {Name: "SleepingSpot", Available: domain.Known(true)}}})
	if err != nil || choice.Method != HospitalBuild || choice.Definition != "SleepingSpot" {
		t.Fatal(choice, err)
	}
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: beds, Rooms: rooms, Definitions: []BenchDefinition{{Name: "Bed", Available: domain.Known(false)}, {Name: "SleepingSpot", Available: domain.Known(false)}}})
	if err != nil || choice.Method != HospitalUnavailable {
		t.Fatal(choice, err)
	}
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: beds, Rooms: rooms})
	if err != nil || choice.Method != HospitalUnknown {
		t.Fatal(choice, err)
	}
}

func TestSelectHospitalBedRefusesConversionAtShelterCapacity(t *testing.T) {
	// Five colonists, five indoor spots: converting one would drop indoor
	// capacity to four and fail the shelter gate, so a new spot is staged.
	var rows []SleepingBed
	var ids []string
	for _, id := range []string{"s1", "s2", "s3", "s4", "s5"} {
		rows = append(rows, hospitalBed(id, false))
		ids = append(ids, id)
	}
	beds := domain.Known(SleepingObservation{Colonists: 5, Beds: rows})
	spot := []BenchDefinition{{Name: "Bed", Available: domain.Known(false)}, {Name: "SleepingSpot", Available: domain.Known(true)}}
	req := HospitalRequest{Patients: hospitalPatients("a"), Sleeping: beds, Rooms: hospitalRooms(RoomRoleBarracks, ids...), Definitions: spot,
		Colonists: domain.Known[int64](5), HousingTarget: domain.Known[int64](5), BedCapacity: domain.Known[int64](5), IndoorCapacity: domain.Known[int64](5), Doctors: domain.Known(1)}
	choice, err := SelectHospitalBed(req)
	if err != nil || choice.Method != HospitalBuild || choice.Definition != "SleepingSpot" {
		t.Fatal(choice, err)
	}
	// The staged sixth spot leaves a surplus of one: now it converts.
	req.BedCapacity, req.IndoorCapacity = domain.Known[int64](6), domain.Known[int64](6)
	if choice, err = SelectHospitalBed(req); err != nil || choice.Method != HospitalConvert {
		t.Fatal(choice, err)
	}
	// A housing target above the colonists holds the surplus too.
	req.HousingTarget = domain.Known[int64](6)
	if choice, err = SelectHospitalBed(req); err != nil || choice.Method != HospitalBuild {
		t.Fatal(choice, err)
	}
	// Nobody can doctor: no hospital work at all.
	req.Doctors = domain.Known(0)
	if choice, err = SelectHospitalBed(req); err != nil || choice.Method != HospitalUnavailable {
		t.Fatal(choice, err)
	}
}

func TestSelectHospitalBedUnknownFacts(t *testing.T) {
	choice, err := SelectHospitalBed(HospitalRequest{})
	if err != nil || choice.Method != HospitalUnknown {
		t.Fatal(choice, err)
	}
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: domain.Known(SleepingObservation{})})
	if err != nil || choice.Method != HospitalUnknown {
		t.Fatal(choice, err)
	}
	unknownRole := domain.Known(RoomObservation{Rooms: []Room{{ID: "room", Beds: []string{"bed1"}}}})
	choice, err = SelectHospitalBed(HospitalRequest{Patients: hospitalPatients("a"), Sleeping: domain.Known(SleepingObservation{Beds: []SleepingBed{hospitalBed("bed1", false)}}), Rooms: unknownRole})
	if err != nil || choice.Method != HospitalUnknown {
		t.Fatal(choice, err)
	}
}
