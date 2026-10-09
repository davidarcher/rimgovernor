package bridge

import (
	"reflect"
	"slices"
	"sort"
	"strings"
	"testing"

	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func TestInstalledPartsMapping(t *testing.T) {
	catalog := fullCatalog(t)
	arm := &o.InstalledPart{Definition: &o.DefinitionRef{DefName: proto.String("BionicArm")}, PartDefName: proto.String("Shoulder"), PartIndex: proto.Int32(12), SpawnThingDefName: proto.String("BionicArm")}
	peg := &o.InstalledPart{Definition: &o.DefinitionRef{DefName: proto.String("PegLeg")}, PartDefName: proto.String("Leg"), PartIndex: proto.Int32(40)}
	got, err := InstalledParts(&o.PawnHealth{InstalledParts: []*o.InstalledPart{arm, peg}}, catalog)
	if err != nil {
		t.Fatal(err)
	}
	want := domain.Known([]policy.InstalledPart{
		{Hediff: "BionicArm", Part: domain.Known("Shoulder"), PartIndex: domain.Known(12), Item: domain.Known(policy.Resource("BionicArm")), Tier: 1.25},
		{Hediff: "PegLeg", Part: domain.Known("Leg"), PartIndex: domain.Known(40), Item: domain.Unknown[policy.Resource](), Tier: 0.6},
	})
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %+v", got)
	}
	if none, err := InstalledParts(&o.PawnHealth{}, catalog); err != nil || !reflect.DeepEqual(none, domain.Known([]policy.InstalledPart{})) {
		t.Fatalf("none %+v %v", none, err)
	}
	bad, err := InstalledParts(&o.PawnHealth{Issues: []*o.ReadIssue{{Field: proto.String("installed_parts")}}, InstalledParts: []*o.InstalledPart{arm}}, catalog)
	if _, known := bad.Value(); known || err != nil {
		t.Fatal("read issue must be unknown")
	}
	ghost := &o.InstalledPart{Definition: &o.DefinitionRef{DefName: proto.String("NoSuchPart")}}
	if _, err := InstalledParts(&o.PawnHealth{InstalledParts: []*o.InstalledPart{ghost}}, catalog); err == nil {
		t.Fatal("a hediff with no row must be an error")
	}
}

// legacyPartTier is the substring table PartTier read before the tier came
// from the rows, kept here to explain each difference.
func legacyPartTier(name string) float64 {
	switch {
	case strings.Contains(name, "Archotech"):
		return 1.5
	case strings.Contains(name, "Bionic"):
		return 1.25
	case strings.Contains(name, "Natural"):
		return 1
	case strings.Contains(name, "Prosthetic"):
		return 0.85
	case strings.Contains(name, "Peg"), strings.Contains(name, "Wooden"), strings.Contains(name, "Denture"):
		return 0.6
	}
	return 0.5
}

// partTierDifferences explains every vanilla recipe whose tier from the rows
// differs from the legacy name table. A recipe not listed here must agree.
var partTierDifferences = map[string]string{
	// The rows give the parts' own partEfficiency; the legacy table guessed it from the name.
	"InstallBionicSpine":           "partEfficiency 1, no better than natural (legacy 1.25 by name)",
	"InstallBionicTongue":          "partEfficiency 1, no better than natural (legacy 1.25 by name)",
	"InstallDenture":               "partEfficiency 0.8 (legacy 0.6)",
	"InstallWoodenFoot":            "partEfficiency 0.8 (legacy 0.6)",
	"InstallSimpleProstheticArm":   "partEfficiency 0.5 (legacy 0.85)",
	"InstallSimpleProstheticHeart": "partEfficiency 0.8 (legacy 0.85)",
	// Artificial replacement parts the legacy table counted as implants (0.5):
	// the game gives them their own efficiency, so a missing part they fit is
	// restored by them and the better than natural ones are elective upgrades.
	"InstallAdrenalHeart":       "artificial heart, partEfficiency 1 (legacy implant 0.5)",
	"InstallAestheticNose":      "artificial nose, partEfficiency 1 (legacy implant 0.5)",
	"InstallCochlearImplant":    "artificial ear, partEfficiency 0.65 (legacy implant 0.5)",
	"InstallCorrosiveHeart":     "artificial heart, partEfficiency 0.85 (legacy implant 0.5)",
	"InstallDetoxifierKidney":   "artificial kidney, partEfficiency 1.1 (legacy implant 0.5)",
	"InstallDetoxifierLung":     "artificial lung, partEfficiency 1.1 (legacy implant 0.5)",
	"InstallDetoxifierStomach":  "artificial stomach, partEfficiency 1.25 (legacy implant 0.5)",
	"InstallDrillArm":           "artificial arm, partEfficiency 1 (legacy implant 0.5)",
	"InstallFieldHand":          "artificial hand, partEfficiency 1 (legacy implant 0.5)",
	"InstallMetalbloodHeart":    "artificial heart, partEfficiency 1 (legacy implant 0.5)",
	"InstallNuclearStomach":     "artificial stomach, partEfficiency 1.25 (legacy implant 0.5)",
	"InstallPowerClaw":          "artificial hand, partEfficiency 1 (legacy implant 0.5)",
	"InstallReprocessorStomach": "artificial stomach, partEfficiency 1.25 (legacy implant 0.5)",
	"InstallRevenantVertebrae":  "artificial spine, partEfficiency 1 (legacy implant 0.5)",
}

// implantTier is the tier of a recipe that installs no replacement part; the
// legacy table said 0.5, the rows say none.
const implantTier = 0.0

func TestPartTiersMatchTheRecordedCatalog(t *testing.T) {
	catalog := fullCatalog(t)
	recipes := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	names := make([]string, 0, len(recipes))
	for name := range recipes {
		names = append(names, name)
	}
	sort.Strings(names)
	used := map[string]bool{}
	for _, name := range names {
		tier, err := catalog.PartTier(name)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		legacy := legacyPartTier(name)
		switch row := recipes[name].(*d.RecipeDef); row.GetWorkerClass() {
		case naturalPartWorker, artificialPartWorker:
		default:
			if tier != implantTier {
				t.Errorf("%s installs no replacement part but has tier %v", name, tier)
			}
			continue
		}
		if diff, ok := partTierDifferences[name]; ok {
			used[name] = true
			if tier == legacy {
				t.Errorf("%s agrees with the legacy tier %v; delete its explanation %q", name, tier, diff)
			}
		} else if tier != legacy {
			t.Errorf("%s: tier %v from the rows, legacy %v, unexplained", name, tier, legacy)
		}
	}
	for name := range partTierDifferences {
		if !used[name] {
			t.Errorf("explained difference %s names no replacement part recipe", name)
		}
	}
}

func TestPartTierReadsTheWorkerAndTheHediff(t *testing.T) {
	catalog := fullCatalog(t)
	for recipe, want := range map[string]float64{
		"InstallArchotechArm": 1.5, "InstallBionicLeg": 1.25, "InstallNaturalKidney": 1, "InstallPegLeg": 0.6,
		"InstallJoywire": 0, "Sterilize": 0,
	} {
		if got, err := catalog.PartTier(recipe); err != nil || got != want {
			t.Errorf("%s: %v %v, want %v", recipe, got, err, want)
		}
	}
	if _, err := catalog.PartTier("NoSuchRecipe"); err == nil {
		t.Error("a recipe with no row must be an error")
	}
	if got, err := (*DefinitionCatalog)(nil).PartTier("InstallPegLeg"); err != nil || got != 0 {
		t.Errorf("nil catalog: %v %v", got, err)
	}
}

func TestSeriousBloodLossIsTheModerateStage(t *testing.T) {
	catalog := fullCatalog(t)
	got, err := catalog.SeriousBloodLoss()
	if v, ok := got.Value(); err != nil || !ok || v != 0.3 {
		t.Fatalf("got %v %v", got, err)
	}
	if got, err := (*DefinitionCatalog)(nil).SeriousBloodLoss(); err != nil || got != domain.Unknown[float64]() {
		t.Fatalf("nil catalog: %v %v", got, err)
	}
}

func TestBodyPartFactsMatchTheRecordedCatalog(t *testing.T) {
	facts, err := fullCatalog(t).RecipeFacts()
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(facts.HarvestOrgans, []string{"Kidney", "Lung"}) {
		t.Errorf("harvest organs %v, want the legacy Kidney, Lung", facts.HarvestOrgans)
	}
	for _, kept := range []string{"Heart", "Liver", "Brain", "Neck"} {
		if !facts.VitalParts[kept] {
			t.Errorf("%s: removing it kills, but it is not a vital part", kept)
		}
	}
	// Removing one of a pair, or a stomach the liver backs up, does not kill. A
	// spine carries no vital tag, so it stays a keptBodyParts judgment.
	for _, survivable := range []string{"Kidney", "Lung", "Stomach", "Spine", "Leg", "Eye"} {
		if facts.VitalParts[survivable] {
			t.Errorf("%s: removing it is survivable, but it is a vital part", survivable)
		}
	}
}
