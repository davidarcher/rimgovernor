package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestWorldSiteExtensionsRejectMalformedNativeEvidence(t *testing.T) {
	v := worldProgressionFixture()
	v.Quests[0].Objectives = []*o.QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HOLD_SURVEY_SCANNER.Enum(), SurveyScanner: &o.QuestSurveyScanner{SiteId: proto.String("site"), DurationTicks: proto.Int64(900000)}}}
	if _, err := worldProgressionSelected(v, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	for name, edit := range map[string]func(*o.WorldSite){
		"negative risk":      func(s *o.WorldSite) { s.Security = &o.QuestSiteSecurity{InitialPoints: proto.Float64(-1)} },
		"diplomacy identity": func(s *o.WorldSite) { s.PeaceTalks = &o.QuestPeaceTalks{} },
		"cargo duplicate": func(s *o.WorldSite) {
			c := &o.QuestSiteCargo{Id: proto.String("item"), Def: proto.String("Silver"), Count: proto.Int64(2)}
			s.Extraction = &o.QuestSiteExtraction{Cargo: []*o.QuestSiteCargo{c, c}}
		},
		"negative exit": func(s *o.WorldSite) {
			s.Extraction = &o.QuestSiteExtraction{ExitCells: []*c.Cell{{X: proto.Int32(-1), Z: proto.Int32(0)}}}
		},
		"unreachable estimate": func(s *o.WorldSite) {
			s.Extraction = &o.QuestSiteExtraction{HomeRoutes: []*o.QuestSiteHomeRoute{{MapId: proto.Int32(0), Tile: proto.Int32(1), TravelTicks: proto.Int64(50), Reachable: proto.Bool(false)}}}
		},
	} {
		t.Run(name, func(t *testing.T) {
			s := &o.WorldSite{State: o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED.Enum()}
			edit(s)
			if err := validSiteExtensions(s); err == nil {
				t.Fatal("accepted malformed census")
			}
		})
	}
	if _, err := validatedQuestSurvey(&o.QuestSurveyScanner{SiteId: proto.String("site"), Alive: proto.Bool(true)}); err == nil {
		t.Fatal("live scanner without exact target")
	}
}

func TestWorldSiteCensusStatesAndThreatPresence(t *testing.T) {
	for state := o.WorldSiteState_WORLD_SITE_STATE_UNKNOWN; state <= o.WorldSiteState_WORLD_SITE_STATE_DESTROYED; state++ {
		for _, threat := range []*bool{nil, proto.Bool(false), proto.Bool(true)} {
			v := worldProgressionFixture()
			site := &o.WorldSite{Id: proto.String("WorldObject_4"), DefName: proto.String("Site"), State: state.Enum(), Tile: proto.Int32(0), LayerId: proto.Int32(0), MapId: proto.Int32(0), Threat: threat, QuestIds: []string{"quest-1", "quest-2"}}
			v.Sites = []*o.WorldSite{site}
			out, err := worldProgressionSelected(v, pbIdentity())
			if err != nil {
				t.Fatal(state, err)
			}
			row := out.Sites[0]
			if row.GetState() != state || row.Threat == nil && threat != nil || row.Threat != nil && threat == nil || row.GetThreat() != site.GetThreat() || len(row.QuestIds) != 2 {
				t.Fatal(row)
			}
		}
	}
}

func TestWorldSiteCensusRejectsInvalidEvidence(t *testing.T) {
	for name, edit := range map[string]func(*o.WorldSite){"unknown enum": func(s *o.WorldSite) { s.State = o.WorldSiteState(999).Enum() }, "negative tile": func(s *o.WorldSite) { s.Tile = proto.Int32(-1) }, "invalid distance": func(s *o.WorldSite) { s.DistanceTiles = proto.Float64(math.NaN()) }, "duplicate quest": func(s *o.WorldSite) { s.QuestIds = []string{"quest-1", "quest-1"} }, "missing loaded map": func(s *o.WorldSite) { s.State = o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED.Enum() }, "route without crew": func(s *o.WorldSite) { s.TravelTicks = proto.Int64(60000); s.Reachable = proto.Bool(true) }} {
		t.Run(name, func(t *testing.T) {
			v := worldProgressionFixture()
			site := &o.WorldSite{Id: proto.String("site"), DefName: proto.String("Site"), State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED.Enum()}
			edit(site)
			v.Sites = []*o.WorldSite{site}
			if _, err := worldProgressionSelected(v, pbIdentity()); err == nil {
				t.Fatal("accepted invalid site")
			}
		})
	}
}

func TestWorldSiteTravelCrewAndCaravanFormationCensus(t *testing.T) {
	v := worldProgressionFixture()
	v.Sites = []*o.WorldSite{{Id: proto.String("site"), DefName: proto.String("Site"), State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED.Enum(), Tile: proto.Int32(42), LayerId: proto.Int32(0), Reachable: proto.Bool(true), TravelTicks: proto.Int64(90000), RoutePawnIds: []string{"pawn-1"}}}
	v.Caravans[0].Destination = proto.Int32(42)
	v.Assemblies = []*o.CaravanAssembly{{Id: proto.String("Lord_4"), MapId: proto.Int32(0), Pawns: []*c.Ref{{Id: proto.String("pawn-1")}}}}
	out, err := worldProgressionSelected(v, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if out.Sites[0].GetTravelTicks() != 90000 || out.Caravans[0].Destination == nil || *out.Caravans[0].Destination != 42 || out.Assemblies[0].MapID != 0 || out.Assemblies[0].PawnIDs[0] != "pawn-1" {
		t.Fatal(out)
	}
}

func TestQuestGravEngineCensusExactIdentityAndProgress(t *testing.T) {
	for _, spawned := range []bool{false, true} {
		v := worldProgressionFixture()
		engine := &o.QuestGravEngine{EngineId: proto.String("Building_GravEngine42"), Spawned: proto.Bool(spawned), Inspected: proto.Bool(false)}
		if spawned {
			engine.MapId = proto.Int32(0)
			engine.Cell = &c.Cell{X: proto.Int32(12), Z: proto.Int32(9)}
			engine.EligiblePawnIds = []string{"pawn-1"}
			engine.InspectingPawnIds = []string{"pawn-2"}
		}
		v.Quests[0].Objectives = []*o.QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_INSPECT_GRAV_ENGINE.Enum(), GravEngine: engine}}
		out, err := worldProgressionSelected(v, pbIdentity())
		if err != nil {
			t.Fatal(err)
		}
		actual := out.Quests[0].Objectives[0].GravEngine
		if actual.GetEngineId() != engine.GetEngineId() || actual.GetSpawned() != spawned || actual.GetInspected() {
			t.Fatal(actual)
		}
	}
	bad := &o.QuestGravEngine{EngineId: proto.String("Building_GravEngine42"), Spawned: proto.Bool(true)}
	if _, err := validatedQuestGravEngine(bad); err == nil {
		t.Fatal("accepted spawned engine without location")
	}
}
