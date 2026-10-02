// Package logdigest turns the controller's service log into the rows a
// player reads: warnings, errors and the few INFO lines that are events,
// with repeats of the same message collapsed into one counted row.
//
// A line is `<time> tick=<n> LEVEL [component] message`; a line that does
// not start that way (a stack trace) belongs to the entry before it.
package logdigest

import (
	"regexp"
	"sort"
	"strconv"
	"strings"
)

// Row is one distinct message: every line that reads the same once its
// numbers and ids are blanked.
type Row struct {
	Level     string
	Component string
	Message   string // the latest occurrence's message
	Count     int
	FirstTime string
	LastTime  string
	FirstTick int64
	LastTick  int64
	Detail    string // the latest occurrence in full, trace lines included
	Problem   bool   // WARN or ERROR; the rest are events
	last      int    // the order the row was last seen in
	key       string
}

var (
	lineRE   = regexp.MustCompile(`^(\S+) tick=(-?\d+) (\w+) \[([^\]]*)\] (.*)$`)
	idRE     = regexp.MustCompile(`[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|\b[0-9a-f]{16,}\b`)
	numberRE = regexp.MustCompile(`-?\d+(\.\d+)?`)
	// noiseRE are the fields that differ on every repeat without changing
	// what happened: the trace, what woke the step, its status flags.
	noiseRE = regexp.MustCompile(`\b(trace|cause|repeated|window_ticks|admitted|running|reconciled|cleaned|deferred|retaken|combat|attempt)=("[^"]*"|\S+)`)
)

// eventWords mark an INFO line as an event worth a row.
var eventWords = []string{"raid", "hostile", "downed", "died", "dead", "killed", "fight", "combat", "retired", "gave up", "give up", "planner refused", "planner refusal cleared", "restart", "crash", "reload"}

// Digest accumulates rows as lines are fed to it.
type Digest struct {
	rows  map[string]*Row
	seq   int
	cur   *Row // the entry trace lines attach to
	curOK bool
}

// New is an empty digest.
func New() *Digest { return &Digest{rows: map[string]*Row{}} }

// Feed reads one log line (without its newline).
func (d *Digest) Feed(line string) {
	line = strings.TrimRight(line, "\r")
	m := lineRE.FindStringSubmatch(line)
	if m == nil {
		if d.curOK && strings.TrimSpace(line) != "" {
			d.cur.Detail += "\n" + line
		}
		return
	}
	d.curOK = false
	tick, _ := strconv.ParseInt(m[2], 10, 64)
	level, component, msg := m[3], m[4], m[5]
	problem := level == "WARN" || level == "ERROR"
	if !problem && !isEvent(msg) {
		return
	}
	key := level + "|" + component + "|" + Normalize(msg)
	r := d.rows[key]
	if r == nil {
		r = &Row{Level: level, Component: component, FirstTime: m[1], FirstTick: tick, key: key, Problem: problem}
		d.rows[key] = r
	}
	d.seq++
	r.Count++
	r.last = d.seq
	r.LastTime, r.LastTick, r.Message = m[1], tick, msg
	r.Detail = line
	d.cur, d.curOK = r, true
}

func isEvent(msg string) bool {
	l := strings.ToLower(msg)
	if strings.HasPrefix(l, "worker outcome") {
		return !strings.Contains(l, "stage_after=completed")
	}
	for _, w := range eventWords {
		if strings.Contains(l, w) {
			return true
		}
	}
	return false
}

// Normalize blanks the ids and numbers in msg so repeats compare equal.
func Normalize(msg string) string {
	return numberRE.ReplaceAllString(idRE.ReplaceAllString(noiseRE.ReplaceAllString(msg, ""), "#"), "#")
}

// Rows is every row, the one seen most recently first.
func (d *Digest) Rows() []Row {
	out := make([]Row, 0, len(d.rows))
	for _, r := range d.rows {
		out = append(out, *r)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].last > out[j].last })
	return out
}

// Text is the row as one copyable block.
func (r Row) Text() string {
	var b strings.Builder
	b.WriteString(r.Level + " [" + r.Component + "] x" + strconv.Itoa(r.Count) + "  " + r.FirstTime + " (tick " + strconv.FormatInt(r.FirstTick, 10) + ")")
	if r.Count > 1 {
		b.WriteString(" .. " + r.LastTime + " (tick " + strconv.FormatInt(r.LastTick, 10) + ")")
	}
	b.WriteString("\n" + r.Detail)
	return b.String()
}
