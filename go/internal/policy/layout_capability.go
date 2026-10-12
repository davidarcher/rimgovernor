package policy

import "slices"

// Capability is a coarse function a planned room can host. Rooms host
// capabilities, not fixed roles; nothing here is persisted, and free capacity
// per room is recomputed each review.
type Capability string

const (
	CapSleep    Capability = "sleep"
	CapCraft    Capability = "craft"
	CapResearch Capability = "research"
	CapCook     Capability = "cook"
	CapStore    Capability = "store"
	CapHeat     Capability = "heat"
)

// capabilityHomes is the census room role whose catalog row defines each
// capability. Heat has no catalog row: only the shelter's campfire provides it.
var capabilityHomes = map[Capability]RoomRole{
	CapSleep:    RoomRoleBedroom,
	CapCraft:    RoomRoleWorkshop,
	CapResearch: RoomRoleLaboratory,
	CapCook:     RoomRoleKitchen,
	CapStore:    RoomRoleStoreroom,
}

// shelterCapabilities is the shelter template's contents: sleeping spots, a
// work bench, a research bench and a campfire to cook and warm by. The
// catalog's shelter row is Pending, so the template is the source.
var shelterCapabilities = []Capability{CapSleep, CapCraft, CapResearch, CapCook, CapHeat}

// roleCapabilities is what a room of each planned role can host, derived from
// FacilityCatalog's Compatible lists through moduleRoomRoles. Every planned
// role has an entry, possibly empty.
var roleCapabilities = deriveRoleCapabilities()

func deriveRoleCapabilities() map[PlannedRole][]Capability {
	caps := []Capability{CapSleep, CapCraft, CapResearch, CapCook, CapStore, CapHeat}
	out := make(map[PlannedRole][]Capability, len(plannedRoleTier))
	for role := range plannedRoleTier {
		out[role] = nil
		room, ok := moduleRoomRoles[role]
		if !ok {
			continue
		}
		for _, c := range caps {
			if role == PlannedShelter && slices.Contains(shelterCapabilities, c) {
				out[role] = append(out[role], c)
				continue
			}
			home, ok := capabilityHomes[c]
			if !ok {
				continue
			}
			if f, err := Facility(home); err == nil && f.Hosts(room) {
				out[role] = append(out[role], c)
			}
		}
	}
	return out
}

// CapabilitiesOf lists the capabilities a room of the planned role can host.
func CapabilitiesOf(role PlannedRole) []Capability { return slices.Clone(roleCapabilities[role]) }

// The shelter's heat and cooling pieces: the game offers no signal tying a
// capability to a def, so these two names are judgment tables (DefTables).
const (
	// ShelterHeatDef is the campfire that provides CapHeat.
	ShelterHeatDef = "Campfire"
	// ShelterCoolDef is the passive cooler a hot map's shelter holds.
	ShelterCoolDef = "PassiveCooler"
)

// capabilityCells is the floor a capability's piece takes in the shelter
// template: the crafting spot one cell, the plain research bench 3x2.
var capabilityCells = map[Capability]int{CapCraft: 1, CapResearch: 6}

// ShelterHosts reports whether the shelter template hosts the capability.
func ShelterHosts(c Capability) bool { return slices.Contains(shelterCapabilities, c) }
