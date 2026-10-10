package policy

import (
	"go/ast"
	"go/parser"
	"go/token"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

func TestEveryCauseHasBoundedASCIIWording(t *testing.T) {
	seen := map[Cause]bool{}
	for _, c := range Causes {
		if seen[c] {
			t.Errorf("cause %q listed twice", c)
		}
		seen[c] = true
		if err := c.Validate(); err != nil {
			t.Error(err)
		}
		if causeWording[c] == "" {
			t.Errorf("cause %q has no wording", c)
		}
		for _, subject := range []string{"", "bench", "café " + strings.Repeat("x", 200)} {
			w := Wording(c, subject)
			if len(w) > 160 {
				t.Errorf("wording for %q is %d bytes", c, len(w))
			}
			for _, r := range w {
				if r < ' ' || r > '~' {
					t.Errorf("wording for %q has non-ASCII %q", c, r)
				}
			}
		}
	}
	if len(causeWording) != len(Causes) {
		t.Errorf("%d wordings for %d causes", len(causeWording), len(Causes))
	}
}

func TestCauseSetIsClosed(t *testing.T) {
	if Cause("not_a_cause").Validate() == nil || Cause("").Validate() == nil {
		t.Fatal("unknown cause validated")
	}
}

func TestUnreadFactsAndFixedBlockedMapToACause(t *testing.T) {
	for _, f := range UnreadFacts {
		if err := CauseOfUnread(f).Validate(); err != nil {
			t.Error(err)
		}
	}
	for _, b := range fixedBlocked {
		c, ok := CauseOfBlocked(b)
		if !ok || c.Validate() != nil || string(c) != string(b) {
			t.Errorf("blocked %q maps to %q ok=%v", b, c, ok)
		}
	}
	if len(fixedBlocked) != 7 {
		t.Errorf("%d fixed blocked reasons, want 7", len(fixedBlocked))
	}
	if _, ok := CauseOfBlocked(BlockedPlanner("x")); ok {
		t.Error("composed blocked reason mapped")
	}
}

// Every declared admission Reason constant converts to a Cause: a new Reason
// without one fails here.
func TestEveryAdmissionReasonIsACause(t *testing.T) {
	files, err := filepath.Glob("*.go")
	if err != nil {
		t.Fatal(err)
	}
	fset := token.NewFileSet()
	var reasons []string
	for _, name := range files {
		f, err := parser.ParseFile(fset, name, nil, 0)
		if err != nil {
			t.Fatal(err)
		}
		for _, d := range f.Decls {
			g, ok := d.(*ast.GenDecl)
			if !ok || g.Tok != token.CONST {
				continue
			}
			var typ string
			for _, s := range g.Specs {
				v := s.(*ast.ValueSpec)
				if id, ok := v.Type.(*ast.Ident); ok {
					typ = id.Name
				} else if v.Type != nil {
					typ = ""
				}
				if typ != "Reason" || len(v.Values) != 1 {
					continue
				}
				if lit, ok := v.Values[0].(*ast.BasicLit); ok {
					s, _ := strconv.Unquote(lit.Value)
					reasons = append(reasons, s)
				}
			}
		}
	}
	if len(reasons) != 18 {
		t.Fatalf("found %d admission Reason constants, want 18", len(reasons))
	}
	for _, r := range reasons {
		if err := CauseOfReason(Reason(r)).Validate(); err != nil {
			t.Errorf("admission Reason %q: %v", r, err)
		}
	}
}
