package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	operationspb "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func huntCensusFacts(bills bool) *o.ColonyFactsSnapshot {
	census := &o.HuntCensus{Hunters: []*o.HunterFacts{{
		PawnId: proto.String("ann"), Position: &c.Cell{X: proto.Int32(20), Z: proto.Int32(10)}, Downed: proto.Bool(false), InMentalState: proto.Bool(false),
		HuntingActive: proto.Bool(true), HuntingPriority: proto.Int32(3), CookingActive: proto.Bool(true),
		Weapon: &o.HuntWeaponFacts{DefName: proto.String("Gun_BoltActionRifle"), Ranged: proto.Bool(true), Verbs: []*o.HuntVerbFacts{{Melee: proto.Bool(false), AiWeapon: proto.Bool(true), Range: proto.Float64(36), ProjectileKind: o.HuntProjectileKind_HUNT_PROJECTILE_KIND_BULLET.Enum(), DamageDef: proto.String("Bullet")}}},
		Routes: []*o.HuntRoute{{PreyId: proto.String("deer"), Safe: proto.Bool(true)}}, ReachableBenches: []string{"bench"}}}}
	if bills {
		census.Benches = []*o.HuntButcherBench{{BenchId: proto.String("bench"), Usable: proto.Bool(true), Bills: []*o.HuntButcherBill{{RepeatMode: operationspb.RepeatMode_REPEAT_MODE_FOREVER.Enum(), AllowedCorpses: []string{"Corpse_Deer"}}}}}
	}
	return &o.ColonyFactsSnapshot{HuntCensus: census, Acquisition: []*o.AcquisitionFacts{{Source: bridge.NewRef("deer"), Resource: proto.String("Corpse_Deer"), Hunt: proto.Bool(true), Food: proto.Bool(true), Yield: proto.Float64(1), Fogged: proto.Bool(false), InMentalState: proto.Bool(false)}}}
}

// A hunt row with no butcher bill reaches policy as a hold with a stable reason, not as a source.
func TestHuntRowWithNoButcherBillIsHeldInGo(t *testing.T) {
	tables := heads(&o.EntityRef{Id: proto.String("deer"), DefName: proto.String("Deer"), Position: &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}})
	rows, holds := decodeAcquisition(huntCensusFacts(false), tables)
	if got, ok := rows.Value(); !ok || len(got) != 0 || len(holds) != 1 || holds[0].ID != "deer" || holds[0].Reason != "no_butcher_bill" {
		t.Fatal(got, holds)
	}
	rows, holds = decodeAcquisition(huntCensusFacts(true), tables)
	if got, ok := rows.Value(); !ok || len(got) != 1 || got[0].WeaponRange != 36 || len(holds) != 0 {
		t.Fatal(got, holds)
	}
}
