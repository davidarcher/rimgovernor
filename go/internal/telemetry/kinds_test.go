package telemetry

import (
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"path/filepath"
	"strings"
	"testing"
)

// The flight recorder is the only log, so a record with no kind goes
// nowhere. Every slog call in the module's non-test sources must name a
// kind (KindKey or "kind") among its arguments.
func TestEverySlogCallNamesAKind(t *testing.T) {
	root := filepath.Join("..", "..")
	levels := map[string]bool{"Info": true, "Warn": true, "Error": true, "Debug": true, "InfoContext": true, "WarnContext": true, "ErrorContext": true, "DebugContext": true, "Log": true}
	var bad []string
	err := filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			if name := d.Name(); name == "generated" || name == ".rimgovernor" || name == "testdata" {
				return filepath.SkipDir
			}
			return nil
		}
		if !strings.HasSuffix(path, ".go") || strings.HasSuffix(path, "_test.go") {
			return nil
		}
		fset := token.NewFileSet()
		file, err := parser.ParseFile(fset, path, nil, 0)
		if err != nil {
			return err
		}
		ast.Inspect(file, func(n ast.Node) bool {
			call, ok := n.(*ast.CallExpr)
			if !ok {
				return true
			}
			sel, ok := call.Fun.(*ast.SelectorExpr)
			if !ok || !levels[sel.Sel.Name] || !loggerReceiver(sel.X) || namesKind(call.Args) {
				return true
			}
			bad = append(bad, fset.Position(call.Pos()).String())
			return true
		})
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	for _, position := range bad {
		t.Errorf("%s: slog call without a kind attribute writes no row", position)
	}
}

// loggerReceiver is slog itself, slog.Default(), or a variable named logger.
func loggerReceiver(x ast.Expr) bool {
	switch x := x.(type) {
	case *ast.Ident:
		return x.Name == "slog" || x.Name == "logger"
	case *ast.CallExpr:
		sel, ok := x.Fun.(*ast.SelectorExpr)
		if !ok || sel.Sel.Name != "Default" {
			return false
		}
		pkg, ok := sel.X.(*ast.Ident)
		return ok && pkg.Name == "slog"
	}
	return false
}

// namesKind reports whether KindKey or "kind" appears anywhere in the
// arguments (the clock wrappers build the attribute list with append).
func namesKind(args []ast.Expr) bool {
	found := false
	for _, arg := range args {
		ast.Inspect(arg, func(n ast.Node) bool {
			switch n := n.(type) {
			case *ast.BasicLit:
				found = found || n.Value == `"kind"`
			case *ast.SelectorExpr:
				found = found || n.Sel.Name == "KindKey"
			case *ast.Ident:
				found = found || n.Name == "KindKey"
			}
			return !found
		})
	}
	return found
}
