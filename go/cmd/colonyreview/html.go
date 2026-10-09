package main

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"html/template"
	"io"
	"os"
	"path/filepath"
	"sort"
)

const style = `
:root{--bg:#fafaf8;--fg:#1d1d1b;--muted:#6b6b66;--card:#fff;--line:#e2e1dc;--bad:#c0392b;--warn:#b7791f;--accent:#2f6f9f;--track:#e8e7e2}
@media (prefers-color-scheme:dark){:root{--bg:#161615;--fg:#ecebe6;--muted:#9c9b94;--card:#1f1f1d;--line:#34332f;--bad:#ef6f5e;--warn:#e3a64a;--accent:#79b4dd;--track:#2e2d2a}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,-apple-system,Segoe UI,sans-serif}
main{max-width:1280px;margin:0 auto;padding:16px}h1{font-size:22px;margin:4px 0}h2{font-size:16px;margin:0 0 8px}
a{color:var(--accent)}.muted{color:var(--muted)}.meta span{margin-right:14px}[hidden]{display:none!important}
.tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(130px,1fr));gap:8px;margin:8px 0}
.tile{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:6px 10px}.tile b{display:block;font-size:19px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px;margin-bottom:12px}
.layout{display:grid;grid-template-columns:minmax(0,1fr) 340px;gap:16px;align-items:start}
.right{position:sticky;top:12px;display:grid;gap:8px}
.viewer{position:relative;background:#000;border-radius:8px;overflow:hidden;aspect-ratio:1;max-height:60vh;margin:0 auto}
.viewer img{width:100%;height:100%;object-fit:contain;display:block}
.viewer .badge{position:absolute;left:8px;top:8px;background:rgba(0,0,0,.65);color:#fff;border-radius:6px;padding:2px 8px;font-weight:600}
#vtoggle{position:absolute;right:8px;top:8px;display:flex;border-radius:6px;overflow:hidden}
button,select{font:inherit;color:var(--fg);background:var(--card);border:1px solid var(--line);border-radius:6px;padding:4px 10px;cursor:pointer}
button:hover{border-color:var(--accent)}#vtoggle button{border-radius:0;background:rgba(0,0,0,.65);color:#ddd;border:0}#vtoggle button.on{background:var(--accent);color:#fff}
.controls{display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin-top:10px}.controls .grow{flex:1 1 220px;min-width:180px}
#play{min-width:44px;font-size:16px;background:var(--accent);color:#fff;border-color:var(--accent)}
.scrub{position:relative;padding-bottom:20px}.scrub input{width:100%;margin:0;accent-color:var(--accent)}
.marks{position:absolute;left:0;right:0;top:20px;height:18px;pointer-events:none}
.marks .day{position:absolute;transform:translateX(-50%);font-size:10px;color:var(--muted)}
.marks .flag{position:absolute;top:-4px;width:3px;height:6px;margin-left:-1px;border-radius:1px;background:var(--warn)}.marks .flag.bad{background:var(--bad)}
.hint{font-size:12px;color:var(--muted);margin:6px 0 0}
#now .pawns{width:100%;border-collapse:collapse;font-size:13px}#now .pawns th{text-align:left;font-weight:400;color:var(--muted);font-size:12px}#now .pawns td{padding:2px 8px 2px 0}
.bar{display:inline-block;width:90px;height:8px;border-radius:4px;background:var(--track);vertical-align:middle;overflow:hidden}.bar i{display:block;height:100%;background:var(--accent)}
.bar.warn i{background:var(--warn)}.bar.bad i{background:var(--bad)}.barv{font-size:12px;color:var(--muted)}
.notes{margin:6px 0 0;padding-left:18px}.notes li.bad,.bad{color:var(--bad)}.notes li.warn,.warn{color:var(--warn)}
.chips .chip{display:inline-block;background:var(--track);border-radius:10px;padding:0 8px;margin:2px 4px 2px 0;font-size:12px}
.chart{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:6px 8px}
.chead{display:flex;justify-content:space-between}.crange{display:flex;justify-content:space-between;font-size:11px}
.plot{position:relative;height:64px;cursor:ew-resize;touch-action:none}.plot svg{width:100%;height:100%;display:block}
.plot path{fill:none;stroke:var(--accent);stroke-width:1.5;vector-effect:non-scaling-stroke}
.strip{display:flex;align-items:center;gap:6px;margin-top:4px}.sname{flex:0 0 52px;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.strip .plot{flex:1;height:16px;background:var(--track);border-radius:3px;overflow:hidden}.strip canvas{width:100%;height:100%;display:block}
.plot .cursor{position:absolute;top:0;bottom:0;width:0;border-left:2px solid var(--bad);pointer-events:none}
details.card>summary{cursor:pointer;font-weight:600}details.card[open]>summary{margin-bottom:8px}
.flags{list-style:none;padding:0;margin:0;max-height:300px;overflow:auto}
.flags li{padding:3px 6px;border-bottom:1px solid var(--line)}.flags li.on{background:var(--track)}
table.runs{width:100%;border-collapse:collapse}table.runs td,table.runs th{padding:6px;border-bottom:1px solid var(--line);text-align:left;vertical-align:middle}
table.runs img{width:72px;height:72px;object-fit:cover;border-radius:4px}
@media (max-width:900px){.layout{grid-template-columns:1fr}.right{position:static;grid-template-columns:repeat(auto-fill,minmax(240px,1fr))}}
`

var funcs = template.FuncMap{
	"pct1": func(v float64) string { return fmt.Sprintf("%.0f%%", v*100) },
	"f1":   func(v float64) string { return fmt.Sprintf("%.1f", v) },
	"f0":   func(v float64) string { return fmt.Sprintf("%.0f", v) },
	"f2":   func(v float64) string { return fmt.Sprintf("%.2f", v) },
	"sgn": func(v *float64, format string) string {
		if v == nil {
			return "unknown"
		}
		return fmt.Sprintf(format, *v)
	},
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
}

//go:embed player.js
var playerJS string

var runPage = template.Must(template.New("run").Funcs(funcs).Parse(`<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Colony review {{.S.Meta.date}}</title><style>` + style + `</style></head><body><main>
<p><a href="../index.html">All runs</a></p>
<h1>Colony review {{.S.Meta.date}}</h1>
<p class="meta muted">{{range $k, $v := .S.Meta}}<span>{{$k}}: {{$v}}</span>{{end}}</p>
{{if .S.Error}}<p class="bad">{{if .S.Hours}}The run ended early{{else}}The run failed{{end}}: {{.S.Error}}</p>{{end}}
{{if .S.Hours}}<div class="tiles">
<div class="tile"><span class="muted">Score</span><b>{{if .S.Score.Scalar}}{{f1 (deref .S.Score.Scalar)}}{{else}}unknown{{end}}</b></div>
<div class="tile"><span class="muted">Colonists</span><b>{{.S.FirstColonists}} → {{.S.LastColonists}}</b></div>
<div class="tile"><span class="muted">Lowest mean mood</span><b>{{pct1 .S.MinMood}}</b></div>
<div class="tile"><span class="muted">Lowest food runway</span><b>{{f1 .S.MinFoodDays}} days</b></div>
<div class="tile"><span class="muted">Final wealth</span><b>{{f0 .S.FinalWealth}}</b></div>
<div class="tile"><span class="muted">Hours · flags</span><b>{{.S.Hours}} · <span class="{{if .S.Flags}}warn{{end}}">{{.S.Flags}}</span></b></div>
</div>{{end}}
<div class="layout"><div class="left">
<p id="empty" class="card muted">{{if .S.Error}}Nothing was recorded.{{else}}No screenshots were recorded for this run.{{end}}</p>
<section id="player" class="card">
<div class="viewer"><img id="shot" alt="The colony at the chosen hour"><span class="badge" id="when"></span>
<div id="vtoggle"><button class="on" data-view="colony">Colony</button><button data-view="map">Whole map</button></div></div>
<div class="controls">
<button id="prevday" title="Previous day (shift+left)">⏮</button><button id="prev" title="Previous hour (left)">◀</button>
<button id="play" title="Play (space)">▶</button>
<button id="next" title="Next hour (right)">▶|</button><button id="nextday" title="Next day (shift+right)">⏭</button>
<select id="speed" title="Hours per second"><option value="2">2 h/s</option><option value="8" selected>8 h/s</option><option value="24">24 h/s</option><option value="48">48 h/s</option></select>
<span class="muted" id="clock"></span>
<button id="prevflag" title="Previous flagged hour ([)">⚑ ◀</button><button id="nextflag" title="Next flagged hour (])">⚑ ▶</button>
</div>
<div class="scrub"><input type="range" id="seek" min="0" max="0" value="0" step="1" aria-label="Hour of the run"><div class="marks" id="marks"></div></div>
<p class="hint">Space plays and pauses, arrows step an hour, shift+arrows a day, [ and ] jump between flagged hours (red and amber marks). Click or drag a chart to seek.</p>
</section>
<section id="now" class="card"></section>
{{if .Flagged}}<details class="card"><summary>Flagged hours ({{.S.Flags}})</summary><ul class="flags" id="flaglist">{{range .Flagged}}{{$a := .Anchor}}{{$d := .Label}}{{$t := .Tick}}{{range .Flags}}<li class="{{.Severity}}" data-t="{{$t}}"><a href="#{{$a}}">{{$d}}</a> {{.Text}}</li>{{end}}{{end}}</ul></details>{{end}}
<details class="card"><summary>Score and storage</summary>
<p>Score <b>{{if .S.Score.Scalar}}{{f1 (deref .S.Score.Scalar)}} / 100{{else}}unknown{{end}}</b> <span class="muted">weighted scalar of the components below; a signal, not a verdict.</span></p>
<table>{{range .S.Score.Components}}<tr><td>{{.Name}}</td><td>{{if .Value}}{{f2 (deref .Value)}} {{.Unit}}{{else}}<span class="muted">unknown</span>{{end}}</td><td>{{if .Score}}{{f2 (deref .Score)}}{{end}}</td><td class="muted">weight {{f1 .Weight}}{{if .Note}} · {{.Note}}{{end}}</td></tr>{{end}}</table>
{{with .S.Delta}}<h2>Against the previous night</h2>{{if eq .Status "no_baseline"}}<p class="muted">No earlier run with this seed to compare with (unknown, not zero).</p>{{else}}<p>Score {{sgn .Scalar "%+.1f"}} points against {{.Baseline}}{{if .BaselineCommit}} ({{.BaselineCommit}}){{end}}. <span class="muted">One run per seed: a signal, not a verdict.</span></p>
<table>{{range .Components}}<tr><td>{{.Name}}</td><td>{{sgn .Delta "%+.2f"}}</td><td class="muted">{{if .ValueDelta}}{{sgn .ValueDelta "%+.2f"}} in its unit{{end}}</td></tr>{{end}}</table>{{end}}{{end}}
<h2>Storage at the end</h2>{{if .S.Zones}}<table>{{range .S.Zones}}<tr><td>{{.Role}}</td><td>{{.Zones}} zone{{if ne .Zones 1}}s{{end}}</td><td>{{.Used}} of {{.Cells}} cells in use</td></tr>{{end}}</table>
<p class="muted">Starting supplies {{if .S.SuppliesForbidden}}<span class="warn">still forbidden</span>{{else}}allowed{{end}}</p>{{else}}<p class="muted">No stockpile review filed.</p>{{end}}
</details>
</div><aside class="right" id="charts"></aside></div>
<script>var DATA={{.Data}};</script>
<script>{{.Script}}</script>
</main></body></html>
`))

// frameDTO is one hour as the player reads it (player.js); keys are short
// because a run is hundreds of frames.
type frameDTO struct {
	Tick   int         `json:"t"`
	Day    int         `json:"d"`
	Label  string      `json:"l"`
	Shot   string      `json:"s,omitempty"`
	Map    string      `json:"m,omitempty"`
	Sample string      `json:"sam,omitempty"`
	Err    string      `json:"err,omitempty"`
	N      *int64      `json:"n,omitempty"`
	Mood   *float64    `json:"mood,omitempty"`
	Food   *float64    `json:"food,omitempty"`
	Wealth *float64    `json:"wealth,omitempty"`
	Tier   string      `json:"tier,omitempty"`
	Pawns  []pawnDTO   `json:"p,omitempty"`
	Zones  []zoneDTO   `json:"z,omitempty"`
	Forbid bool        `json:"fb,omitempty"`
	Flags  []flagDTO   `json:"f,omitempty"`
	Change []string    `json:"c,omitempty"`
	Unmet  [][2]string `json:"u"` // concern id, status, for each concern in deficit
}

type pawnDTO struct {
	Label  string   `json:"l"`
	Mood   *float64 `json:"m"`
	Food   *float64 `json:"f"`
	Downed bool     `json:"d,omitempty"`
}

type zoneDTO struct {
	Role  string `json:"r"`
	Zones int    `json:"z"`
	Cells int    `json:"c"`
	Used  int    `json:"u"`
}

type flagDTO struct {
	Severity string `json:"s"`
	Text     string `json:"t"`
}

func frames(rows []Row) []frameDTO {
	out := make([]frameDTO, 0, len(rows))
	for _, r := range rows {
		c := r.Census
		f := frameDTO{Tick: r.Tick, Day: r.Day, Label: r.Label, Shot: r.ColonyShot, Map: r.MapShot, Sample: r.Sampled, Err: c.Error,
			N: c.Colonists, Mood: c.MoodMean, Food: c.FoodRunwayDays, Wealth: c.WealthTotal, Change: r.Changes, Unmet: [][2]string{}}
		if c.TechTier != nil {
			f.Tier = *c.TechTier
		}
		for _, p := range c.Pawns {
			f.Pawns = append(f.Pawns, pawnDTO{p.Label, p.Mood, p.Food, p.Downed != nil && *p.Downed})
		}
		for _, z := range c.Stockpiles {
			f.Zones = append(f.Zones, zoneDTO(z))
		}
		f.Forbid = c.ForbiddenSupplies != nil && *c.ForbiddenSupplies
		for _, fl := range r.Flags {
			f.Flags = append(f.Flags, flagDTO(fl))
		}
		for _, g := range r.Concerns {
			if g.Need == "unmet" {
				f.Unmet = append(f.Unmet, [2]string{g.ID, g.Status})
			}
		}
		out = append(out, f)
	}
	return out
}

func renderRun(w io.Writer, rows []Row, s Summary) error {
	var flagged []Row
	for _, r := range rows {
		if len(r.Flags) > 0 {
			flagged = append(flagged, r)
		}
	}
	// json.Marshal escapes <, > and &, so the data cannot close the script.
	data, err := json.Marshal(map[string]any{"frames": frames(rows)})
	if err != nil {
		return err
	}
	return runPage.Execute(w, map[string]any{"S": s, "Flagged": flagged,
		"Data": template.JS(data), "Script": template.JS(playerJS)})
}

var sitePage = template.Must(template.New("site").Funcs(funcs).Parse(`<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Colony reviews</title><style>` + style + `</style></head><body><main>
<h1>Colony reviews</h1>
<p class="muted">Each run plays a fresh map for an in-game week with the governor in charge. Open one to see it hour by hour.</p>
<table class="runs"><tr><th></th><th>Run</th><th>Map</th><th>Colonists</th><th>Low food</th><th>Flags</th><th>Score</th></tr>
{{range .}}<tr><td>{{if .S.Thumb}}<a href="{{.Dir}}/index.html"><img loading="lazy" src="{{.Dir}}/{{.S.Thumb}}" alt=""></a>{{end}}</td>
<td><a href="{{.Dir}}/index.html">{{.S.Meta.date}}</a><br><span class="muted">{{.S.Meta.commit}}</span></td>
<td>{{.S.Meta.biome}}<br><span class="muted">{{.S.Meta.seed}}</span></td>
{{if .S.Hours}}<td class="{{if lt .S.LastColonists .S.FirstColonists}}bad{{end}}">{{.S.FirstColonists}} → {{.S.LastColonists}}</td>
<td>{{f1 .S.MinFoodDays}} d</td>{{else}}<td class="bad" colspan="2">failed: {{.S.Error}}</td>{{end}}<td>{{.S.Flags}}</td>
<td>{{if .S.Score.Scalar}}{{f1 (deref .S.Score.Scalar)}}{{else}}<span class="muted">unknown</span>{{end}}{{with .S.Delta}}{{if eq .Status "ok"}}{{if .Scalar}} <span class="muted">({{sgn .Scalar "%+.1f"}})</span>{{end}}{{end}}{{end}}</td></tr>{{end}}
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
