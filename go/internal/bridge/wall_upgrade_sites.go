package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// WallMaterial is one candidate stone stuff RoutineStoneShellPlanner may
// build the backups and permanent replacement from, and its per-wall cost.
type WallMaterial struct {
	Stuff string
	Costs []Amount
}
type Amount struct {
	Resource string
	Units    int64
}

// WallUpgradeSite is one wall-upgrade candidate RoutineStoneShellPlanner
// proposes a bundle from: the current native occupant at this exact geometry
// and whether native lists the site as a legal, unblocked candidate. Native
// excludes geometrically invalid sites from the listing entirely
// (unsupported roof, no interior, bounds); a returned row that still carries
// a blocker is present for status but is not eligible.
type WallUpgradeSite struct {
	TargetID             string
	TargetPresent        bool
	Blocker              string
	X, Z, NX, NZ         int32
	BackupCells          []domain.Cell
	LeftSupport          bool
	RightSupport         bool
	ReplacementMaterials []WallMaterial
}

func (s WallUpgradeSite) Eligible() bool {
	return s.TargetID != "" && s.TargetPresent && s.Blocker == ""
}

// WallUpgradeSites is one fresh census of candidate sites plus its
// observation context.
type WallUpgradeSites struct {
	Context *c.ObservationContext
	Sites   []WallUpgradeSite
}

// ReadWallUpgradeSites lists current wall-upgrade candidate sites, optionally
// scoped to one original wall's identity.
func (client *Client) ReadWallUpgradeSites(ctx context.Context, identity *c.Identity, targetID string) (WallUpgradeSites, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return WallUpgradeSites{}, Result{}, err
	}
	if targetID != "" && validID(targetID) != nil {
		return WallUpgradeSites{}, Result{}, contract("invalid wall upgrade target identity")
	}
	request := &o.WallUpgradeSitesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	if targetID != "" {
		request.TargetId = proto.String(targetID)
	}
	reply := &o.WallUpgradeSitesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_wall_upgrade_sites", request, reply)
	if err != nil {
		return WallUpgradeSites{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return WallUpgradeSites{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.WallUpgradeSitesReply_Failure:
		return WallUpgradeSites{}, raw, failure(v.Failure, raw)
	case *o.WallUpgradeSitesReply_Unavailable:
		return WallUpgradeSites{}, raw, unavailable(v.Unavailable, raw)
	case *o.WallUpgradeSitesReply_Observed:
		buildings, err := client.FrameBuildings(ctx, identity)
		if err != nil {
			return WallUpgradeSites{}, raw, err
		}
		sites, err := validateWallUpgradeSites(v.Observed, identity, buildings)
		if err != nil {
			return WallUpgradeSites{}, raw, err
		}
		return WallUpgradeSites{Context: v.Observed.Context, Sites: sites}, raw, nil
	default:
		return WallUpgradeSites{}, raw, contract("wall upgrade sites outcome missing")
	}
}

func validateWallUpgradeSites(v *o.WallUpgradeSnapshot, identity *c.Identity, buildings Buildings) ([]WallUpgradeSite, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid wall upgrade sites context")
	}
	rows := make([]WallUpgradeSite, 0, len(v.Sites))
	for _, row := range v.Sites {
		if row == nil || row.Normal == nil {
			return nil, contract("invalid wall upgrade site")
		}
		site := WallUpgradeSite{Blocker: row.GetBlocker(), NX: row.Normal.GetX(), NZ: row.Normal.GetZ(), LeftSupport: row.LeftSupport != nil, RightSupport: row.RightSupport != nil}
		// The original wall's cell is its building row's (#1342).
		if original, ok := buildings.Row(row.Original); ok {
			if pos := original.GetBuilding().GetPosition(); pos != nil {
				site.X, site.Z = pos.GetX(), pos.GetZ()
			}
		}
		if row.Target != nil {
			site.TargetID = row.Target.GetId()
			site.TargetPresent = row.GetTargetPresent()
		}
		for _, cell := range row.BackupCells {
			if cell == nil {
				return nil, contract("invalid wall upgrade backup cell")
			}
			site.BackupCells = append(site.BackupCells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		for _, m := range row.ReplacementMaterials {
			if m == nil || m.Stuff == nil {
				return nil, contract("invalid wall upgrade material")
			}
			material := WallMaterial{Stuff: m.GetStuff()}
			for _, q := range m.Costs {
				if q == nil || q.DefName == nil || q.Units == nil {
					return nil, contract("invalid wall upgrade material cost")
				}
				material.Costs = append(material.Costs, Amount{Resource: q.GetDefName(), Units: q.GetUnits()})
			}
			site.ReplacementMaterials = append(site.ReplacementMaterials, material)
		}
		rows = append(rows, site)
	}
	return rows, nil
}
