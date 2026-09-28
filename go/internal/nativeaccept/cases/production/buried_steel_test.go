package production

import "testing"

func TestBuriedSteelVerdictReplacementWalls(t *testing.T) {
	done := map[string]any{"openLeft": 0.0, "buriedLeft": 0.0, "steelStored": 50.0, "roofless": 0.0, "collapsedRocks": 0.0, "collapsing": 0.0}
	if err := buriedSteelVerdict(done, false); err != nil {
		t.Fatalf("default layout: %v", err)
	}
	if err := buriedSteelVerdict(done, true); err == nil {
		t.Fatal("stockpile beside the face passed without replacement walls")
	}
	done["replacementWalls"] = 2.0
	if err := buriedSteelVerdict(done, true); err != nil {
		t.Fatalf("walled face: %v", err)
	}
}
