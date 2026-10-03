package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// VetRoom is what the layout exposes of the vet room (#1633) to the herd
// plan. Ready is a vet room reservation with a built medical animal bed;
// Area is the allowed-area id covering the room, the area an animal is let
// into for surgery. Either unknown (or Ready false) means no sterilize.
type VetRoom struct {
	Ready domain.Fact[bool]
	Area  domain.Fact[string]
}

// sterilizeWanted is the animals the plan wants sterilized now: of a race
// that is superseded (a better race will take its job; it keeps working but
// stops breeding) or the males beyond the plan's ratio (herdMalesPerFemales)
// of a race kept within its cap. Never a founder, a retiring race (culled
// instead), an animal designated for removal, or one with an unread
// sterilize fact; and never a sex below its breeding pair of fertile
// animals (herdPairMales, herdPairFemales), which must exist to begin with.
// A queued bill counts as sterile already so a bill cannot take a race
// below its pair.
func sterilizeWanted(rows []UpkeepAnimal, herd HerdPolicy) map[PawnID]bool {
	fertile := map[Resource][]UpkeepAnimal{}
	for _, a := range rows {
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		sterilized, tk := a.Sterilized.Value()
		queued, qk := a.SterilizeQueued.Value()
		if !rk || !sk || !tk || !qk || release || slaughter || sterilized || queued {
			continue
		}
		fertile[a.Definition] = append(fertile[a.Definition], a)
	}
	wanted := map[PawnID]bool{}
	for race, animals := range fertile {
		role := herd.Roles[race]
		if role.Founder || role.Retiring || herd.Retired[race] {
			continue
		}
		males, females := int64(0), int64(0)
		for _, a := range animals {
			switch a.Gender {
			case "Male":
				males++
			case "Female":
				females++
			}
		}
		if males < herdPairMales || females < herdPairFemales {
			continue
		}
		excessMales := males - max(herdPairMales, (females+herdMalesPerFemales-1)/herdMalesPerFemales)
		for _, a := range animals {
			switch {
			case a.Gender == "Male" && males > herdPairMales && (role.Superseded || excessMales > 0):
				wanted[a.ID] = true
				males--
				excessMales--
			case a.Gender == "Female" && females > herdPairFemales && role.Superseded:
				wanted[a.ID] = true
				females--
			}
		}
	}
	return wanted
}

// SterilizeChoice sequences the sterilize surgery through the vet room, one
// write per cycle, from the animals' current allowed area and sterilize
// facts alone: let an animal wanted sterilized into the vet room
// (allowed_area), queue its bill (sterilize), and let it out once it is
// sterilized (allowed_area cleared). One animal is in the room at a time,
// and an animal with a bill queued is waited on. An animal in the room that
// is no longer wanted and has no bill is let out. An unready or unknown vet
// room chooses nothing.
func SterilizeChoice(animals domain.Fact[[]UpkeepAnimal], herd HerdPolicy, vet VetRoom) HusbandryChoice {
	none := HusbandryChoice{Reason: HusbandryNoDeficit}
	rows, known := animals.Value()
	ready, rk := vet.Ready.Value()
	area, ak := vet.Area.Value()
	if !known || !rk || !ready || !ak || area == "" {
		return none
	}
	rows = append([]UpkeepAnimal(nil), rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
	wanted := sterilizeWanted(rows, herd)
	for _, a := range rows {
		if in, ok := a.AllowedArea.Value(); !ok || in != area {
			continue
		}
		sterilized, tk := a.Sterilized.Value()
		queued, qk := a.SterilizeQueued.Value()
		switch {
		case !tk || !qk || queued:
			return none
		case sterilized, !wanted[a.ID]:
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryAllowedArea}
		}
		return HusbandryChoice{Animal: a.ID, Method: domain.HusbandrySterilize}
	}
	for _, a := range rows {
		if supports, ok := a.SupportsAreas.Value(); wanted[a.ID] && ok && supports {
			return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryAllowedArea, Argument: area}
		}
	}
	return none
}
