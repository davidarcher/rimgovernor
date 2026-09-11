package contractgen

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const csharpReplySchema = `{"title":"Reply","oneOf":[{"$ref":"#/$defs/Good"},{"$ref":"#/$defs/Bad"}],"$defs":{"Good":{"type":"object","additionalProperties":false,"required":["success","count","kind"],"properties":{"success":{"type":"boolean","const":true},"count":{"type":["integer","null"],"minimum":0,"maximum":99,"x-integerToken":true},"kind":{"type":"string","enum":["ready","blocked"],"x-maxUTF16Length":20}}},"Bad":{"type":"object","additionalProperties":false,"required":["success","error"],"properties":{"success":{"type":"boolean","const":false},"error":{"type":"string","x-maxUTF16Length":20}}}}}`

func TestCSharpReplyGeneration(t *testing.T) {
	schema, err := ParseSchema([]byte(csharpReplySchema))
	if err != nil {
		t.Fatal(err)
	}
	data, err := GenerateCSharp(schema, CSharpOptions{Namespace: "Example", SchemaPath: "reply.json"})
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"public int? Count", "NullValueHandling.Include", "public Good? Success", "public Bad? Failure", "public static Reply From(Good value)", "serializer.Serialize(writer, typed.WireValue)", "Incorrect boolean constant", "Unknown string enum value"} {
		if !strings.Contains(string(data), want) {
			t.Errorf("missing %s", want)
		}
	}
	if dir := os.Getenv("RIMGOVERNOR_CSHARP_REPLY_OUTPUT"); dir != "" {
		if err := os.MkdirAll(dir, 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(dir, "Reply.cs"), data, 0644); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(dir, "Program.cs"), []byte(csharpReplyHarness), 0644); err != nil {
			t.Fatal(err)
		}
		if source := os.Getenv("RIMGOVERNOR_CSHARP_REPLY_SCHEMA"); source != "" {
			raw, err := os.ReadFile(source)
			if err != nil {
				t.Fatal(err)
			}
			actual, err := ParseSchema(raw)
			if err != nil {
				t.Fatal(err)
			}
			generated, err := GenerateCSharp(actual, CSharpOptions{Namespace: "Example.Native", SchemaPath: "reply-v2.json"})
			if err != nil {
				t.Fatal(err)
			}
			if err := os.WriteFile(filepath.Join(dir, "NativeReply.cs"), generated, 0644); err != nil {
				t.Fatal(err)
			}
		}
	}
}

const csharpReplyHarness = `using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Example;
class Program {
 static int checks;
 static void Reject(Action action) { try { action(); } catch (FormatException) { checks++; return; } throw new Exception("Expected rejection"); }
 static void Main() {
  var good = Reply.From(Good.From(null, "ready", true));
  var json = JsonConvert.SerializeObject(good);
  var token = JObject.Parse(json);
  if (token["count"]!.Type != JTokenType.Null || token["Success"] != null || token["Value"] != null || good.Success == null || good.Failure != null) throw new Exception("Invalid union/null shape"); checks++;
  if (Reply.Decode(json).Success!.Count != null) throw new Exception("Null lost"); checks++;
  if (Reply.From(Bad.From("failed", false)).Failure!.Error != "failed") throw new Exception("Failure lost"); checks++;
  Reject(()=>Good.From(100,"ready",true));
  Reject(()=>Good.From(1,"other",true));
  Reject(()=>Good.From(1,"ready",false));
  Reject(()=>Reply.Decode("{\"success\":true,\"kind\":\"ready\"}"));
  Reject(()=>Reply.Decode("{\"success\":true,\"kind\":\"ready\",\"count\":1.0}"));
  Reject(()=>Reply.Decode("{\"success\":true,\"kind\":\"ready\",\"count\":null,\"error\":\"bad\"}"));
  Reject(()=>Reply.Decode("{\"success\":false,\"error\":null}"));
  Reject(()=>Reply.Decode("{\"success\":true,\"success\":false,\"error\":\"bad\"}"));
  Console.WriteLine(checks + " C# reply checks passed");
 }
}`

func TestCSharpReplyMemberCollision(t *testing.T) {
	for _, name := range []string{"From", "Success", "Failure", "WireValue"} {
		schema, err := ParseSchema([]byte(strings.Replace(csharpReplySchema, `"title":"Reply"`, `"title":"`+name+`"`, 1)))
		if err != nil {
			t.Fatal(err)
		}
		if _, err := GenerateCSharp(schema, CSharpOptions{Namespace: "Example", SchemaPath: "reply.json"}); err == nil {
			t.Fatalf("accepted %s", name)
		}
	}
	schema, err := ParseSchema([]byte(strings.Replace(csharpReplySchema, `"success","count","kind"`, `"success","kind"`, 1)))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := GenerateCSharp(schema, CSharpOptions{Namespace: "Example", SchemaPath: "reply.json"}); err == nil {
		t.Fatal("optional nullable lost presence")
	}
}
