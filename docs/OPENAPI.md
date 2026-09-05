# RIMAPI HTTP contract

`integrations/RIMAPI/Contracts/rimapi.openapi.json` is the authored OpenAPI 3.1
contract for the RimWorld 1.6 built-in HTTP surface: **197 operations**, including construction,
pawns, work, storage, research, world data, camera control, documentation, and SSE.
It references the separately authored `construction.openapi.json` sub-contract.
There are **428 component schemas**, including distinct legacy input and output
shapes. Edit these source documents; do not regenerate them from C# DTOs.

The bundled document is available from the controller at
`http://127.0.0.1:8787/api/rimapi/openapi.json`, even with RimWorld closed.
The built RIMAPI mod serves the same document at
`http://127.0.0.1:8765/api/openapi.json`. Its references are self-contained.

## Generation and drift checks

```powershell
.\.venv\Scripts\python.exe scripts/generate_http_contracts.py
.\.venv\Scripts\python.exe scripts/generate_http_contracts.py --check
powershell -ExecutionPolicy Bypass -File .\build-rimapi.ps1
```

Generation produces Python wire models, typed async client methods, operation
metadata, and the bundled document embedded by RIMAPI. It does not need a running
game. Standard OpenAPI validation and generated-file checks run in the test suite.

`build-rimapi.ps1` also runs `scripts/ApiAudit`, using Roslyn and the actual native
build references. It compares registered route coverage and reviewed handler/DTO
fingerprints against the contract. A source change fails the check until its
contract is reviewed. The audit is an implementation check, not a schema generator.
It is restricted to the built-in API; extension-mod endpoints need their own
contracts. The script builds the DLL without installing it.

## Typed client

```python
import httpx
from rimbot.http_contract import HttpContractClient
from rimbot.http_models import get_v1_map_rooms_Query

async with httpx.AsyncClient(base_url="http://127.0.0.1:8765") as http:
    client = HttpContractClient(http)
    result = await client.get_v1_map_rooms(query=get_v1_map_rooms_Query(map_id=0))
    for room in result.data.rooms:
        print(room.id, room.open_roof_count)
```

The transport validates outgoing JSON/query arguments and incoming JSON before
returning generated models. Native errors remain errors. It never retries writes.
GET endpoints that read JSON bodies retain that transport; query fallbacks and
content types are explicit. Streaming endpoints are excluded from buffered JSON
methods. Supplying a contract does not authorize a manager to execute an endpoint.

## Enforcement and remaining migration

Construction v2 already uses generated C# and Python models with native validation.
The remaining native services still use their existing C# DTO implementations;
this iteration documents and audits them and supplies generated Python clients.
The existing manager catalog and legacy orchestration have not all been switched
to this client. That migration can now proceed against one reviewed HTTP contract,
instead of adding more controller-side guesses about response shapes.

Legacy request deserialization ignores unknown fields and supplies native defaults
for omitted fields. The input schemas describe that behavior; they do not claim
new server validation. Response schemas use the real snake_case resolver, numeric
enums, nullable values, omitted null properties, dictionaries, and response envelopes.
Service-level legality and action completion still require native game checks.

Known differences are explicit:

- Growing-zone GET currently reads `map_id` as both the map and zone ID. This is a
  documented implementation defect, not a working independent zone lookup.
- Two attributed documentation methods have incompatible registration signatures;
  they are listed under `x-unregistered-routes`, not advertised as working paths.
- Native collection filters use dotted query keys and preserve DTO shapes. They
  are distinct from the Python controller's query/projection language.
- SSE uses native event payload naming, not the HTTP snake_case resolver; hook and
  extension payloads are extensible. OpenAPI documents the stream transport, not
  an exhaustive event-payload catalog.
- Camera control is HTTP, screenshot responses carry image data, and live frames
  use UDP. There is no native HTTP video-stream endpoint in this build.

## Validation

```powershell
.\.venv\Scripts\python.exe scripts/live_http_contracts.py
```

The read-only test checks 18 representative endpoints against a loaded colony,
including pawns, rooms, definitions, storage, research, and camera status. Reports
are written under `.rimbot/http-contract-tests/`. It makes no model calls or game
mutations. Passing it does not establish all 197 operations' gameplay correctness.

Latest result: 18/18 passed in
`.rimbot/http-contract-tests/20260905-230648/report.json`. The 57-test controller
suite passed, as did OpenAPI validation, generated-file checks, Roslyn coverage,
and native compilation. Compilation retains the existing System.Runtime reference
warning; NuGet vulnerability metadata was unavailable during restore. This
iteration did not replace the running game's DLL.
