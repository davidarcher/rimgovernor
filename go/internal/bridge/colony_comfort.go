package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"math"
	"slices"
)

func validateColonyUpkeep(v *o.UpkeepFacts, size *o.MapSize) error {
	if v == nil || !proto.Equal(v, &o.UpkeepFacts{Comfort: v.Comfort, Completeness: v.Completeness, Issues: v.Issues, Items: v.Items, Structures: v.Structures, Fires: v.Fires, Filth: v.Filth, Animals: v.Animals, People: v.People, Beds: v.Beds, HomeCoverage: v.HomeCoverage, Lighting: v.Lighting, WildAnimals: v.WildAnimals, Flooring: v.Flooring, Routes: v.Routes}) {
		return contract("unsupported upkeep projection")
	}
	if err := colonyCounts(v.Completeness, 1, 1); err != nil {
		return err
	}
	if err := pawnsIssues(v.Issues, v.ProtoReflect()); err != nil {
		return err
	}
	comfort := v.GetComfort().GetObserved()
	if comfort == nil {
		return validateUnavailable(v.GetComfort().GetUnavailable())
	}
	if err := colonyCounts(comfort.Completeness, len(comfort.People), 256); err != nil {
		return err
	}
	if len(comfort.Surfaces) > 256 || len(comfort.Dining) > 256 || len(comfort.Recreation) > 256 {
		return contract("comfort facilities exceed bound")
	}
	people := map[string]bool{}
	for _, id := range comfort.People {
		if validID(id) != nil || people[id] {
			return contract("invalid comfort person")
		}
		people[id] = true
	}
	surfaces := map[string]bool{}
	for _, surface := range comfort.Surfaces {
		if surface == nil || validID(surface.GetId()) != nil || surfaces[surface.GetId()] || len(surface.Adjacent) > 4096 || surface.RoomId != nil && validID(surface.GetRoomId()) != nil {
			return contract("invalid dining surface")
		}
		surfaces[surface.GetId()] = true
		cells := map[[2]int32]bool{}
		for _, cell := range surface.Adjacent {
			key := [2]int32{cell.GetX(), cell.GetZ()}
			if !colonyCell(cell, size) || cells[key] {
				return contract("invalid dining adjacency")
			}
			cells[key] = true
		}
	}
	for kind, facilities := range [][]*o.ComfortFacility{comfort.Dining, comfort.Recreation} {
		seen := map[string]bool{}
		for _, f := range facilities {
			if f == nil || validID(f.GetId()) != nil || seen[f.GetId()] || kind == 0 && f.Kind != nil || kind == 1 && validID(f.GetKind()) != nil || f.RoomId != nil && validID(f.GetRoomId()) != nil {
				return contract("invalid comfort facility")
			}
			seen[f.GetId()] = true
			for _, ids := range [][]string{f.AccessibleTo, f.Users} {
				if len(ids) > 256 {
					return contract("comfort access exceeds bound")
				}
				found := map[string]bool{}
				for _, id := range ids {
					if !people[id] || found[id] {
						return contract("invalid comfort access or use")
					}
					found[id] = true
				}
			}
		}
	}
	return validateRecreationCensus(comfort, people)
}

func validateRecreationCensus(v *o.ComfortFacts, people map[string]bool) error {
	j := v.Joy
	if j == nil {
		return nil
	}
	bad := func() error { return contract("invalid recreation kind census") }
	if len(j.Kinds) > 16 || len(j.Pawns) > 256 || len(j.Pawns)*len(j.Kinds) > 2048 || len(j.Methods) > 4 {
		return bad()
	}
	encoded, err := protojson.Marshal(j)
	if err != nil || len(encoded) > 64*1024 {
		return bad()
	}
	kinds := map[string]bool{}
	for _, k := range j.Kinds {
		if validID(k) != nil || kinds[k] {
			return bad()
		}
		kinds[k] = true
	}
	available := map[string]bool{}
	for _, f := range v.Recreation {
		if !kinds[f.GetKind()] {
			return bad()
		}
		available[f.GetKind()] = true
	}
	if len(available) != len(kinds) {
		return bad()
	}
	pawns := map[string]bool{}
	for _, p := range j.Pawns {
		if p == nil || !people[p.Pawn] || pawns[p.Pawn] || len(p.Tolerance) != len(j.Kinds) || len(p.Bored) != len(j.Kinds) {
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
		if m == nil || !slices.Contains([]string{"TubeTelevision", "BilliardsTable", "ChessTable", "HorseshoesPin"}, m.Definition) || methods[m.Definition] || validID(m.Kind) != nil || math.IsNaN(m.PowerW) || math.IsInf(m.PowerW, 0) || m.PowerW < 0 {
			return bad()
		}
		methods[m.Definition] = true
	}
	return nil
}
