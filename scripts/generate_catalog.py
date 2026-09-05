"""Extract HTTP contracts from a pinned RIMAPI checkout, not invented game actions.

Usage: python scripts/generate_catalog.py PATH_TO_RIMAPI
Review the generated diff when updating the upstream revision. This deliberately
supports the concrete C# DTO/controller style used by the pinned revision; it is
not a general C# parser. Runtime route discovery verifies endpoint availability.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve()
source = root / "Source/RIMAPI/RimworldRestApi"
classes = {}

def snake(s):
    return re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", re.sub(r"(.)([A-Z][a-z]+)", r"\1_\2", s)).lower()

def block(text, start):
    depth = 1
    end = start + 1
    while end < len(text) and depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start + 1:end - 1]

for file in (source / "Models").rglob("*.cs"):
    text = file.read_text(encoding="utf-8-sig")
    for m in re.finditer(r"class\s+(\w+)(?:\s*:\s*(\w+))?\s*\{", text):
        classes[m[1]] = (block(text, m.end() - 1), m[2])

def schema(typ, chain=()):
    nullable = typ.endswith("?")
    typ = typ.rstrip("?")
    primitives = {"string": "string", "int": "integer", "long": "integer", "float": "number", "double": "number", "bool": "boolean"}
    if typ in primitives:
        result = {"type": primitives[typ]}
    elif typ.startswith("List<") or typ.endswith("[]"):
        result = {"type": "array", "items": schema(typ[5:-1] if typ.startswith("List<") else typ[:-2], chain)}
    elif typ.startswith("Dictionary<"):
        result = {"type": "object"}
    elif typ in classes and typ not in chain:
        body, parent = classes[typ]
        result = schema(parent, chain + (typ,)) if parent else {"type": "object", "properties": {}, "additionalProperties": False}
        props = result.setdefault("properties", {})
        for p in re.finditer(r'public\s+([\w<>?, \[\]]+?)\s+(\w+)\s*\{\s*get;\s*set;\s*\}(?:\s*=\s*([^;]+);)?', body):
            key = snake(p[2])
            props[key] = schema(p[1].replace(" ", ""), chain + (typ,))
        # DTO reference types are often optional in C#. Required keys are
        # conservative, source-reviewed additions in policy.json.
    else:
        result = {"description": "RIMAPI type: " + str(typ)}
    if nullable:
        result = {"anyOf": [result, {"type": "null"}]}
    return result

routes = []
for file in (source / "Controllers").rglob("*.cs"):
    text = file.read_text(encoding="utf-8-sig")
    matches = list(re.finditer(r'\[(Get|Post|Put|Delete|Patch)\("([^"]+)"\)\]', text))
    for i, m in enumerate(matches):
        segment = text[m.end():matches[i+1].start() if i+1 < len(matches) else len(text)]
        method = re.search(r'public\s+(?:async\s+)?[\w<>]+\s+(\w+)\s*\(', segment)
        body = re.search(r'ReadBodyAsync<([^>]+)>', segment)
        query, required = {}, []
        if "RequestParser.GetMapId(context)" in segment:
            query["map_id"] = {"type": "integer"}
            required.append("map_id")
        for q in re.finditer(r'RequestParser.Get(\w+)Parameter\(context,\s*"([^"]+)"([^)]*)\)', segment):
            qt = {"Int":"integer", "Float":"number", "Boolean":"boolean"}.get(q[1], "string")
            query[q[2]] = {"type": qt}
            if "false" not in q[3] and q[1] != "Boolean":
                required.append(q[2])
        # Controllers that accept a JSON DTO can also have a query fallback.
        if body and 'context.Request.HasEntityBody' in segment:
            query, required = {}, []
        request = schema(body[1]) if body else {"type":"object", "properties":query, "required":sorted(set(required)), "additionalProperties":False}
        if body:
            request.setdefault('properties', {}).update(query)
            request['required'] = sorted(set(required))
        name = m[1].lower() + "_" + re.sub(r'[^a-zA-Z0-9]+', '_', m[2].removeprefix('/api/v1/')).strip('_')
        routes.append({"name":name, "method":m[1].upper(), "path":m[2], "category":file.stem.replace("Controller", ""), "description":re.sub(r'(?<!^)(?=[A-Z])', ' ', method[1]) if method else name, "transport":"json" if body else "query", "query_keys":list(query), "schema":request})

revision = subprocess.check_output(["git", "-c", "safe.directory=*", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
out = Path(__file__).resolve().parents[1] / "controller/rimbot/data/catalog.json"
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps({"source":"https://github.com/IlyaChichkov/RIMAPI", "revision":revision, "endpoints":sorted(routes, key=lambda x:x["name"])}, indent=2) + "\n", encoding="utf-8")
print(f"Extracted {len(routes)} endpoint contracts at {revision}")
