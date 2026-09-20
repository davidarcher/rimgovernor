package bridge

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func TestZoneExactConfigurationEvidence(t *testing.T) {
	zone, _ := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	token := ZoneConfigurationToken(zone)
	if token != "zone-defa6b500f62968795cd38e729afec1f76043176f93cab6983029e87a4556a9c" {
		t.Fatal("native configuration hash drift", token)
	}
	v := &r.EffectEvidence{Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{ZoneId: proto.String("zone1"), Present: proto.Bool(true), ListedCellCount: proto.Int32(2), GridCellCount: proto.Int32(2), PhantomCellCount: proto.Int32(0), ChangedCells: proto.Int32(2), Snapshot: &r.SnapshotEvidence{EntityId: proto.String("zone1"), BeforeToken: proto.String("map-token"), AfterToken: proto.String(token)}}}}
	for _, cell := range zone.Cells() {
		v.GetZone().Cells = append(v.GetZone().Cells, &r.CellResult{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Accepted: proto.Bool(true)})
	}
	if matches, err := ZoneMatches(v, zone, "map-token"); err != nil || !matches {
		t.Fatal(matches, err)
	}
	for _, change := range []func(*r.EffectEvidence){func(v *r.EffectEvidence) { v.GetZone().Snapshot.AfterToken = proto.String("other-crop") }, func(v *r.EffectEvidence) { v.GetZone().GridCellCount = proto.Int32(1) }, func(v *r.EffectEvidence) { v.GetZone().Cells[1].Cell.X = proto.Int32(2) }, func(v *r.EffectEvidence) { v.GetZone().Snapshot.BeforeToken = proto.String("foreign-map") }} {
		copy := proto.Clone(v).(*r.EffectEvidence)
		change(copy)
		if matches, _ := ZoneMatches(copy, zone, "map-token"); matches {
			t.Fatal("foreign/incomplete zone")
		}
	}
	v.GetZone().Cells[1] = v.GetZone().Cells[0]
	if _, err := ZoneMatches(v, zone, "map-token"); err == nil {
		t.Fatal("duplicate cells")
	}
}

func TestFishingZoneExtensionBindsIdentityAndFloor(t *testing.T) {
	z, err := domain.NewFishingZoneExtension("Zone_7", []domain.Cell{{X: 1, Z: 2}})
	if err != nil {
		t.Fatal(err)
	}
	op := ZoneConfiguration(z)
	if op.GetType() != 4 || op.GetExtendZoneId() != "Zone_7" || op.GetFishing().GetPopulationFloor() != .6 || op.Growing != nil || op.Stockpile != nil {
		t.Fatal(op)
	}
	v := &r.EffectEvidence{Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{ZoneId: proto.String("Zone_7"), Present: proto.Bool(true), ListedCellCount: proto.Int32(1), GridCellCount: proto.Int32(1), PhantomCellCount: proto.Int32(0), ChangedCells: proto.Int32(1), Snapshot: &r.SnapshotEvidence{EntityId: proto.String("Zone_7"), BeforeToken: proto.String("map-token"), AfterToken: proto.String(ZoneConfigurationToken(z))}, Cells: []*r.CellResult{{Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}, Accepted: proto.Bool(true)}}}}}
	if matches, err := ZoneMatches(v, z, "map-token"); err != nil || !matches {
		t.Fatal(matches, err)
	}
	v.GetZone().ZoneId, v.GetZone().Snapshot.EntityId = proto.String("Zone_8"), proto.String("Zone_8")
	if matches, _ := ZoneMatches(v, z, "map-token"); matches {
		t.Fatal("extension accepted different zone identity")
	}
}

func TestCorpseLarderZoneExcludesRottenAndNonAnimalStock(t *testing.T) {
	zone, err := domain.NewStockpileZone(domain.CorpseLarderPreset, domain.ImportantPriority, []domain.Cell{{X: 1, Z: 2}})
	if err != nil {
		t.Fatal(err)
	}
	settings := ZoneConfiguration(zone).Stockpile
	if settings.GetPreset() != op.FilterPreset_FILTER_PRESET_NOTHING || len(settings.Filter.Allow) != 2 || settings.Filter.Allow[0].GetCategoryDef() != "CorpsesAnimal" || settings.Filter.Allow[1].GetSpecialFilterDef() != "AllowFresh" || len(settings.Filter.Disallow) != 1 || settings.Filter.Disallow[0].GetSpecialFilterDef() != "AllowRotten" {
		t.Fatal(settings)
	}
	if restored, err := domain.ReconstructZone(zone); err != nil || restored != zone {
		t.Fatal(restored, err)
	}
}

func TestZoneConfigurationBranchesOnKind(t *testing.T) {
	cells := []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}
	growing, _ := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", cells)
	stockpile, _ := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, cells)

	g := ZoneConfiguration(growing)
	if g.GetType() != op.ZoneType_ZONE_TYPE_GROWING || g.Stockpile != nil || g.GetGrowing().GetPlantDef() != "Plant_Rice" || !g.GetGrowing().GetAllowSow() || !g.GetGrowing().GetAllowCut() {
		t.Fatal("unexpected growing zone configuration", g)
	}
	s := ZoneConfiguration(stockpile)
	if s.GetType() != op.ZoneType_ZONE_TYPE_STOCKPILE || s.Growing != nil || s.GetStockpile().GetPriority() != op.StoragePriority_STORAGE_PRIORITY_IMPORTANT || s.GetStockpile().GetPreset() != op.FilterPreset_FILTER_PRESET_FOOD {
		t.Fatal("unexpected stockpile zone configuration", s)
	}
	if s.GetLabel() != "RimGovernor food storage" || g.GetLabel() != "RimGovernor crops" {
		t.Fatal("unexpected zone labels", g.GetLabel(), s.GetLabel())
	}
	allowList, _ := domain.NewAllowListStockpileZone(domain.ImportantPriority, []string{"MealSimple", "MealFine"}, cells)
	a := ZoneConfiguration(allowList)
	if a.GetType() != op.ZoneType_ZONE_TYPE_STOCKPILE || a.GetStockpile().GetPreset() != op.FilterPreset_FILTER_PRESET_NOTHING || a.GetStockpile().GetPriority() != op.StoragePriority_STORAGE_PRIORITY_IMPORTANT {
		t.Fatal("unexpected allow-list stockpile configuration", a)
	}
	allow := a.GetStockpile().GetFilter().GetAllow()
	if len(allow) != 2 || allow[0].GetThingDef() != "MealFine" || allow[1].GetThingDef() != "MealSimple" {
		t.Fatal("unexpected allow-list filter selectors", allow)
	}
	if a.GetLabel() != "RimGovernor supplies storage" {
		t.Fatal("unexpected allow-list zone label", a.GetLabel())
	}
	if s.GetStockpile().GetFilter() != nil {
		t.Fatal("food preset must not carry an allow-list filter")
	}
	if ZoneConfigurationToken(growing) == ZoneConfigurationToken(stockpile) {
		t.Fatal("expected distinct configuration hashes for distinct zone kinds")
	}
}

func TestStockpileZoneExactConfigurationEvidence(t *testing.T) {
	zone, _ := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})
	token := ZoneConfigurationToken(zone)
	v := &r.EffectEvidence{Effect: &r.EffectEvidence_Zone{Zone: &r.ZoneEffect{ZoneId: proto.String("zone1"), Present: proto.Bool(true), ListedCellCount: proto.Int32(2), GridCellCount: proto.Int32(2), PhantomCellCount: proto.Int32(0), ChangedCells: proto.Int32(2), Snapshot: &r.SnapshotEvidence{EntityId: proto.String("zone1"), BeforeToken: proto.String("map-token"), AfterToken: proto.String(token)}}}}
	for _, cell := range zone.Cells() {
		v.GetZone().Cells = append(v.GetZone().Cells, &r.CellResult{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Accepted: proto.Bool(true)})
	}
	if matches, err := ZoneMatches(v, zone, "map-token"); err != nil || !matches {
		t.Fatal(matches, err)
	}
	// Native leaves AfterToken hashed for a different desired configuration
	// (e.g. it could not confirm the live filter matches the requested preset
	// exactly); readback must not paper over that with geometry alone.
	other, _ := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 0, Z: 0}, {X: 2, Z: 0}})
	v.GetZone().Cells[1].Cell.X = proto.Int32(2)
	if matches, _ := ZoneMatches(v, other, "map-token"); matches {
		t.Fatal("expected mismatch against stale configuration hash")
	}
}

// A zone preview native evaluated but refused (littered or occupied ground)
// is a reply the planner moves past to its next candidate, not a contract
// violation; a failure outcome or a missing verdict still rejects (#223).
func TestPreviewZoneReturnsARefusedSiteAsAnEvaluation(t *testing.T) {
	zone, _ := domain.NewStockpileZone(domain.FoodPreset, domain.ImportantPriority, []domain.Cell{{X: 1, Z: 1}})
	target := ZoneTarget{Zone: zone, Token: "zone-abc"}
	valid := &op.PreviewReply{Outcome: &op.PreviewReply_Evaluated{Evaluated: &op.PreviewEvaluation{Context: pbContext(), Accepted: proto.Bool(true)}}}
	for _, test := range []struct {
		name     string
		change   func(*op.PreviewReply)
		accepted bool
		ok       bool
	}{
		{"accepted", func(*op.PreviewReply) {}, true, true},
		{"refused site", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = proto.Bool(false) }, false, true},
		{"missing verdict", func(v *op.PreviewReply) { v.GetEvaluated().Accepted = nil }, false, false},
		{"unexpected projection", func(v *op.PreviewReply) { v.GetEvaluated().Projected = &r.EffectEvidence{} }, false, false},
		{"failure outcome", func(v *op.PreviewReply) {
			v.Outcome = &op.PreviewReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}
		}, false, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			reply := proto.Clone(valid).(*op.PreviewReply)
			test.change(reply)
			s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
				if arg.Tool != "rimgovernor/operations_preview" {
					t.Fatal(arg.Tool)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, testBudget)
			got, _, err := client.PreviewZone(context.Background(), pbIdentity(), target)
			if !test.ok {
				if !errors.Is(err, ErrContract) && !errors.Is(err, ErrRefused) {
					t.Fatal("expected rejection", err)
				}
				return
			}
			if err != nil || got.GetEvaluated().GetAccepted() != test.accepted {
				t.Fatal(got, err)
			}
		})
	}
}
