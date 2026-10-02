package main

import (
	"encoding/json"
	"fmt"
	"html/template"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const style = `
:root{--bg:#fafaf8;--fg:#1d1d1b;--muted:#6b6b66;--card:#fff;--line:#e2e1dc;--bad:#c0392b;--warn:#b7791f;--accent:#2f6f9f}
@media (prefers-color-scheme:dark){:root{--bg:#161615;--fg:#ecebe6;--muted:#9c9b94;--card:#1f1f1d;--line:#34332f;--bad:#ef6f5e;--warn:#e3a64a;--accent:#79b4dd}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,-apple-system,Segoe UI,sans-serif}
main{max-width:1200px;margin:0 auto;padding:16px}h1{font-size:22px;margin:4px 0}h2{font-size:17px;margin:24px 0 8px}
a{color:var(--accent)}.muted{color:var(--muted)}.meta span{margin-right:14px}
.tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:8px}
.tile{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:8px 10px}.tile b{display:block;font-size:20px}
.charts{display:grid;grid-template-columns:repeat(auto-fill,minmax(270px,1fr));gap:8px}
.chart{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:8px}.chart svg{width:100%;height:70px;display:block}
.chart polyline{fill:none;stroke:var(--accent);stroke-width:1.5;vector-effect:non-scaling-stroke}
.flags{list-style:none;padding:0;margin:0;max-height:320px;overflow:auto;background:var(--card);border:1px solid var(--line);border-radius:8px}
.flags li{padding:4px 10px;border-bottom:1px solid var(--line)}.bad{color:var(--bad)}.warn{color:var(--warn)}
.days{display:flex;gap:8px;overflow-x:auto;padding-bottom:6px}.days figure{margin:0;flex:0 0 220px}.days img{width:220px;height:220px;object-fit:cover;border-radius:6px}
.hours{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:10px}
.hour{background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}.hour.hasbad{border-color:var(--bad)}
.hour img{width:100%;aspect-ratio:1;object-fit:cover;display:block;background:#000}.hour .body{padding:8px 10px}
.hour table{width:100%;border-collapse:collapse;font-size:12px}.hour td{padding:1px 4px 1px 0;vertical-align:top}
.hour .job{color:var(--muted)}.hour ul{margin:4px 0 0;padding-left:16px;font-size:12px}
.filter{margin:8px 0}figcaption{font-size:12px;color:var(--muted)}
table.runs{width:100%;border-collapse:collapse}table.runs td,table.runs th{padding:6px;border-bottom:1px solid var(--line);text-align:left;vertical-align:middle}
table.runs img{width:72px;height:72px;object-fit:cover;border-radius:4px}
@media (max-width:600px){.hours{grid-template-columns:1fr}}
`

var funcs = template.FuncMap{
	"pct": func(v *float64) string {
		if v == nil {
			return "–"
		}
		return fmt.Sprintf("%.0f%%", *v*100)
	},
	"pct1": func(v float64) string { return fmt.Sprintf("%.0f%%", v*100) },
	"f1":   func(v float64) string { return fmt.Sprintf("%.1f", v) },
	"f0":   func(v float64) string { return fmt.Sprintf("%.0f", v) },
	"deref": func(v any) any {
		switch p := v.(type) {
		case *float64:
			return *p
		case *string:
			return *p
		case *bool:
			return *p
		}
		return v
	},
	"hasBad": func(fs []Flag) bool {
		for _, f := range fs {
			if f.Severity == "bad" {
				return true
			}
		}
		return false
	},
}

var runPage = template.Must(template.New("run").Funcs(funcs).Parse(`<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Colony review {{.S.Meta.date}}</title><style>` + style + `</style></head><body><main>
<p><a href="../index.html">All runs</a></p>
<h1>Colony review {{.S.Meta.date}}</h1>
<p class="meta muted">{{range $k, $v := .S.Meta}}<span>{{$k}}: {{$v}}</span>{{end}}</p>
{{if .S.Error}}<p class="bad">The run ended early: {{.S.Error}}</p>{{end}}
<div class="tiles">
<div class="tile"><span class="muted">Colonists</span><b>{{.S.FirstColonists}} → {{.S.LastColonists}}</b></div>
<div class="tile"><span class="muted">Lowest mean mood</span><b>{{pct1 .S.MinMood}}</b></div>
<div class="tile"><span class="muted">Lowest food runway</span><b>{{f1 .S.MinFoodDays}} days</b></div>
<div class="tile"><span class="muted">Final wealth</span><b>{{f0 .S.FinalWealth}}</b></div>
<div class="tile"><span class="muted">Hours recorded</span><b>{{.S.Hours}}</b></div>
<div class="tile"><span class="muted">Flags</span><b class="{{if .S.Flags}}warn{{end}}">{{.S.Flags}}</b></div>
</div>
<h2>Trends</h2><div class="charts">{{range .Charts}}<div class="chart"><span class="muted">{{.Label}}</span> <b>{{.Last}}</b>
<svg viewBox="0 0 {{.W}} 100" preserveAspectRatio="none"><polyline points="{{.Points}}"/></svg>
<span class="muted">{{.Min}} – {{.Max}}</span></div>{{end}}</div>
<h2>Flags</h2>{{if .Flagged}}<ul class="flags">{{range .Flagged}}{{$a := .Anchor}}{{$d := .Label}}{{range .Flags}}<li class="{{.Severity}}"><a href="#{{$a}}">{{$d}}</a> {{.Text}}</li>{{end}}{{end}}</ul>{{else}}<p class="muted">None.</p>{{end}}
<h2>Whole map, daily</h2><div class="days">{{range .Rows}}{{if .MapShot}}<figure><a href="review/{{.MapShot}}"><img loading="lazy" src="review/{{.MapShot}}" alt="Map {{.Label}}"></a><figcaption>{{.Label}}</figcaption></figure>{{end}}{{end}}</div>
<h2>Hour by hour</h2>
<label class="filter"><input type="checkbox" id="only"> Only hours with flags or goal changes</label>
<div class="hours" id="hours">{{range .Rows}}
<section class="hour{{if hasBad .Flags}} hasbad{{end}}" id="{{.Anchor}}" data-flags="{{len .Flags}}{{len .Changes}}">
{{if .ColonyShot}}<a href="review/{{.ColonyShot}}"><img loading="lazy" src="review/{{.ColonyShot}}" alt="Colony {{.Label}}"></a>{{end}}
<div class="body"><b>{{.Label}}</b> <span class="muted">tick {{.Tick}}{{if .Sampled}} · facts from {{.Sampled}}{{end}}</span><br>
{{with .Census}}<span class="muted">food runway {{if .FoodRunwayDays}}{{f1 (deref .FoodRunwayDays)}}d{{end}} · wealth {{if .WealthTotal}}{{f0 (deref .WealthTotal)}}{{end}}{{if .BuildTier}} · {{deref .BuildTier}}{{end}}</span>
<table>{{range .Pawns}}<tr><td>{{.Label}}</td><td>mood {{pct .Mood}}</td><td>food {{pct .Food}}</td><td>{{if .Downed}}{{if deref .Downed}}<span class="bad">downed</span>{{end}}{{end}}</td></tr>{{end}}</table>{{end}}
{{if .Flags}}<ul>{{range .Flags}}<li class="{{.Severity}}">{{.Text}}</li>{{end}}</ul>{{end}}
{{if .Changes}}<ul class="muted">{{range .Changes}}<li>{{.}}</li>{{end}}</ul>{{end}}
<details><summary class="muted">goals</summary><table>{{range .Goals}}<tr><td>{{.ID}}</td><td class="{{if eq .Need "deficit"}}warn{{end}}">{{.Need}}</td><td class="job">{{.Status}}</td></tr>{{end}}</table></details>
</div></section>{{end}}</div>
<script>
document.getElementById('only').addEventListener('change',e=>{for(const s of document.querySelectorAll('#hours .hour'))s.style.display=e.target.checked&&s.dataset.flags==='00'?'none':''})
</script>
</main></body></html>
`))

// chart is one sparkline over the run's hours.
type chart struct {
	Label, Last, Min, Max, Points string
	W                             int
}

func newChart(label string, rows []Row, value func(Row) float64, format string) chart {
	c := chart{Label: label, W: max(1, len(rows)-1)}
	if len(rows) == 0 {
		return c
	}
	lo, hi := value(rows[0]), value(rows[0])
	for _, r := range rows {
		lo, hi = min(lo, value(r)), max(hi, value(r))
	}
	span := hi - lo
	if span == 0 {
		span = 1
	}
	var pts []string
	for i, r := range rows {
		pts = append(pts, fmt.Sprintf("%d,%.1f", i, 95-90*(value(r)-lo)/span))
	}
	c.Points = strings.Join(pts, " ")
	c.Last, c.Min, c.Max = fmt.Sprintf(format, value(rows[len(rows)-1])), fmt.Sprintf(format, lo), fmt.Sprintf(format, hi)
	return c
}

func renderRun(w io.Writer, rows []Row, s Summary) error {
	var flagged []Row
	for _, r := range rows {
		if len(r.Flags) > 0 {
			flagged = append(flagged, r)
		}
	}
	val := func(p *float64) float64 {
		if p == nil {
			return 0
		}
		return *p
	}
	charts := []chart{
		newChart("Colonists", rows, func(r Row) float64 { return float64(colonists(r)) }, "%.0f"),
		newChart("Mean mood %", rows, func(r Row) float64 { return val(r.Census.MoodMean) * 100 }, "%.0f"),
		newChart("Food runway days", rows, func(r Row) float64 { return val(r.Census.FoodRunwayDays) }, "%.1f"),
		newChart("Wealth", rows, func(r Row) float64 { return val(r.Census.WealthTotal) }, "%.0f"),
		newChart("Goals in deficit", rows, func(r Row) float64 {
			n := 0
			for _, g := range r.Goals {
				if g.Need == "deficit" {
					n++
				}
			}
			return float64(n)
		}, "%.0f"),
	}
	return runPage.Execute(w, map[string]any{"S": s, "Rows": rows, "Flagged": flagged, "Charts": charts})
}

var sitePage = template.Must(template.New("site").Funcs(funcs).Parse(`<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Colony reviews</title><style>` + style + `</style></head><body><main>
<h1>Colony reviews</h1>
<p class="muted">Each run plays a fresh map for an in-game week with the governor in charge. Open one to see it hour by hour.</p>
<table class="runs"><tr><th></th><th>Run</th><th>Map</th><th>Colonists</th><th>Low food</th><th>Flags</th></tr>
{{range .}}<tr><td>{{if .S.Thumb}}<a href="{{.Dir}}/index.html"><img loading="lazy" src="{{.Dir}}/{{.S.Thumb}}" alt=""></a>{{end}}</td>
<td><a href="{{.Dir}}/index.html">{{.S.Meta.date}}</a><br><span class="muted">{{.S.Meta.commit}}</span></td>
<td>{{.S.Meta.biome}}<br><span class="muted">{{.S.Meta.seed}}</span></td>
<td class="{{if lt .S.LastColonists .S.FirstColonists}}bad{{end}}">{{.S.FirstColonists}} → {{.S.LastColonists}}</td>
<td>{{f1 .S.MinFoodDays}} d</td><td>{{.S.Flags}}</td></tr>{{end}}
</table></main></body></html>
`))

type siteRun struct {
	Dir string
	S   Summary
}

// Site indexes every run dir under runs into out, newest first by
// directory name.
func Site(runs, out string) error {
	entries, err := os.ReadDir(runs)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(out, 0755); err != nil {
		return err
	}
	var list []siteRun
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		data, err := os.ReadFile(filepath.Join(runs, e.Name(), "run.json"))
		if err != nil {
			continue
		}
		var s Summary
		if json.Unmarshal(data, &s) != nil {
			continue
		}
		if abs(runs) != abs(out) {
			if err := os.CopyFS(filepath.Join(out, e.Name()), os.DirFS(filepath.Join(runs, e.Name()))); err != nil {
				return err
			}
		}
		list = append(list, siteRun{e.Name(), s})
	}
	sort.Slice(list, func(i, j int) bool { return list[i].Dir > list[j].Dir })
	f, err := os.Create(filepath.Join(out, "index.html"))
	if err != nil {
		return err
	}
	defer f.Close()
	return sitePage.Execute(f, list)
}

func abs(p string) string {
	a, _ := filepath.Abs(p)
	return a
}
