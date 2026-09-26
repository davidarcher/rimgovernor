package speedmatrix

import "testing"

// TestObservationRowProblems checks an empty or unaccounted observation
// report is refused (#656): no rows, a row without interval samples, and a
// governed row without observation hops; the governor-off row needs no hops.
func TestObservationRowProblems(t *testing.T) {
	if len(observationRowProblems(nil)) == 0 {
		t.Fatal("empty report accepted")
	}
	good := []map[string]any{
		{"case": "governor-off", "governor_off": true, "frame_interval_samples": uint64(900)},
		{"case": "uncapped", "frame_interval_samples": uint64(900), "observation_hops": uint64(40)},
	}
	if problems := observationRowProblems(good); len(problems) != 0 {
		t.Fatalf("complete report refused: %v", problems)
	}
	bad := []map[string]any{
		{"case": "uncapped", "frame_interval_samples": uint64(0), "observation_hops": uint64(40)},
		{"case": "viewer", "frame_interval_samples": uint64(9)},
	}
	if problems := observationRowProblems(bad); len(problems) != 2 {
		t.Fatalf("want two problems, got %v", problems)
	}
}
