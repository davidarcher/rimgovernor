# Unified manager discovery

`discover` searches both advertised player API endpoints and native game
names, labels and descriptions. Definitions are indexed once when a game session
connects, using SQLite FTS5 with word stemming and weighted ranking. Game changes
invalidate both the definition snapshot and its index. Repeated searches make no
HTTP or model calls. Only up to eight definition matches enter model context.

Results include the native definition name, group, description, selected native
facts and an exact follow-up read query. Building matches point to construction
definition inspection; other matches point to their definition group and exact
name. An action hint is scoped to the manager's existing permissions. A match
does not prove a building is constructible or an item is currently on the map;
native inspection and current-state queries still establish that.

The index covers the installed RIMAPI definition export (35 groups, 7,624 records
in the current game), including modded definitions. It is lexical full-text
search, not semantic embeddings or an invented game synonym/rule table. Natural
language and unknown synonyms can still produce irrelevant or missing matches.

The separately authored `controller/contracts/discovery.openapi.json` generates
`discovery_models.py` through `scripts/generate_http_contracts.py`. Manager
responses are constructed using those models. This is a controller search
facility; it does not add a native RIMAPI search endpoint.

Run the read-only benchmark with `python scripts/live_discovery.py`.

Live validation: index acquisition/build took 0.72 seconds; four warm searches
took 0.17–1.20 ms. “free instant sleeping spot” ranked SleepingSpot first and
returned zero native work/material counts plus construction_definitions as its
next query. Potato and rifle searches found native plant/weapon definitions.
Evidence: `.rimbot/discovery-live.json`. No game mutations or model calls were
needed. Automated coverage includes caching, session invalidation, ranking,
query hints and role-scoped action hints.

The complete database exposed a previously untested wire value: native mental
state recovery intervals can serialize as the string Infinity. The authored
HTTP schema now documents that value, rather than coercing it into a fabricated
number or discarding the definition snapshot.
