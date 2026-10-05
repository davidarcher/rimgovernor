package affected

import (
	"bytes"
	"strings"
	"testing"
)

// A recorded `go test -short -json` stream: a marked slow test (skipped with
// the slow: message after 2.5 s), an unmarked slow one and a fast one.
const recordedShortStream = `{"Action":"run","Package":"p","Test":"TestMarked"}
{"Action":"output","Package":"p","Test":"TestMarked","Output":"    x_test.go:9: slow: git-heavy\n"}
{"Action":"skip","Package":"p","Test":"TestMarked","Elapsed":2.5}
{"Action":"run","Package":"p","Test":"TestUnmarkedSlow"}
{"Action":"pass","Package":"p","Test":"TestUnmarkedSlow","Elapsed":1.7}
{"Action":"run","Package":"p","Test":"TestFast"}
{"Action":"pass","Package":"p","Test":"TestFast","Elapsed":0.01}
{"Action":"output","Package":"p","Output":"ok  \tp\t4.2s\n"}
{"Action":"pass","Package":"p","Elapsed":4.2}
`

func TestSlowWarningNamesOnlyUnmarkedSlowTests(t *testing.T) {
	var text, warning bytes.Buffer
	slow := renderTestJSON(strings.NewReader(recordedShortStream), &text)
	warnSlow(&warning, slow)
	if got := warning.String(); !strings.Contains(got, "p.TestUnmarkedSlow took 1.7s") ||
		strings.Contains(got, "TestMarked") || strings.Contains(got, "TestFast") {
		t.Fatalf("warning = %q", got)
	}
	if text.String() != "ok  \tp\t4.2s\n" {
		t.Fatalf("rendered text = %q", text.String())
	}
}

func TestNoSlowTestsNoWarning(t *testing.T) {
	var warning bytes.Buffer
	warnSlow(&warning, nil)
	if warning.Len() != 0 {
		t.Fatalf("warning = %q", warning.String())
	}
}

// go test -json emits the test binary's package-level "PASS" as its own
// output event; plain go test prints no such line, so the renderer drops it,
// while a failing package keeps FAIL, its summary line and the failed test's
// own output.
func TestRenderDropsStrayPassKeepsFailure(t *testing.T) {
	const stream = `{"Action":"run","Package":"p","Test":"TestOK"}
{"Action":"output","Package":"p","Test":"TestOK","Output":"--- PASS: TestOK (0.00s)\n"}
{"Action":"pass","Package":"p","Test":"TestOK","Elapsed":0}
{"Action":"output","Package":"p","Output":"PASS\n"}
{"Action":"output","Package":"p","Output":"ok  \tp\t0.1s\n"}
{"Action":"pass","Package":"p","Elapsed":0.1}
{"Action":"run","Package":"q","Test":"TestBad"}
{"Action":"output","Package":"q","Test":"TestBad","Output":"    q_test.go:3: boom\n"}
{"Action":"fail","Package":"q","Test":"TestBad","Elapsed":0}
{"Action":"output","Package":"q","Output":"FAIL\n"}
{"Action":"output","Package":"q","Output":"FAIL\tq\t0.1s\n"}
{"Action":"fail","Package":"q","Elapsed":0.1}
`
	var text bytes.Buffer
	renderTestJSON(strings.NewReader(stream), &text)
	const want = "ok  \tp\t0.1s\n    q_test.go:3: boom\nFAIL\nFAIL\tq\t0.1s\n"
	if text.String() != want {
		t.Fatalf("rendered text = %q, want %q", text.String(), want)
	}
}
