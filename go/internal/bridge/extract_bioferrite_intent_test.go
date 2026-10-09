package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// An extract_bioferrite setting builds one PawnSettingsIntent arm; a
// false flag still sets the arm.
func TestExtractBioferriteBuildsPawnSettingsIntent(t *testing.T) {
	for _, want := range []bool{true, false} {
		value, err := domain.NewExtractBioferriteSetting("entity-7", want)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewPawnSettingsAction("bio1", value)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		got := wire.GetPawnSettings()
		if _, ok := got.GetSetting().(*op.PawnSettingsIntent_ExtractBioferrite); !ok || got.GetPawnId() != "entity-7" || got.GetExtractBioferrite() != want {
			t.Fatalf("%v: %v", want, wire)
		}
	}
}
