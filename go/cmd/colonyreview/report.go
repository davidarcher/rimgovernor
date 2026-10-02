package main

import (
	"bufio"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// Colonist is one colonist in a recorder row.
type Colonist struct {
	Name    string   `json:"name"`
	Mood    *float64 `json:"mood"`
	Food    *float64 `json:"food"`
	Rest    *float64 `json:"rest"`
	Health  *float64 `json:"health"`
	Downed  bool     `json:"downed"`
	Drafted bool     `json:"drafted"`
	Mental  string   `json:"mental"`
	Job     string   `json:"job"`
	Hediffs []string `json:"hediffs"`
}

// Event is a letter or message the game archived since the previous row.
type Event struct {
	Tick  int    `json:"tick"`
	Kind  string `json:"kind"`
	Label string `json:"label"`
}

// Row is one stats.jsonl line (ColonyReview.WriteStats).
type Row struct {
	Tick          int        `json:"tick"`
	Day           int        `json:"day"`
	Hour          int        `json:"hour"`
	Date          string     `json:"date"`
	Season        string     `json:"season"`
	Weather       string     `json:"weather"`
	OutdoorTemp   float64    `json:"outdoorTemp"`
	ColonistCount int        `json:"colonistCount"`
	Colonists     []Colonist `json:"colonists"`
	DeadColonists int        `json:"deadColonists"`
	Hostiles      int        `json:"hostiles"`
	Wealth        float64    `json:"wealth"`
	Nutrition     float64    `json:"nutrition"`
	Silver        int        `json:"silver"`
	Buildings     int        `json:"buildings"`
	HomeCells     int        `json:"homeCells"`
	Blueprints    int        `json:"blueprints"`
	Fires         int        `json:"fires"`
	Events        []Event    `json:"events"`
	Shots         []string   `json:"shots"`
	Flags         []Flag     `json:"-"`
	MeanMood      float64    `json:"-"`
	FoodDays      float64    `json:"-"`
	ColonyShot    string     `json:"-"`
	MapShot       string     `json:"-"`
	Anchor        string     `json:"-"`
}

// Summary is the run at a glance, saved in run.json for the site index.
type Summary struct {
	Meta           map[string]string `json:"meta"`
	Hours          int               `json:"hours"`
	Days           int               `json:"days"`
	FirstColonists int               `json:"first_colonists"`
	LastColonists  int               `json:"last_colonists"`
	Deaths         int               `json:"deaths"`
	MinMood        float64           `json:"min_mood"`
	MinFoodDays    float64           `json:"min_food_days"`
	FinalWealth    float64           `json:"final_wealth"`
	FinalBuildings int               `json:"final_buildings"`
	Flags          int               `json:"flags"`
	Thumb          string            `json:"thumb"`
}

// NutritionPerDay is one colonist's daily nutrition need, for food days.
const NutritionPerDay = 1.6

// Load reads stats.jsonl from dir.
func Load(dir string) ([]Row, error) {
	f, err := os.Open(filepath.Join(dir, "stats.jsonl"))
	if err != nil {
		return nil, err
	}
	defer f.Close()
	var rows []Row
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1<<20), 16<<20)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}
		var r Row
		if err := json.Unmarshal([]byte(line), &r); err != nil {
			return nil, fmt.Errorf("stats.jsonl line %d: %w", len(rows)+1, err)
		}
		rows = append(rows, r)
	}
	return rows, sc.Err()
}

// Derive fills each row's derived fields and flags and returns the summary.
func Derive(rows []Row, meta map[string]string) Summary {
	s := Summary{Meta: meta, Hours: len(rows), MinMood: 1, MinFoodDays: -1}
	for i := range rows {
		r := &rows[i]
		r.Anchor = fmt.Sprintf("t%d", r.Tick)
		var moods []float64
		for _, c := range r.Colonists {
			if c.Mood != nil {
				moods = append(moods, *c.Mood)
			}
		}
		r.MeanMood = mean(moods)
		if r.ColonistCount > 0 {
			r.FoodDays = r.Nutrition / (NutritionPerDay * float64(r.ColonistCount))
		}
		for _, shot := range r.Shots {
			if strings.HasPrefix(shot, "map-") {
				r.MapShot = shot
			} else {
				r.ColonyShot = shot
			}
		}
		var prev *Row
		if i > 0 {
			prev = &rows[i-1]
		}
		r.Flags = flags(rows[:i+1], prev)
		s.Flags += len(r.Flags)
		if len(moods) > 0 && r.MeanMood < s.MinMood {
			s.MinMood = r.MeanMood
		}
		if r.ColonistCount > 0 && (s.MinFoodDays < 0 || r.FoodDays < s.MinFoodDays) {
			s.MinFoodDays = r.FoodDays
		}
	}
	if len(rows) > 0 {
		first, last := rows[0], rows[len(rows)-1]
		s.FirstColonists, s.LastColonists = first.ColonistCount, last.ColonistCount
		s.Deaths = last.DeadColonists - first.DeadColonists
		s.FinalWealth, s.FinalBuildings = last.Wealth, last.Buildings
		s.Days = (last.Tick - first.Tick) / 60000
		s.Thumb = last.ColonyShot
	}
	return s
}

func mean(v []float64) float64 {
	if len(v) == 0 {
		return 0
	}
	t := 0.0
	for _, x := range v {
		t += x
	}
	return t / float64(len(v))
}

// Report reads the recording in in and writes the run report to out.
func Report(in, out string, meta map[string]string) error {
	rows, err := Load(in)
	if err != nil {
		return err
	}
	if len(rows) == 0 {
		return fmt.Errorf("%s: no recorded hours", in)
	}
	// The recorder's meta.json (the map) under the caller's -meta.
	merged := map[string]string{}
	if data, err := os.ReadFile(filepath.Join(in, "meta.json")); err == nil {
		if err := json.Unmarshal(data, &merged); err != nil {
			return fmt.Errorf("meta.json: %w", err)
		}
	}
	for k, v := range meta {
		merged[k] = v
	}
	summary := Derive(rows, merged)
	if err := os.MkdirAll(out, 0755); err != nil {
		return err
	}
	for _, r := range rows {
		for _, shot := range r.Shots {
			if err := copyFile(filepath.Join(in, shot), filepath.Join(out, shot)); err != nil && !os.IsNotExist(err) {
				return err
			}
		}
	}
	if err := copyFile(filepath.Join(in, "stats.jsonl"), filepath.Join(out, "stats.jsonl")); err != nil {
		return err
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
