package affected

import (
	"go/scanner"
	"go/token"
	"os"
	"path/filepath"
	"strings"
)

// commentOnly reports whether a Go file's edit since base changed nothing
// but comments: the token streams of the two versions match once ordinary
// comments are dropped. Compiler directives (//go:build, //go:embed,
// //go:noinline, //line, //export, // +build) and a cgo preamble are
// comments the toolchain reads, so a file touching them never counts as
// comment-only, nor does a file that fails to scan, is new, or is deleted.
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
