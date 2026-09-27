package nativeaccept

import (
	"go/ast"
	"go/parser"
	"go/token"
	"io/fs"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/reflect/protoregistry"

	_ "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	_ "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
)

// TestWireRequestLiteralsNameProtoFields checks every map literal a case
// hands to Harness.Wire against the tool's request message: native decodes
// requests strictly, so a field the proto dropped (the removed page field)
// is an INVALID_REQUEST at run time. Only literal keys are checked; values
// built at run time are not.
func TestWireRequestLiteralsNameProtoFields(t *testing.T) {
	requests := wireRequestTypes()
	fset := token.NewFileSet()
	checked := 0
	err := filepath.WalkDir(".", func(path string, d fs.DirEntry, err error) error {
		if err != nil || d.IsDir() || !strings.HasSuffix(path, ".go") || strings.HasSuffix(path, "_test.go") {
			return err
		}
		file, err := parser.ParseFile(fset, path, nil, 0)
		if err != nil {
			return err
		}
		ast.Inspect(file, func(n ast.Node) bool {
			call, ok := n.(*ast.CallExpr)
			if !ok || len(call.Args) != 4 {
				return true
			}
			sel, ok := call.Fun.(*ast.SelectorExpr)
			if !ok || (sel.Sel.Name != "Wire" && sel.Sel.Name != "WireBytes") {
				return true
			}
			lit, ok := call.Args[2].(*ast.BasicLit)
			if !ok || lit.Kind != token.STRING {
				return true
			}
			tool, _ := strconv.Unquote(lit.Value)
			desc, ok := requests[tool]
			if !ok {
				return true
			}
			for _, m := range requestLiterals(call.Args[3]) {
				checked++
				checkLiteralFields(t, fset, tool, desc, m)
			}
			return true
		})
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	if checked == 0 {
		t.Fatal("no Wire request literal was checked")
	}
}

// wireRequestTypes maps each native tool name (<package>_<snake method>,
// the package being the middle of rimgovernor.<package>.v1) to its request.
func wireRequestTypes() map[string]protoreflect.MessageDescriptor {
	out := map[string]protoreflect.MessageDescriptor{}
	protoregistry.GlobalFiles.RangeFiles(func(fd protoreflect.FileDescriptor) bool {
		parts := strings.Split(string(fd.Package()), ".")
		if len(parts) != 3 || parts[0] != "rimgovernor" {
			return true
		}
		services := fd.Services()
		for i := 0; i < services.Len(); i++ {
			methods := services.Get(i).Methods()
			for j := 0; j < methods.Len(); j++ {
				m := methods.Get(j)
				out[parts[1]+"_"+snake(string(m.Name()))] = m.Input()
			}
		}
		return true
	})
	return out
}

func snake(name string) string {
	var b strings.Builder
	for i, r := range name {
		if r >= 'A' && r <= 'Z' {
			if i > 0 {
				b.WriteByte('_')
			}
			r += 'a' - 'A'
		}
		b.WriteRune(r)
	}
	return b.String()
}

// requestLiterals returns the map literals an argument is built from: a
// literal, the literals merged by Merge, or the literal an identifier was
// assigned.
func requestLiterals(expr ast.Expr) []*ast.CompositeLit {
	switch e := expr.(type) {
	case *ast.CompositeLit:
		if isStringMap(e) {
			return []*ast.CompositeLit{e}
		}
	case *ast.CallExpr:
		name := ""
		switch fn := e.Fun.(type) {
		case *ast.Ident:
			name = fn.Name
		case *ast.SelectorExpr:
			name = fn.Sel.Name
		}
		if name != "Merge" {
			return nil
		}
		var out []*ast.CompositeLit
		for _, arg := range e.Args {
			out = append(out, requestLiterals(arg)...)
		}
		return out
	case *ast.Ident:
		if e.Obj == nil {
			return nil
		}
		assign, ok := e.Obj.Decl.(*ast.AssignStmt)
		if !ok || len(assign.Lhs) != len(assign.Rhs) {
			return nil
		}
		for i, lhs := range assign.Lhs {
			if id, ok := lhs.(*ast.Ident); ok && id.Name == e.Name {
				if lit, ok := assign.Rhs[i].(*ast.CompositeLit); ok && isStringMap(lit) {
					return []*ast.CompositeLit{lit}
				}
			}
		}
	}
	return nil
}

func isStringMap(lit *ast.CompositeLit) bool {
	m, ok := lit.Type.(*ast.MapType)
	if !ok {
		return false
	}
	key, ok := m.Key.(*ast.Ident)
	return ok && key.Name == "string"
}

func checkLiteralFields(t *testing.T, fset *token.FileSet, tool string, desc protoreflect.MessageDescriptor, lit *ast.CompositeLit) {
	t.Helper()
	if strings.HasPrefix(string(desc.FullName()), "google.protobuf.") {
		return
	}
	for _, elt := range lit.Elts {
		kv, ok := elt.(*ast.KeyValueExpr)
		if !ok {
			continue
		}
		keyLit, ok := kv.Key.(*ast.BasicLit)
		if !ok || keyLit.Kind != token.STRING {
			continue
		}
		key, _ := strconv.Unquote(keyLit.Value)
		fields := desc.Fields()
		field := fields.ByJSONName(key)
		if field == nil {
			field = fields.ByName(protoreflect.Name(key))
		}
		if field == nil {
			t.Errorf("%s: %s request has no field %q (%s)", fset.Position(keyLit.Pos()), tool, key, desc.FullName())
			continue
		}
		if field.Message() == nil || field.IsMap() || field.IsList() {
			continue
		}
		if nested, ok := kv.Value.(*ast.CompositeLit); ok && isStringMap(nested) {
			checkLiteralFields(t, fset, tool, field.Message(), nested)
		}
	}
}
