package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMedicineTierTable(t *testing.T) {
	for _, tt := range []struct {
		name, disease                                  string
		severity, immunity, severityRate, immunityRate float64
		herbal, industrial                             int64
		life, unknown                                  bool
		want                                           MedicineTier
	}{
		{"early flu", "Flu", .1, .1, .1, .2, 5, 5, false, false, MedicineHerbalTier},
		{"losing flu", "Flu", .7, .2, .2, .1, 5, 5, false, false, MedicineIndustrialTier},
		{"race tie", "Flu", .2, .2, .1, .1, 5, 5, false, false, MedicineIndustrialTier},
		{"no immunity gain", "Flu", .2, .2, .1, 0, 5, 5, false, false, MedicineIndustrialTier},
		{"plague", "Plague", .1, .1, .1, .2, 5, 5, false, false, MedicineIndustrialTier},
		{"early malaria", "Malaria", .1, .1, .1, .2, 5, 5, false, false, MedicineHerbalTier},
		{"late malaria", "Malaria", .5, .6, .1, .2, 5, 5, false, false, MedicineIndustrialTier},
		{"native life threat", "Flu", .1, .1, .1, .2, 5, 5, true, false, MedicineIndustrialTier},
		{"scarce industrial", "Plague", .1, .1, .1, .2, 5, 0, false, false, MedicineHerbalTier},
		{"no herbal", "Flu", .1, .1, .1, .2, 0, 5, false, false, MedicineIndustrialTier},
		{"glitterworld only", "Plague", .1, .1, .1, .2, 0, 0, false, false, MedicineNoMeds},
		{"unknown rates", "Flu", .1, .1, .1, .2, 5, 5, false, true, ""},
		{"immune", "Flu", .1, 1, .1, .2, 5, 5, false, false, ""},
		{"invalid rate", "Flu", .1, .1, math.NaN(), .2, 5, 5, false, false, ""},
	} {
		t.Run(tt.name, func(t *testing.T) {
			row := CareCondition{DefName: domain.Known(tt.disease), Severity: domain.Known(tt.severity), Immunity: domain.Known(tt.immunity), SeverityPerDay: domain.Known(tt.severityRate), ImmunityPerDay: domain.Known(tt.immunityRate)}
			if tt.unknown {
				row.SeverityPerDay = domain.Unknown[float64]()
			}
			stock := domain.Known([]Amount{{Resource: "MedicineHerbal", Count: tt.herbal}, {Resource: "MedicineIndustrial", Count: tt.industrial}, {Resource: "MedicineUltratech", Count: 100}})
			got, known := SelectMedicineTier(domain.Known([]CareCondition{row}), domain.Known(tt.life), stock).Value()
			if known != (tt.want != "") || got != tt.want {
				t.Fatalf("got %q known=%v, want %q", got, known, tt.want)
			}
		})
	}
}

func TestMedicineTierUnknownStockAndMixedConditions(t *testing.T) {
	flu := CareCondition{DefName: domain.Known("Flu"), Severity: domain.Known(.1), Immunity: domain.Known(.1), SeverityPerDay: domain.Known(.1), ImmunityPerDay: domain.Known(.2)}
	conditions := domain.Known([]CareCondition{flu})
	for _, stock := range []domain.Fact[[]Amount]{
		domain.Unknown[[]Amount](),
		domain.Known([]Amount{{Resource: "MedicineHerbal", Count: -1}}),
		domain.Known([]Amount{{Resource: "MedicineHerbal", Count: 1}, {Resource: "MedicineHerbal", Count: 2}}),
	} {
		if _, known := SelectMedicineTier(conditions, domain.Known(false), stock).Value(); known {
			t.Fatal("unusable stock authorized a care write")
		}
	}
	plague := flu
	plague.DefName = domain.Known("Plague")
	for _, rows := range [][]CareCondition{{flu, plague}, {plague, flu}} {
		got, known := SelectMedicineTier(domain.Known(rows), domain.Known(false), domain.Known([]Amount{{Resource: "MedicineHerbal", Count: 5}, {Resource: "MedicineIndustrial", Count: 5}})).Value()
		if !known || got != MedicineIndustrialTier {
			t.Fatal("urgent disease must dominate", got, known)
		}
	}
}
