package postmortem

import (
	"context"
	"database/sql"
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

// colonyGrid lists every persisted colony grid (#605) by world and load,
// read raw from the store, so a case can see which origin its site
// searches snapped to and when it was fixed.
func colonyGrid(ctx context.Context, db *sql.DB, note string) Section {
	s := Section{Name: "colony grid"}
	if db == nil {
		s.Note = note
		return s
	}
	rows, err := db.QueryContext(ctx, "SELECT colony,map_id,load_token,tick,origin_x,origin_z,pitch,axis0_x,axis0_z,axis1_x,axis1_z,source FROM colony_grids ORDER BY colony,map_id,tick")
	if err != nil {
		s.Note = "colony_grids: " + err.Error()
		return s
	}
	defer rows.Close()
	for rows.Next() {
		var colony, load, source string
		var mapID, tick, ox, oz, pitch, a0x, a0z, a1x, a1z int64
		if err := rows.Scan(&colony, &mapID, &load, &tick, &ox, &oz, &pitch, &a0x, &a0z, &a1x, &a1z, &source); err != nil {
			s.Note = "colony_grids: " + err.Error()
			return s
		}
		s.Lines = append(s.Lines, Line{Text: fmt.Sprintf("colony=%s map=%d load=%s tick=%d origin=(%d,%d) pitch=%d axes=[(%d,%d) (%d,%d)] source=%s", colony, mapID, load, tick, ox, oz, pitch, a0x, a0z, a1x, a1z, source), Evidence: "service.sqlite colony_grids"})
	}
	if len(s.Lines) == 0 {
		s.Note = "colony_grids is empty: no grid was established"
	}
	return s
}
