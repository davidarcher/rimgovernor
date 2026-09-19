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

// commentOnly reports whether a Go file's edit since base changed nothing
// a case can observe: the token streams of the two versions match once
// ordinary comments are dropped, or they match once the debug trace is
// dropped as well (diagnosticOnly, #361). Compiler directives (//go:build,
// //go:embed, //go:noinline, //line, //export, // +build) and a cgo
// preamble are comments the toolchain reads, so a file touching them never
// counts as comment-only, nor does a file that fails to scan, is new, or is
// deleted.
func commentOnly(repo, base, file string) bool {
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
	return diagnosticOnly([]byte(before), afterBytes)
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

// diagnosticGates are the identifiers of the clock's debug trace
// (internal/buildingruntime/clock_scheduler.go): an `if` whose condition
// names one, and a statement calling one, run nothing a case observes.
var diagnosticGates = map[string]bool{"clockDebug": true, "clockSchedulerDebug": true, "clockSchedulerLog": true}

// diagnosticOnly reports whether two versions of a Go file agree once the
// statements under the debug trace are removed from both: the trace-gated
// blocks and trace calls stripped (stripDiagnostics), the rest printed
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

// stripDiagnostics removes every statement the debug trace gates from the
// file's statement lists and else branches.
func stripDiagnostics(file *ast.File) {
	ast.Inspect(file, func(n ast.Node) bool {
		switch n := n.(type) {
		case *ast.BlockStmt:
			n.List = withoutDiagnostics(n.List)
		case *ast.CaseClause:
			n.Body = withoutDiagnostics(n.Body)
		case *ast.CommClause:
			n.Body = withoutDiagnostics(n.Body)
		case *ast.IfStmt:
			if n.Else != nil && diagnostic(n.Else) {
				n.Else = nil
			}
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

// diagnostic reports whether a statement is a trace call or an `if` on a
// trace gate with no other branch whose body only traces (traceOnly): a
// gated block that assigns outside itself or returns runs under `serve
// --debug`, which every acceptance launch passes, so it stays significant.
func diagnostic(stmt ast.Stmt) bool {
	switch stmt := stmt.(type) {
	case *ast.ExprStmt:
		call, ok := stmt.X.(*ast.CallExpr)
		return ok && gate(call.Fun)
	case *ast.IfStmt:
		if stmt.Else != nil || stmt.Init != nil {
			return false
		}
		gated := false
		ast.Inspect(stmt.Cond, func(n ast.Node) bool {
			if gate(n) {
				gated = true
			}
			return !gated
		})
		return gated && traceOnly(stmt.Body.List, map[string]bool{})
	}
	return false
}

// traceOnly reports whether statements only trace: trace calls, locals
// declared for them (:=, var) and assigned within the block, and loops or
// branches over the same. locals collects the block's own names.
func traceOnly(list []ast.Stmt, locals map[string]bool) bool {
	for _, stmt := range list {
		switch stmt := stmt.(type) {
		case *ast.ExprStmt:
			call, ok := stmt.X.(*ast.CallExpr)
			if !ok || !gate(call.Fun) {
				return false
			}
		case *ast.AssignStmt:
			for _, lhs := range stmt.Lhs {
				if stmt.Tok == token.DEFINE {
					if ident, ok := lhs.(*ast.Ident); ok {
						locals[ident.Name] = true
					}
				} else if !local(lhs, locals) {
					return false
				}
			}
		case *ast.IncDecStmt:
			if !local(stmt.X, locals) {
				return false
			}
		case *ast.DeclStmt:
			if decl, ok := stmt.Decl.(*ast.GenDecl); ok {
				for _, spec := range decl.Specs {
					if spec, ok := spec.(*ast.ValueSpec); ok {
						for _, name := range spec.Names {
							locals[name.Name] = true
						}
					}
				}
			}
		case *ast.IfStmt:
			if stmt.Init != nil && !traceOnly([]ast.Stmt{stmt.Init}, locals) || !traceOnly(stmt.Body.List, locals) || stmt.Else != nil && !traceOnly([]ast.Stmt{stmt.Else}, locals) {
				return false
			}
		case *ast.BlockStmt:
			if !traceOnly(stmt.List, locals) {
				return false
			}
		case *ast.RangeStmt:
			if stmt.Tok == token.DEFINE {
				for _, expr := range []ast.Expr{stmt.Key, stmt.Value} {
					if ident, ok := expr.(*ast.Ident); ok {
						locals[ident.Name] = true
					}
				}
			} else if stmt.Key != nil && !local(stmt.Key, locals) || stmt.Value != nil && !local(stmt.Value, locals) {
				return false
			}
			if !traceOnly(stmt.Body.List, locals) {
				return false
			}
		case *ast.ForStmt:
			if stmt.Init != nil && !traceOnly([]ast.Stmt{stmt.Init}, locals) || stmt.Post != nil && !traceOnly([]ast.Stmt{stmt.Post}, locals) || !traceOnly(stmt.Body.List, locals) {
				return false
			}
		default:
			return false
		}
	}
	return true
}

// local reports whether an assignment target is one of the block's own
// names (or an element or field of one).
func local(expr ast.Expr, locals map[string]bool) bool {
	switch expr := expr.(type) {
	case *ast.Ident:
		return expr.Name == "_" || locals[expr.Name]
	case *ast.IndexExpr:
		return local(expr.X, locals)
	case *ast.SelectorExpr:
		return local(expr.X, locals)
	case *ast.ParenExpr:
		return local(expr.X, locals)
	}
	return false
}

func gate(n ast.Node) bool {
	ident, ok := n.(*ast.Ident)
	return ok && diagnosticGates[ident.Name]
}
