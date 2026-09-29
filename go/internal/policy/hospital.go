package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HospitalMethod is what a MaintainMedicalReserves deficit needs from the
// facility ladder: a humanlike bed the game treats as medical, standing in a
// room whose native role can host the Hospital function.
type HospitalMethod string

const (
	// HospitalUnknown: a native fact the choice depends on is unobserved.
	HospitalUnknown HospitalMethod = ""
	// HospitalExisting: enough hosted medical beds already stand; tending,
	// rescue and the medicine reserve own the deficit from here.
	HospitalExisting HospitalMethod = "existing"
	// HospitalConvert: flag Bed, an existing hosted bed, as medical.
	HospitalConvert HospitalMethod = "convert"
	// HospitalBuild: stage Definition in a Hospital-hosting room; the next
	// review converts it.
	HospitalBuild HospitalMethod = "build"
	// HospitalNoDemand: no living colonist is a patient.
	HospitalNoDemand HospitalMethod = "no_demand"
	// HospitalUnavailable: no bed can be converted without evicting a healthy
	// colonist and no bed definition is buildable now.
	HospitalUnavailable HospitalMethod = "unavailable"
)

// HospitalBedDefinitions lists, in preference order, the bed definitions the
// hospital planner may stage; a sleeping spot is the free fallback.
var HospitalBedDefinitions = []string{"Bed", "SleepingSpot"}

type HospitalRequest struct {
	Patients    domain.Fact[[]CarePawn]
	Sleeping    domain.Fact[SleepingObservation]
	Rooms       domain.Fact[RoomObservation]
	Definitions []BenchDefinition
	// Colonists, HousingTarget, BedCapacity and IndoorCapacity are the
	// routine counts a conversion must not drop below: the native indoor
	// sleeping capacity excludes medical beds, so converting a needed
	// shelter spot fails the shelter gate.
	Colonists, HousingTarget, BedCapacity, IndoorCapacity domain.Fact[int64]
	// Doctors is the count of Doctor-capable colonists; a known zero means
	// nobody could treat a patient in the ward, so no hospital work runs.
	Doctors domain.Fact[int]
	// Surgical are patients whose operation waits on a better bed or room
	// (SurgeryBedShortPatients, #1240): each needs a hosted medical bed,
	// and while any waits a sleeping spot neither counts nor is staged.
	Surgical []PawnID
}

type HospitalChoice struct {
	Method HospitalMethod
	// Needed is the count of hosted medical beds the patients call for.
	Needed     int
	Bed        string
	Definition string
}

// SelectHospitalBed decides whether the colony's patients already have hosted
// medical beds, which existing bed to convert, or which definition to stage.
// Conversion prefers an unowned bed, then a bed a patient owns (the game
// drops the owner, who then rests in it as a patient); a healthy colonist is
// never evicted for a hospital bed while one can be built instead. It issues
// no orders and reserves nothing.
func SelectHospitalBed(r HospitalRequest) (HospitalChoice, error) {
	if len(r.Definitions) > 256 {
		return HospitalChoice{}, errors.New("hospital request exceeds bound")
	}
	patients, known := r.Patients.Value()
	if !known {
		return HospitalChoice{Method: HospitalUnknown}, nil
	}
	if len(patients) > 256 {
		return HospitalChoice{}, errors.New("hospital census exceeds bound")
	}
	// A hospital bed serves medical rest. MaintainMedicalReserves's wider
	// census (any bad condition, e.g. a scar) keeps the deficit; it does
	// not size the ward.
	ill := map[PawnID]bool{}
	for _, p := range patients {
		dead, dk := p.Dead.Value()
		rest, rk := p.NeedsRest.Value()
		if !dk {
			return HospitalChoice{Method: HospitalUnknown}, nil
		}
		if dead {
			continue
		}
		if !rk {
			return HospitalChoice{Method: HospitalUnknown}, nil
		}
		if rest {
			ill[p.ID] = true
		}
	}
	for _, id := range r.Surgical {
		ill[id] = true
	}
	surgical := len(r.Surgical) > 0
	choice := HospitalChoice{Needed: len(ill)}
	if choice.Needed == 0 {
		choice.Method = HospitalNoDemand
		return choice, nil
	}
	if doctors, known := r.Doctors.Value(); known && doctors <= 0 {
		choice.Method = HospitalUnavailable
		return choice, nil
	}
	facility, err := Facility(RoomRoleHospital)
	if err != nil {
		return HospitalChoice{}, err
	}
	sleeping, sk := r.Sleeping.Value()
	rooms, rk := r.Rooms.Value()
	if !sk || !rk {
		choice.Method = HospitalUnknown
		return choice, nil
	}
	if len(sleeping.Beds) > 256 || len(rooms.Rooms) > 256 {
		return HospitalChoice{}, errors.New("hospital census exceeds bound")
	}
	hosted := map[string]bool{}
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		if !known {
			choice.Method = HospitalUnknown
			return choice, nil
		}
		if facility.Hosts(role) {
			for _, bed := range room.Beds {
				hosted[bed] = true
			}
		}
	}
	medical := 0
	var spare, owned []SleepingBed
	for _, bed := range sleeping.Beds {
		if !hosted[bed.ID] || surgical && bed.Definition == "SleepingSpot" {
			continue
		}
		humanlike, hk := bed.Humanlike.Value()
		isMedical, mk := bed.Medical.Value()
		prisoners, pk := bed.Prisoners.Value()
		if !hk || !mk || !pk {
			choice.Method = HospitalUnknown
			return choice, nil
		}
		if !humanlike || prisoners {
			continue
		}
		if isMedical {
			medical++
			continue
		}
		if len(bed.Owners) == 0 {
			spare = append(spare, bed)
			continue
		}
		patientOwned := true
		for _, owner := range bed.Owners {
			patientOwned = patientOwned && ill[owner]
		}
		if patientOwned {
			owned = append(owned, bed)
		}
	}
	if medical >= choice.Needed {
		choice.Method = HospitalExisting
		return choice, nil
	}
	if !hospitalConversionSpare(r, sleeping) {
		spare, owned = nil, nil
	}
	for _, candidates := range [][]SleepingBed{spare, owned} {
		if len(candidates) == 0 {
			continue
		}
		sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
		choice.Method, choice.Bed = HospitalConvert, candidates[0].ID
		return choice, nil
	}
	byName := map[string]BenchDefinition{}
	for _, d := range r.Definitions {
		byName[d.Name] = d
	}
	unknown := false
	for _, name := range HospitalBedDefinitions {
		if surgical && name == "SleepingSpot" {
			continue
		}
		d, exists := byName[name]
		available, ak := d.Available.Value()
		if !exists || !ak {
			unknown = true
			continue
		}
		if available {
			choice.Method, choice.Definition = HospitalBuild, name
			return choice, nil
		}
	}
	if unknown {
		choice.Method = HospitalUnknown
		return choice, nil
	}
	choice.Method = HospitalUnavailable
	return choice, nil
}

// hospitalConversionSpare reports whether one regular bed can turn medical
// without dropping bed or indoor sleeping capacity below
// max(colonists, housing target). Native IndoorSleepingCapacity excludes
// medical beds, so converting a needed shelter spot deadlocks the shelter
// gate; a new bed is staged instead (and converted once it stands, when the
// surplus it adds allows).
func hospitalConversionSpare(r HospitalRequest, sleeping SleepingObservation) bool {
	required := int64(sleeping.Colonists)
	for _, f := range []domain.Fact[int64]{r.Colonists, r.HousingTarget} {
		if v, known := f.Value(); known {
			required = max(required, v)
		}
	}
	capacity, measured := int64(0), false
	for _, f := range []domain.Fact[int64]{r.BedCapacity, r.IndoorCapacity} {
		if v, known := f.Value(); known {
			if !measured || v < capacity {
				capacity = v
			}
			measured = true
		}
	}
	if !measured {
		for _, bed := range sleeping.Beds {
			humanlike, _ := bed.Humanlike.Value()
			medical, mk := bed.Medical.Value()
			prisoners, _ := bed.Prisoners.Value()
			if humanlike && mk && !medical && !prisoners {
				capacity++
			}
		}
	}
	return capacity-1 >= required
}
