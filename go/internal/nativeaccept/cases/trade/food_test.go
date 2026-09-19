package trade

import "testing"

func TestFoodTradeRequiresNativeExchangeAndRetainedCropFloor(t *testing.T) {
	before := map[string]any{"RawRice": 6000.0, "Silver": 400.0}
	for _, tc := range []struct {
		mode  string
		after map[string]any
		pass  bool
	}{
		{"bridge", map[string]any{"Pemmican": 100.0, "Silver": 200.0}, true},
		{"bridge", map[string]any{"Pemmican": 100.0, "Silver": 400.0}, false},
		{"bridge", map[string]any{"Silver": 200.0}, false},
		{"surplus", map[string]any{"Meat_Muffalo": 40.0, "RawRice": 5000.0}, true},
		{"surplus", map[string]any{"Meat_Muffalo": 40.0, "RawRice": 4999.0}, false},
		{"surplus", map[string]any{"Meat_Muffalo": 40.0, "RawRice": 6000.0}, false},
		{"surplus", map[string]any{"RawRice": 5000.0}, false},
	} {
		if err := checkFoodTrade(tc.mode, before, tc.after); (err == nil) != tc.pass {
			t.Fatalf("%s %v: %v", tc.mode, tc.after, err)
		}
	}
}
