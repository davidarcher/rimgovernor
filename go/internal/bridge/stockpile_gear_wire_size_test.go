package bridge

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"google.golang.org/protobuf/proto"
)

// The armory and wardrobe filters list every armor thingDef, so the settings
// message grows with the catalog. Measured on the 31 armor defs of
// Core and all five DLCs (the game's Soldier-without-Worker apparel, see
// ApparelIsArmor): about 0.9 KB per store, one patch when a gear zone is
// created or its filter changes. The real ceiling is the transport frame
// (maxResponseBytes, 50 MiB): a catalog of ten thousand armor defs, far past
// any modlist, still encodes well inside it, so no category or tag selector is
// needed and no cap is imposed.
func TestGearFilterSettingsSizeStaysFarUnderTheFrame(t *testing.T) {
	t.Parallel()
	size := func(armor []string) (armory, wardrobe int) {
		a, err := domain.ArmoryFilter(armor)
		if err != nil {
			t.Fatal(err)
		}
		w, err := domain.WardrobeFilter(armor)
		if err != nil {
			t.Fatal(err)
		}
		measure := func(f domain.StockpileFilter) int {
			z, err := domain.NewFilteredStockpileZone(f, domain.NormalPriority, stockpileTestRectangle([]domain.Cell{{X: 1, Z: 1}}))
			if err != nil {
				t.Fatal(err)
			}
			return proto.Size(stockpileSettings(z))
		}
		return measure(a), measure(w)
	}
	vanilla := []string{"Apparel_KidHelmet", "Apparel_MechlordSuit", "Apparel_AdvancedHelmet", "Apparel_ArmorHelmetRecon", "Apparel_ArmorRecon", "Apparel_FlakJacket", "Apparel_FlakPants", "Apparel_FlakVest", "Apparel_PlateArmor", "Apparel_PowerArmor", "Apparel_PowerArmorHelmet", "Apparel_SimpleHelmet", "Apparel_WarMask", "Apparel_WarVeil", "Apparel_ArmorCataphract", "Apparel_ArmorCataphractPhoenix", "Apparel_ArmorCataphractPrestige", "Apparel_ArmorHelmetCataphract", "Apparel_ArmorHelmetCataphractPrestige", "Apparel_ArmorHelmetReconPrestige", "Apparel_ArmorLocust", "Apparel_ArmorMarineGrenadier", "Apparel_ArmorMarineHelmetPrestige", "Apparel_ArmorMarinePrestige", "Apparel_ArmorReconPrestige", "Apparel_EltexSkullcap", "Apparel_Gunlink", "Apparel_PsyfocusHelmet", "Apparel_PsyfocusRobe", "Apparel_PsyfocusShirt", "Apparel_PsyfocusVest"}
	armory, wardrobe := size(vanilla)
	t.Logf("%d armor defs: armory settings %d bytes, wardrobe %d bytes", len(vanilla), armory, wardrobe)
	var huge []string
	for i := range 10000 {
		huge = append(huge, fmt.Sprintf("Mod%04d_Apparel_ArmorSuitOfTheLongestPlausibleName", i))
	}
	armory, wardrobe = size(huge)
	t.Logf("%d armor defs: armory settings %d bytes, wardrobe %d bytes", len(huge), armory, wardrobe)
	if armory >= maxResponseBytes/10 || wardrobe >= maxResponseBytes/10 {
		t.Fatalf("settings of %d armor defs take %d and %d bytes", len(huge), armory, wardrobe)
	}
}
