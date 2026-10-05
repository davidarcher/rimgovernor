package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The fields_select row names the winner in reason and target and carries
// every candidate with its terms and reason in attrs (farmselect.Parse reads
// it).
func TestFieldsSelectDecision(t *testing.T) {
	plan := policy.SiteTypePlan{Kind: policy.SiteOutdoor, Crop: policy.CropChoice{Name: "Plant_Rice"}, Needed: 100, Urgent: true,
		Candidates: []policy.SiteTypeCandidate{
			{Kind: policy.SiteOutdoor, Crop: policy.CropChoice{Name: "Plant_Rice"}, Needed: 100, Cells: 40, Score: 0.5, Terms: []policy.FarmSiteTerm{{Name: "yield", Value: 2.5}}},
			{Kind: policy.SiteOutdoor, Crop: policy.CropChoice{Name: "Plant_Potato"}, Reason: "season too short"},
		}}
	d := fieldsSelectDecision(plan)
	if d.Kind != "fields_select" || d.Reason != string(policy.SiteOutdoor) || d.Target != "Plant_Rice" || d.Verdict != "selected" {
		t.Fatalf("%+v", d)
	}
	candidates := d.Attrs["candidates"].([]map[string]any)
	if d.Attrs["cells"] != 40 || d.Attrs["urgent"] != true || len(candidates) != 2 || candidates[0]["terms"].(map[string]float64)["yield"] != 2.5 || candidates[1]["reason"] != "season too short" {
		t.Fatalf("%+v", d.Attrs)
	}
}
