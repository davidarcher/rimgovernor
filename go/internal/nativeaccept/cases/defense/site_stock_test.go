package defense

import "testing"

// The funnel, trap corridor, fences and doors are wood (#2134) and the case
// serves no resource family to cut any: the site stock must carry the wood
// (the 78-wall funnel alone is 5 a wall) or the layout waits on it forever.
func TestSiteStockCarriesWoodForTheWoodenTiers(t *testing.T) {
	wood, stone := 0, 0
	for _, thing := range siteStock(10, 10) {
		switch thing.Def {
		case "WoodLog":
			wood += thing.Count
		case "BlocksGranite":
			stone += thing.Count
		}
	}
	if wood < 78*5+400 {
		t.Fatalf("site stock holds %d wood, want the funnel walls plus traps, fences and doors", wood)
	}
	if stone != perimeterStacks*75 {
		t.Fatalf("site stock holds %d stone blocks, want %d", stone, perimeterStacks*75)
	}
}
