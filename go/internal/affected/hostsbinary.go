package affected

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"strings"
)

// serveIdents are the identifiers a case area's sources use to host the
// rimgovernor binary: a Serve spec or Service mark on a case,
// na.ServiceLaunch, na.LaunchService or the session's Rimgovernor binary.
var serveIdents = map[string]bool{"ServeSpec": true, "ServiceLaunch": true, "LaunchService": true, "Rimgovernor": true, "Serve": true, "Service": true}

// hostsBinary reports whether a case area's sources host `rimgovernor
// serve`. A bridge-only area never runs the binary, so no change to it
// reaches the area (#361). Build tags are not consulted.
func hostsBinary(dir string) (bool, error) {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return false, err
	}
	fset := token.NewFileSet()
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || !strings.HasSuffix(name, ".go") {
			continue
		}
		file, err := parser.ParseFile(fset, filepath.Join(dir, name), nil, 0)
		if err != nil {
			return false, err
		}
		found := false
		ast.Inspect(file, func(n ast.Node) bool {
			if id, ok := n.(*ast.Ident); ok && serveIdents[id.Name] {
				found = true
			}
			return !found
		})
		if found {
			return true, nil
		}
	}
	return false, nil
}
