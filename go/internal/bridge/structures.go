package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const (
	structuresPageLimit = 256
	structuresMaxPages  = 32
)

// Structure is one player building, blueprint or frame of a requested
// definition standing on a cell, whatever its build state.
type Structure struct {
	ID         string
	Definition string
	Stuff      string
	Cell       domain.Cell
	// Status is blueprint, frame or built, native's own vocabulary.
	Status string
}

// StructureRead is the complete census of the requested definitions inside a
// region at one native tick.
type StructureRead struct {
	Tick       domain.Tick
	Generation uint64
	Structures []Structure
}

// ReadStructures lists every player thing of the given definitions whose
// anchor lies inside the inclusive region, in every build state. A shell
// routine uses it to tell its own earlier walls and doors, whether finished,
// framed or still blueprints, from cells nothing has claimed yet.
func (client *Client) ReadStructures(ctx context.Context, identity *c.Identity, minimum, maximum domain.Cell, definitions []string) (StructureRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return StructureRead{}, Result{}, err
	}
	if len(definitions) < 1 || len(definitions) > 16 || minimum.X > maximum.X || minimum.Z > maximum.Z || minimum.X < 0 || minimum.Z < 0 {
		return StructureRead{}, Result{}, contract("invalid structure census request")
	}
	for _, def := range definitions {
		if validID(def) != nil {
			return StructureRead{}, Result{}, contract("invalid structure definition")
		}
	}
	identity = proto.Clone(identity).(*c.Identity)
	out := StructureRead{}
	cursor := ""
	var raw Result
	for page := 0; page < structuresMaxPages; page++ {
		request := &o.ListBuildingsRequest{
			Scope:      &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
			DefNames:   append([]string(nil), definitions...),
			Statuses:   []string{"all"},
			PlayerOnly: proto.Bool(true),
			Region: &o.Rectangle{
				Minimum: &c.Cell{X: proto.Int32(minimum.X), Z: proto.Int32(minimum.Z)},
				Maximum: &c.Cell{X: proto.Int32(maximum.X), Z: proto.Int32(maximum.Z)},
			},
			Page: &c.PageRequest{Limit: proto.Uint32(structuresPageLimit)},
		}
		if cursor != "" {
			request.Page.Cursor = proto.String(cursor)
		}
		reply := &o.ListBuildingsReply{}
		var err error
		raw, err = client.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
		if err != nil {
			return StructureRead{}, raw, err
		}
		if buildingUnknown(reply) != nil {
			return StructureRead{}, raw, contract("unknown structure census fields")
		}
		var observed *o.BuildingsSnapshot
		switch v := reply.Outcome.(type) {
		case *o.ListBuildingsReply_Failure:
			return StructureRead{}, raw, failure(v.Failure, raw)
		case *o.ListBuildingsReply_Unavailable:
			return StructureRead{}, raw, unavailable(v.Unavailable, raw)
		case *o.ListBuildingsReply_Observed:
			observed = v.Observed
		default:
			return StructureRead{}, raw, contract("structure census outcome missing")
		}
		if observed == nil {
			return StructureRead{}, raw, contract("structure census snapshot missing")
		}
		if err = buildingContext(observed.Context, identity, 0, false); err != nil {
			return StructureRead{}, raw, err
		}
		counts := observed.Completeness
		if counts == nil || counts.Page == nil || counts.Returned == nil || counts.Unreadable == nil ||
			counts.GetUnreadable() != 0 || counts.GetReturned() != uint64(len(observed.Buildings)) {
			return StructureRead{}, raw, contract("incomplete structure census")
		}
		next := counts.Page.GetNextCursor()
		if next == "" && !counts.Page.GetComplete() {
			return StructureRead{}, raw, contract("incomplete structure census page")
		}
		if page == 0 {
			out.Tick = domain.Tick(observed.Context.GetTick())
			out.Generation = observed.Context.GetNativeGeneration()
		} else if domain.Tick(observed.Context.GetTick()) != out.Tick || observed.Context.GetNativeGeneration() != out.Generation {
			return StructureRead{}, raw, contract("structure census changed during pagination")
		}
		for _, row := range observed.Buildings {
			if row == nil || row.Building == nil || validID(row.Building.GetId()) != nil || row.Building.Position == nil || validID(row.Building.GetDefName()) != nil {
				return StructureRead{}, raw, contract("invalid structure census row")
			}
			switch row.GetStatus() {
			case "blueprint", "frame", "built":
			default:
				return StructureRead{}, raw, contract("unexpected structure status")
			}
			// Blueprints and frames carry their own definition names; the
			// building they will become is the one a shell compares against.
			definition := row.Building.GetDefName()
			if row.BuildDefName != nil {
				if validID(row.GetBuildDefName()) != nil {
					return StructureRead{}, raw, contract("invalid structure build definition")
				}
				definition = row.GetBuildDefName()
			}
			out.Structures = append(out.Structures, Structure{
				ID:         row.Building.GetId(),
				Definition: definition,
				Stuff:      row.GetStuff(),
				Cell:       domain.Cell{X: row.Building.Position.GetX(), Z: row.Building.Position.GetZ()},
				Status:     row.GetStatus(),
			})
		}
		if next == "" {
			return out, raw, nil
		}
		cursor = next
	}
	return StructureRead{}, raw, contract("structure census exceeds page bound")
}
