package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCommonRoomTargetsScaleWithUsersAndLedgerPressure(t *testing.T) {
	q := domain.Known(RoomQuality{Impressiveness: 10})
	rooms := domain.Known([]UpkeepRoom{{ID: "dining", Role: "DiningRoom", Quality: q}})
	at := func(users int, tier TechTier, pressure float64) RoomTarget {
		return CommonRoomTargets(SleepingObservation{Colonists: users, Rooms: rooms}, tier, testImpressiveness, pressure)["dining"]
	}
	if base := at(3, TechTierPowered, 0); base.Min != ImpressivenessMediocre || base.Cells != 0 {
		t.Fatalf("small colony = %+v", base)
	}
	if got := at(4, TechTierPowered, 0); got.Min != ImpressivenessDecent || got.Cells != 12 {
		t.Errorf("4 users = %+v", got)
	}
	if got := at(3, TechTierPowered, 1); got.Min != ImpressivenessDecent || got.Cells != 0 {
		t.Errorf("ledger pressure = %+v", got)
	}
	if got := at(4, TechTierPowered, 1); got.Min != ImpressivenessSlightlyImpressive {
		t.Errorf("users and pressure = %+v", got)
	}
	// Capped at two tiers above and by Spacer; cells capped.
	if got := at(40, TechTierMasonry, 5); got.Min != ImpressivenessDecent || got.Cells != 120 {
		t.Errorf("capped = %+v", got)
	}
	if got := at(40, TechTierSpacer, 5); got.Min != ImpressivenessSlightlyImpressive {
		t.Errorf("spacer = %+v", got)
	}
	for users := 0; users < 30; users++ {
		for _, tier := range []TechTier{TechTierCamp, TechTierMasonry, TechTierPowered, TechTierIndustrial, TechTierSpacer} {
			if at(users, tier, 0).Min < testImpressiveness.Baseline(tier) {
				t.Fatalf("users %d tier %d below baseline", users, tier)
			}
		}
	}
}

func TestCommonRoomPressureSumsRoomThoughtLoss(t *testing.T) {
	ledger := MoodLedger{Sources: []MoodLedgerSource{{Def: "NeedRoomSize", Lost: -6}, {Def: "AteInImpressiveDiningRoom", Lost: -4}, {Def: "SleptOutside", Lost: -9}}}
	if got := CommonRoomPressure(ledger); got != 10 {
		t.Errorf("pressure = %v", got)
	}
}
