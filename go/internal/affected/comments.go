package affected

import (
	"bytes"
	"go/ast"
	"go/parser"
	"go/printer"
	"go/scanner"
	"go/token"
	"os"
	"path/filepath"
	"strings"
)

// acceptanceOnly reports whether a Go file's edit since base changed nothing
// a case can observe: the token streams of the two versions match once
// ordinary comments are dropped, or they match once the debug trace is
// dropped as well (literal-only clock logs in buildingruntime). This is
// acceptance classification only, never permission to skip Go checks.
// Compiler directives (//go:build,
// //go:embed, //go:noinline, //line, //export, // +build) and a cgo
// preamble are comments the toolchain reads, so a file touching them never
// counts as comment-only, nor does a file that fails to scan, is new, or is
// deleted.
func acceptanceOnly(repo, base, file string) bool {
	if !strings.HasSuffix(file, ".go") {
		return false
	}
	before, err := output(repo, "git", "show", base+":"+file)
	if err != nil {
		return false
	}
	afterBytes, err := os.ReadFile(filepath.Join(repo, filepath.FromSlash(file)))
	if err != nil {
		return false
	}
	after := string(afterBytes)
	if strings.Contains(before, `import "C"`) || strings.Contains(after, `import "C"`) {
		return false
	}
	a, ok := significantTokens([]byte(before))
	if !ok {
		return false
	}
	b, ok := significantTokens([]byte(after))
	if !ok {
		return false
	}
	if sameTokens(a, b) {
		return true
	}
	return strings.HasPrefix(file, "go/internal/buildingruntime/") && diagnosticOnly([]byte(before), afterBytes)
}

func sameTokens(a, b []lexeme) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

type lexeme struct {
	tok token.Token
	lit string
}

// significantTokens scans src into the tokens the compiler acts on:
// everything but comments, plus the comments that are directives.
func significantTokens(src []byte) ([]lexeme, bool) {
	fset := token.NewFileSet()
	var s scanner.Scanner
	failed := false
	s.Init(fset.AddFile("", fset.Base(), len(src)), src, func(token.Position, string) { failed = true }, scanner.ScanComments)
	var out []lexeme
	for {
		_, tok, lit := s.Scan()
		if tok == token.EOF {
			break
		}
		if tok == token.COMMENT && !directive(lit) {
			continue
		}
		out = append(out, lexeme{tok, lit})
	}
	return out, !failed
}

func directive(comment string) bool {
	for _, prefix := range []string{"//go:", "//line ", "//export ", "// +build", "//extern "} {
		if strings.HasPrefix(comment, prefix) {
			return true
		}
	}
	return false
}

// diagnosticOnly reports whether two versions of a Go file agree once the
// literal-only trace calls are removed from both (stripDiagnostics), then printed
// without comments and compared token by token. The directive comments
// must agree as well.
func diagnosticOnly(before, after []byte) bool {
	a, ok := diagnosticFreeTokens(before)
	if !ok {
		return false
	}
	b, ok := diagnosticFreeTokens(after)
	if !ok {
		return false
	}
	return sameTokens(a, b)
}

// diagnosticFreeTokens is the file's significant token stream with the
// debug trace stripped, its directive comments first.
func diagnosticFreeTokens(src []byte) ([]lexeme, bool) {
	fset := token.NewFileSet()
	file, err := parser.ParseFile(fset, "", src, parser.ParseComments)
	if err != nil {
		return nil, false
	}
	var out []lexeme
	for _, group := range file.Comments {
		for _, comment := range group.List {
			if directive(comment.Text) {
				out = append(out, lexeme{token.COMMENT, comment.Text})
			}
		}
	}
	stripDiagnostics(file)
	file.Comments = nil
	var buf bytes.Buffer
	if err := (&printer.Config{Mode: printer.RawFormat}).Fprint(&buf, fset, file); err != nil {
		return nil, false
	}
	tokens, ok := significantTokens(buf.Bytes())
	if !ok {
		return nil, false
	}
	return append(out, tokens...), true
}

// stripDiagnostics removes literal-only log statements from statement lists.
func stripDiagnostics(file *ast.File) {
	ast.Inspect(file, func(n ast.Node) bool {
		switch n := n.(type) {
		case *ast.BlockStmt:
			n.List = withoutDiagnostics(n.List)
		case *ast.CaseClause:
			n.Body = withoutDiagnostics(n.Body)
		case *ast.CommClause:
			n.Body = withoutDiagnostics(n.Body)
		}
		return true
	})
}

func withoutDiagnostics(list []ast.Stmt) []ast.Stmt {
	kept := list[:0]
	for _, stmt := range list {
		if !diagnostic(stmt) {
			kept = append(kept, stmt)
		}
	}
	return kept
}

// diagnostic admits only direct logger calls with literal arguments. Unknown
// calls, receives, alias writes, initializers and control flow stay significant.
func diagnostic(stmt ast.Stmt) bool {
	expr, ok := stmt.(*ast.ExprStmt)
	if !ok {
		return false
	}
	call, ok := expr.X.(*ast.CallExpr)
	if !ok || call.Ellipsis.IsValid() {
		return false
	}
	name, ok := call.Fun.(*ast.Ident)
	if !ok || name.Name != "clockSchedulerLog" || name.Obj != nil && name.Obj.Kind != ast.Fun {
		return false
	}
	for _, arg := range call.Args {
		if _, ok := arg.(*ast.BasicLit); !ok {
			return false
		}
	}
	return true
}
