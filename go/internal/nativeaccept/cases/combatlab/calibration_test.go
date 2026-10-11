package combatlab

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatsim/calibrate"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// TestCommittedCalibrationReportIsCurrent keeps the recorded gate output honest:
// it must carry this build's stamp (a changed assembly hash, rules version,
// thresholds version or sweep schema invalidates it) and must be what the gate
// produces now. Re-record with `go run ./cmd/combatcalibrate -record` from go/.
func TestCommittedCalibrationReportIsCurrent(t *testing.T) {
	data, err := os.ReadFile("calibration/report.json")
	if err != nil {
		t.Fatal(err)
	}
	rec, err := calibrate.ParseReport(data)
	if err != nil {
		t.Fatal(err)
	}
	if rec.Stamp != calibrate.CurrentStamp() {
		t.Fatalf("calibration/report.json was recorded under %+v, this build is %+v: re-record it with `go run ./cmd/combatcalibrate -record`", rec.Stamp, calibrate.CurrentStamp())
	}
	in, err := CalibrationInputs(recordedcatalog.Catalog(t), "calibration")
	if err != nil {
		t.Fatal(err)
	}
	now := calibrate.Evaluate(calibrate.CurrentStamp(), in)
	text, err := os.ReadFile("calibration/report.txt")
	if err != nil {
		t.Fatal(err)
	}
	if now.Text() != string(text) || now.Pass != rec.Pass {
		t.Fatalf("calibration/report.* is not what the gate produces now: re-record it with `go run ./cmd/combatcalibrate -record`\n%s", now.Text())
	}
}

func TestEveryListedFixtureExistsAndRolesDoNotOverlap(t *testing.T) {
	seen := map[string]bool{}
	for _, name := range append(append([]string{}, CalibrationFixtures...), HeldOutFixtures...) {
		if seen[name] {
			t.Errorf("fixture %s is in both lists or twice", name)
		}
		seen[name] = true
		if _, err := Baseline(name); err != nil {
			t.Errorf("fixture %s has no baseline: %v", name, err)
		}
	}
}
