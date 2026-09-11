package contractgen

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
)

func TestEmittedDecodersCompileAndEnforcePlacementBoundary(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/schemas/placement-previews-request.v1.schema.json")
	if err != nil {
		t.Fatal(err)
	}
	schema, err := ParseSchema(data)
	if err != nil {
		t.Fatal(err)
	}
	output, err := GenerateGo(schema, GoOptions{Package: "placementpreview", SchemaPath: "contracts/schemas/placement-previews-request.v1.schema.json"})
	if err != nil {
		t.Fatal(err)
	}
	directory := t.TempDir()
	writeGenerated(t, directory, "request.gen.go", output)
	// A second root in the same package must not collide with private helpers.
	other, err := ParseSchema([]byte(`{"title":"Status","type":"boolean"}`))
	if err != nil {
		t.Fatal(err)
	}
	second, err := GenerateGo(other, GoOptions{Package: "placementpreview", SchemaPath: "contracts/status.json"})
	if err != nil {
		t.Fatal(err)
	}
	writeGenerated(t, directory, "status.gen.go", second)
	writeGenerated(t, directory, "request_test.go", []byte(emittedTests))
	goName := "go"
	if runtime.GOOS == "windows" {
		goName += ".exe"
	}
	command := exec.Command(filepath.Join(runtime.GOROOT(), "bin", goName), "test", "-count=1", ".")
	command.Dir = directory
	command.Env = append(os.Environ(), "GO111MODULE=off", "GOWORK=off", "GOTOOLCHAIN=local")
	result, err := command.CombinedOutput()
	if err != nil {
		t.Fatalf("emitted decoder tests: %v\n%s", err, result)
	}
}

func writeGenerated(t *testing.T, directory, name string, data []byte) {
	t.Helper()
	if err := os.WriteFile(filepath.Join(directory, name), data, 0600); err != nil {
		t.Fatal(err)
	}
}

const emittedTests = `package placementpreview
import("strings";"testing")
const candidate = "{\"defName\":\"Wall\",\"x\":0,\"z\":0,\"rotation\":\"north\",\"stuff\":\"\"}"
func TestAcceptBoundaries(t *testing.T){
 for _,row:=range []string{candidate,strings.Replace(candidate,"\"x\":0","\"x\":-2147483648",1),strings.Replace(candidate,"\"z\":0","\"z\":2147483647",1),strings.Replace(candidate,"Wall",strings.Repeat("🐾",100),1),strings.Replace(candidate,"Wall","\u200b",1),strings.Replace(candidate,"Wall","\\ud83d\\udc3e",1)} {
  if value,err:=DecodePlacementBatch([]byte("["+row+"]"));err!=nil||len(value)!=1{t.Fatalf("valid row refused: %s %v",row,err)}
 }
 batch:="["+strings.Repeat(candidate+",",15)+candidate+"]"
 if value,err:=DecodePlacementBatch([]byte(batch));err!=nil||len(value)!=16{t.Fatal(err)}
 if value,err:=DecodePlacementPreviewArguments([]byte("{\"placements\":\"\"}"));err!=nil||value.Placements!=""{t.Fatal(err)}
 if value,err:=DecodeStatus([]byte("false"));err!=nil||bool(value){t.Fatal(err)}
}
func TestRefuseMalformedAndChangedShape(t *testing.T){
 rows:=[]string{strings.Replace(candidate,"\"defName\":\"Wall\",","",1),strings.Replace(candidate,"Wall",strings.Repeat("🐾",101),1),strings.Replace(candidate,"Wall","\u0085\u2000",1),strings.Replace(candidate,"Wall","\\ud800",1),strings.Replace(candidate,"Wall","\\udc00",1),strings.Replace(candidate,"\"x\":0","\"x\":0,\"\\u0078\":1",1),strings.Replace(candidate,"\"x\":0","\"X\":0",1),strings.Replace(candidate,"\"x\":0","\"x\":0,\"extra\":0",1)}
 for _,number:=range []string{"2147483648","-2147483649","1.0","1e0","null","true","\"0\"","01"}{ rows=append(rows,strings.Replace(candidate,"\"x\":0","\"x\":"+number,1)) }
 for _,row:=range rows{ if _,err:=DecodePlacementBatch([]byte("["+row+"]"));err==nil{t.Fatalf("invalid accepted: %s",row)} }
 for _,data:=range []string{"[]","null","[null]","["+strings.Repeat(candidate+",",16)+candidate+"]","["+candidate+"]{}","["+candidate+",]",strings.Repeat(" ",1<<20)+"[]"}{if _,err:=DecodePlacementBatch([]byte(data));err==nil{t.Fatalf("invalid batch accepted, length %d",len(data))}}
 for _,data:=range []string{"{}","{\"placements\":null}","{\"placements\":1}","{\"placements\":\"a\",\"placements\":\"b\"}","{\"placements\":\""+strings.Repeat("a",32769)+"\"}"}{if _,err:=DecodePlacementPreviewArguments([]byte(data));err==nil{t.Fatal("invalid outer accepted")}}
 if _,err:=DecodePlacementCandidate(append([]byte(candidate),0xff));err==nil{t.Fatal("invalid UTF8 accepted")}
}
`
