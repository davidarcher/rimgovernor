package contractgen

import (
	"bytes"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestPythonReplyVariants(t *testing.T) {
	schema, err := ParseSchema([]byte(`{
"title":"Reply","oneOf":[{"$ref":"#/$defs/Failed"},{"$ref":"#/$defs/Accepted"}],
"$defs":{
"Accepted":{"type":"object","additionalProperties":false,"required":["success","count","mode"],"properties":{
"success":{"type":"boolean","const":true},
"count":{"type":["integer","null"],"minimum":0,"maximum":2147483647,"x-integerToken":true},
"optional":{"type":["null","integer"],"minimum":-1,"maximum":1,"x-integerToken":true},
"mode":{"type":"string","enum":["native","preview"],"x-maxUTF16Length":10}}},
"Failed":{"type":"object","additionalProperties":false,"required":["success","error"],"properties":{
"success":{"type":"boolean","const":false},"error":{"type":"string","x-maxUTF16Length":30}}},
"Envelope":{"type":"object","additionalProperties":false,"required":["reply"],"properties":{"reply":{"$ref":"#/$defs/NestedReply"}}},
"NestedReply":{"oneOf":[{"$ref":"#/$defs/Accepted"},{"$ref":"#/$defs/Failed"}]}
}}`))
	if err != nil {
		t.Fatal(err)
	}
	options := PythonOptions{SchemaPath: "test/reply.json"}
	generated, err := GeneratePython(schema, options)
	if err != nil {
		t.Fatal(err)
	}
	again, err := GeneratePython(schema, options)
	if err != nil || !bytes.Equal(generated, again) {
		t.Fatal("nondeterministic reply output", err)
	}
	for _, annotation := range []string{"Reply = Failed | Accepted", "success: Literal[True]", "count: int | None", "optional: int | None | Missing = MISSING", `mode: Literal["native", "preview"]`} {
		if !bytes.Contains(generated, []byte(annotation)) {
			t.Fatalf("missing concrete type %s", annotation)
		}
	}
	directory := t.TempDir()
	if err = os.WriteFile(filepath.Join(directory, "generated.py"), generated, 0600); err != nil {
		t.Fatal(err)
	}
	code := `import json
import generated as g

success = g.decode_reply('{"success":true,"count":null,"mode":"native"}')
assert isinstance(success,g.Accepted) and success.count is None
assert isinstance(success.optional,g.Missing)
assert g.to_wire(success) == {'success':True,'count':None,'mode':'native'}
explicit_null = g.decode_reply('{"success":true,"count":0,"optional":null,"mode":"preview"}')
assert explicit_null.optional is None and 'optional' in g.to_wire(explicit_null)
failure = g.decode_reply('{"success":false,"error":"placement refused"}')
assert isinstance(failure,g.Failed) and failure.success is False
for valid in [success, explicit_null, failure,
              g.decode_reply('{"success":true,"count":2147483647,"optional":-1,"mode":"native"}')]:
    wire = g.to_wire(valid)
    assert 'Accepted' not in wire and 'Failed' not in wire
    assert g.decode_reply(json.dumps(wire)) == valid
    envelope = g.decode_envelope(json.dumps({'reply':wire}))
    assert envelope.reply == valid and g.to_wire(envelope) == {'reply':wire}

invalid = [
 '{}','null','[]','true',
 '{"success":null}','{"success":1}','{"success":0}','{"success":"true"}',
 '{"success":true,"mode":"native"}',
 '{"success":true,"count":0,"mode":null}',
 '{"success":true,"count":0,"mode":"Native"}',
 '{"success":true,"count":0,"mode":1}',
 '{"success":true,"count":true,"mode":"native"}',
 '{"success":true,"count":1.0,"mode":"native"}',
 '{"success":true,"count":1e0,"mode":"native"}',
 '{"success":true,"count":-1,"mode":"native"}',
 '{"success":true,"count":2147483648,"mode":"native"}',
 '{"success":true,"count":0,"optional":false,"mode":"native"}',
 '{"success":true,"count":0,"optional":2,"mode":"native"}',
 '{"success":true,"count":0,"mode":"native","error":"mixed branch"}',
 '{"success":false,"count":null,"mode":"native"}',
 '{"success":false,"error":null}',
 '{"success":false,"error":"ok","unknown":0}',
 '{"success":false,"success":true,"error":"ok"}',
 '{"success":false,"error":"\\ud800"}',
 '{"success":false,"error":"ok"}null',
 '{"success":false,"error":"ok",}',
]
for raw in invalid:
    try:
        g.decode_reply(raw)
    except ValueError:
        pass
    else:
        raise AssertionError('accepted invalid reply: '+raw)
for decoder, raw in [
    (g.decode_accepted,'{"success":false,"count":0,"mode":"native"}'),
    (g.decode_failed,'{"success":true,"error":"ok"}')]:
    try:
        decoder(raw)
    except ValueError:
        pass
    else:
        raise AssertionError('branch decoder ignored success constant')
`
	command := exec.Command(contractPython(t), "-c", code)
	command.Dir = directory
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("generated Python reply decode: %v\n%s", err, output)
	}
}
