package postmortem

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Use the latest successful recorded routines response, never reconstruct
// current safety from history alone or combine different observations.
func extentEligibility(dir string) Section {
	s := Section{Name: "colony extent", Note: "no recorded /api/routines eligibility view"}
	paths, _ := filepath.Glob(filepath.Join(dir, "service*", "http-*.json"))
	sort.Slice(paths, func(i, j int) bool { return filepath.Base(paths[i]) < filepath.Base(paths[j]) })
	for i := len(paths) - 1; i >= 0; i-- {
		data, err := os.ReadFile(paths[i])
		if err != nil {
			continue
		}
		var row struct {
			Path     string `json:"path"`
			Status   int    `json:"status"`
			Response struct {
				Extent *policy.ExtentEligibilityView `json:"extentEligibility"`
				Reach  policy.ResourceReachDecision  `json:"resourceReach"`
			} `json:"response"`
		}
		if json.Unmarshal(data, &row) != nil || row.Path != "/api/routines" || row.Status != 200 || row.Response.Extent == nil {
			continue
		}
		rel, _ := filepath.Rel(dir, paths[i])
		rel = filepath.ToSlash(rel)
		v := row.Response.Extent
		s.Note = ""
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("extent known=%t reason=%s; resource reach stage=%s reason=%s", v.Known, v.Reason, row.Response.Reach.Stage, row.Response.Reach.Reason), Evidence: rel})
		for j, r := range v.Regions {
			if j >= maxLines-1 {
				s.Note = fmt.Sprintf("%d more regions in recorded response", len(v.Regions)-j)
				break
			}
			s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("region=%d stage=%s origins=%v facilities=%v active=%v eligible=%t holds=%v", r.Region, r.Stage, r.Origins, r.Facilities, r.ActiveFacilities, r.Eligible, r.HoldReasons), Evidence: rel})
		}
		return s
	}
	return s
}
