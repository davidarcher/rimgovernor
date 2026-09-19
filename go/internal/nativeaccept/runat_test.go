package nativeaccept

import (
	"strings"
	"testing"
)

func pausedStatus(letters []map[string]any, windows []map[string]any, forcePaused bool) map[string]any {
	rows := make([]any, 0, len(letters))
	for _, l := range letters {
		rows = append(rows, l)
	}
	wins := make([]any, 0, len(windows))
	for _, w := range windows {
		wins = append(wins, w)
	}
	return map[string]any{
		"time":    map[string]any{"paused": true, "forcePaused": forcePaused},
		"letters": rows,
		"ui":      map[string]any{"windows": wins},
	}
}

func TestClassifyPause(t *testing.T) {
	tools := []string{DismissLetterTool}
	benign := map[string]any{"id": "l1", "label": "Wanderer joins", "letterDef": "AcceptJoiner"}
	threat := map[string]any{"id": "l2", "label": "Raid", "letterDef": "ThreatBig"}
	dialog := map[string]any{"type": "Dialog_NodeTree", "title": "Trade request", "forcePause": true}

	// Running again: a transient the wait continues past.
	if cause, dismiss := classifyPause(map[string]any{"time": map[string]any{"paused": false}}, tools, "observe", 5); cause != nil || dismiss != nil {
		t.Fatalf("running game: cause=%v dismiss=%v", cause, dismiss)
	}
	// Benign letters only, with the fixture: dismissed.
	if cause, dismiss := classifyPause(pausedStatus([]map[string]any{benign}, nil, false), tools, "observe", 5); cause != nil || len(dismiss) != 1 || dismiss[0]["id"] != "l1" {
		t.Fatalf("benign letter: cause=%v dismiss=%v", cause, dismiss)
	}
	// The same letter without the fixture fails, naming it.
	cause, dismiss := classifyPause(pausedStatus([]map[string]any{benign}, nil, false), nil, "observe", 5)
	if cause == nil || dismiss != nil || !strings.Contains(cause.Error(), `letter "Wanderer joins" (AcceptJoiner)`) {
		t.Fatalf("benign letter without dismiss tool: cause=%v dismiss=%v", cause, dismiss)
	}
	// A threat letter beside a benign one fails.
	if cause, dismiss := classifyPause(pausedStatus([]map[string]any{benign, threat}, nil, false), tools, "observe", 5); cause == nil || dismiss != nil || len(cause.Letters) != 2 {
		t.Fatalf("threat letter: cause=%v dismiss=%v", cause, dismiss)
	}
	// A force-pausing window fails even with only benign letters.
	cause, dismiss = classifyPause(pausedStatus([]map[string]any{benign}, []map[string]any{dialog}, true), tools, "observe", 1705)
	if cause == nil || dismiss != nil || !cause.ForcePaused || !strings.Contains(cause.Error(), `window Dialog_NodeTree "Trade request"`) || !strings.Contains(cause.Error(), "tick 1705") {
		t.Fatalf("dialog: cause=%v dismiss=%v", cause, dismiss)
	}
	// Paused with nothing visible still fails at once.
	cause, _ = classifyPause(pausedStatus(nil, nil, false), tools, "observe", 5)
	if cause == nil || !strings.Contains(cause.Error(), "no letter or force-pausing window") {
		t.Fatalf("silent pause: %v", cause)
	}
}
