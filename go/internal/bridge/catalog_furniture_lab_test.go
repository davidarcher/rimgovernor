package bridge

import "testing"

// The advanced lab is the other laboratory bench that links a research speed
// facility, and the analyzer is that facility; neither is chosen by name, and a
// catalog without them has neither.
func TestRoomFurnitureAdvancedLabAndAnalyzerAreChosenFromTheLinks(t *testing.T) {
	slice := buildingsSlice(t)
	f := furnitureOf(t, slice)
	if f.AdvancedLab != "HiTechResearchBench" || f.Analyzer.Def != "MultiAnalyzer" || f.Analyzer.MaxDistance != 8 || f.Analyzer.MaxSimultaneous != 1 || f.Analyzer.Adjacent {
		t.Errorf("advanced lab %q analyzer %+v", f.AdvancedLab, f.Analyzer)
	}
	if err := f.Validate(); err != nil {
		t.Error(err)
	}
	slice.CopyThing("HiTechResearchBench", "ZLabBench")
	slice.ScaleCosts("ZLabBench", 1, 4)
	if got := furnitureOf(t, slice).AdvancedLab; got != "ZLabBench" {
		t.Errorf("advanced lab %q, want the cheaper ZLabBench", got)
	}
	slice.Drop("ZLabBench")
	slice.Drop("MultiAnalyzer")
	if got := furnitureOf(t, slice); got.AdvancedLab != "" || got.Analyzer.Def != "" {
		t.Errorf("a catalog with no analyzer still names %q and %+v", got.AdvancedLab, got.Analyzer)
	}
}
