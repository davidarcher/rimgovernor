package main

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// The report reads what the run already records: result.json's timeline
// (sustainedfood.Watch: the colony census from /api/player/colony and each
// sampled goal's state, every in-game hour) and the recorder's screenshots
// under review/ (colony-<tick>.jpg, map-<tick>.jpg).

// Pawn is one census pawn.
type Pawn struct {
	Label  string   `json:"label"`
	Downed *bool    `json:"downed"`
	Mood   *float64 `json:"mood"`
	Food   *float64 `json:"food"`
}

// Census is the timeline sample's colony block.
type Census struct {
	Error          string   `json:"error"`
	Colonists      *int64   `json:"colonists"`
	FoodRunwayDays *float64 `json:"foodRunwayDays"`
	WealthTotal    *float64 `json:"wealthTotal"`
	MoodMean       *float64 `json:"moodMean"`
	Downed         int      `json:"downed"`
	BuildTier      *string  `json:"buildTier"`
	Pawns          []Pawn   `json:"pawns"`
}

// Goal is one sampled goal's state.
type Goal struct {
	ID     string
	Need   string
	Status string
}

// Row is one hour of the report: a timeline sample and its screenshot.
type Row struct {
	Tick       int
	Day, Hour  int
	Label      string
	Census     Census
	Goals      []Goal
	Changes    []string // goal need transitions since the previous row
	ColonyShot string
	MapShot    string
	Flags      []Flag
	Anchor     string
}

// Summary is the run at a glance, saved in run.json for the site index.
type Summary struct {
	Meta           map[string]string `json:"meta"`
	Hours          int               `json:"hours"`
	FirstColonists int               `json:"first_colonists"`
	LastColonists  int               `json:"last_colonists"`
	MinMood        float64           `json:"min_mood"`
	MinFoodDays    float64           `json:"min_food_days"`
	FinalWealth    float64           `json:"final_wealth"`
	Flags          int               `json:"flags"`
	Thumb          string            `json:"thumb"`
	Error          string            `json:"error,omitempty"`
}

// result is the slice of result.json the report reads.
type result struct {
	Error    string                       `json:"error"`
	Review   map[string]any               `json:"review"`
	Timeline []map[string]json.RawMessage `json:"timeline"`
}

// Load reads the case output directory: result.json and review/*.jpg.
func Load(dir string) ([]Row, result, error) {
	var res result
	data, err := os.ReadFile(filepath.Join(dir, "result.json"))
	if err != nil {
		return nil, res, err
	}
	if err := json.Unmarshal(data, &res); err != nil {
		return nil, res, fmt.Errorf("result.json: %w", err)
	}
	shots := map[string]map[int]string{"colony": {}, "map": {}}
	entries, _ := os.ReadDir(filepath.Join(dir, "review"))
	for _, e := range entries {
		var kind string
		var tick int
		name := strings.TrimSuffix(e.Name(), ".jpg")
		if k, t, ok := strings.Cut(name, "-"); ok && (k == "colony" || k == "map") {
			if n, err := strconv.Atoi(t); err == nil {
				kind, tick = k, n
			}
		}
		if kind != "" {
			shots[kind][tick] = e.Name()
		}
	}
	var rows []Row
	seen := map[int]bool{}
	for _, sample := range res.Timeline {
		var tick int
		if json.Unmarshal(sample["tick"], &tick) != nil || seen[tick] {
			continue // a sample without a live tick, or a woken duplicate
		}
		seen[tick] = true
		r := Row{Tick: tick, Day: tick/60000 + 1, Hour: tick % 60000 / 2500, Anchor: fmt.Sprintf("t%d", tick)}
		r.Label = fmt.Sprintf("Day %d, %02dh", r.Day, r.Hour)
		json.Unmarshal(sample["colony"], &r.Census)
		for key, raw := range sample {
			var g struct {
				Need   string `json:"need"`
				Status string `json:"status"`
			}
			if strings.HasPrefix(key, "Ensure") || strings.HasPrefix(key, "Maintain") {
				if json.Unmarshal(raw, &g) == nil && g.Need != "" {
					r.Goals = append(r.Goals, Goal{key, g.Need, g.Status})
				}
			}
		}
		var need, status string
		json.Unmarshal(sample["need"], &need)
		json.Unmarshal(sample["status"], &status)
		if need != "" {
			r.Goals = append(r.Goals, Goal{"EnsureFoodSupply", need, status})
		}
		sort.Slice(r.Goals, func(i, j int) bool { return r.Goals[i].ID < r.Goals[j].ID })
		rows = append(rows, r)
	}
	sort.Slice(rows, func(i, j int) bool { return rows[i].Tick < rows[j].Tick })
	// Each shot goes to the last sample at or before its tick.
	for kind, byTick := range shots {
		for tick, name := range byTick {
			i := sort.Search(len(rows), func(i int) bool { return rows[i].Tick > tick }) - 1
			if i < 0 {
				i = 0
			}
			if i >= len(rows) {
				continue
			}
			if kind == "colony" && rows[i].ColonyShot == "" {
				rows[i].ColonyShot = name
			} else if kind == "map" && rows[i].MapShot == "" {
				rows[i].MapShot = name
			}
		}
	}
	return rows, res, nil
}

// Derive fills each row's goal changes and flags and returns the summary.
func Derive(rows []Row, meta map[string]string) Summary {
	s := Summary{Meta: meta, Hours: len(rows), MinMood: 1, MinFoodDays: -1}
	for i := range rows {
		r := &rows[i]
		var prev *Row
		if i > 0 {
			prev = &rows[i-1]
			before := map[string]string{}
			for _, g := range prev.Goals {
				before[g.ID] = g.Need
			}
			for _, g := range r.Goals {
				if b, ok := before[g.ID]; ok && b != g.Need {
					r.Changes = append(r.Changes, fmt.Sprintf("%s %s → %s", g.ID, b, g.Need))
				}
			}
		}
		r.Flags = flags(rows[:i+1], prev)
		s.Flags += len(r.Flags)
		c := r.Census
		if c.MoodMean != nil && *c.MoodMean < s.MinMood {
			s.MinMood = *c.MoodMean
		}
		if c.FoodRunwayDays != nil && (s.MinFoodDays < 0 || *c.FoodRunwayDays < s.MinFoodDays) {
			s.MinFoodDays = *c.FoodRunwayDays
		}
		if r.ColonyShot != "" {
			s.Thumb = "review/" + r.ColonyShot
		}
	}
	if len(rows) > 0 {
		s.FirstColonists, s.LastColonists = colonists(rows[0]), colonists(rows[len(rows)-1])
		if w := rows[len(rows)-1].Census.WealthTotal; w != nil {
			s.FinalWealth = *w
		}
	}
	return s
}

func colonists(r Row) int {
	if r.Census.Colonists == nil {
		return 0
	}
	return int(*r.Census.Colonists)
}

// Report reads the case output in and writes the run report to out.
func Report(in, out string, meta map[string]string) error {
	rows, res, err := Load(in)
	if err != nil {
		return err
	}
	merged := map[string]string{}
	for k, v := range res.Review {
		merged[k] = fmt.Sprint(v)
	}
	for k, v := range meta {
		merged[k] = v
	}
	summary := Derive(rows, merged)
	summary.Error = res.Error
	if err := os.MkdirAll(filepath.Join(out, "review"), 0755); err != nil {
		return err
	}
	for _, r := range rows {
		for _, shot := range []string{r.ColonyShot, r.MapShot} {
			if shot == "" {
				continue
			}
			if err := copyFile(filepath.Join(in, "review", shot), filepath.Join(out, "review", shot)); err != nil {
				return err
			}
		}
	}
	data, _ := json.MarshalIndent(summary, "", "  ")
	if err := os.WriteFile(filepath.Join(out, "run.json"), data, 0644); err != nil {
		return err
	}
	f, err := os.Create(filepath.Join(out, "index.html"))
	if err != nil {
		return err
	}
	defer f.Close()
	return renderRun(f, rows, summary)
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}
