# Administrator investigation and action

The administrator is no longer limited to voting on department proposals. Strategy,
daily planning and semantic review can inspect the colony, retrieve knowledge,
keep notes and issue supported orders. Advisors can still propose objectives;
specialists remain available for detailed execution. A failed advisor review does
not prevent the administrator from making its own decision.

## Tools

| Tool | Behavior |
| --- | --- |
| `query`, native read tools | Read game state using the existing RIMAPI contracts. |
| `discover`, `describe` | Find capabilities and inspect their exact arguments. |
| `inspect_colony` | Page through projects, work receipts and spatial reservations; read saved strategy and player direction. Tracking is distinguished from live game evidence. |
| `wiki_search` | Search RimWorld Wiki titles/content for relevant mechanics and guides. Available to advisors and specialists too. |
| `wiki_read` | Read an article or section in bounded excerpts, with revision URL, attribution and continuation offset. |
| `memory_read`, `memory_write` | Search, save, revise or delete colony-specific administrator notes. |
| `create_goal` | Create/revise an approved semantic objective without a manager proposal. Existing identity, dependencies and player cancellations remain enforced. |
| `execute_order` | Directly execute a described native command within an active project, without another inference call. |
| `reserve_goal_site` | Use the existing shared site resolver for a current project increment. |
| `build_enclosure` | Use the existing enclosure compiler to issue a complete perimeter, including selected entrances. |
| `submit` | Return a plan or decide on advisor proposals. Correct assumptions in `updates`; defer unsupported proposals and retire/hold obsolete projects. |

Direct execution uses the existing domain, material, labor, dependency and spatial
checks, runtime write lock, session validation and post-write observation. Results
remain attached to the project. Pending/unknown orders are not reported as verified.
This adds an entry point to existing execution, not another game implementation.
Direct spatial work needs an existing shared base plan/reservation; the administrator
does not bypass the architect's reservations. Game mutations require Automate.

## Memory and knowledge

Notes live in the existing colony-scoped SQLite state and are exposed in controller
state and activity events. They survive controller restarts; a different colony
gets its own notebook. The prompt contains a small key index, not the whole notebook.
The administrator retrieves relevant entries explicitly. There are at most 64 notes
of 1,600 characters each; it can consolidate or delete them. Notes retain an observed
game tick and supporting evidence, but are fallible recollections rather than facts.

Wiki queries use the public [MediaWiki search API](https://www.mediawiki.org/wiki/API:Search)
and article parsing at `https://rimworldwiki.com/api.php`. Requests go only to this
fixed public endpoint. Search text and article titles are sent; colony snapshots,
player messages and notes are not automatically uploaded. Article responses include
a stable revision link. Redirects are resolved by MediaWiki. Results are cached for
15 minutes, with 32 cached requests per planner; excerpts default to 3,000 characters
and support sections/paging. Network failures return explicit tool errors.

Wiki guidance and notes never supersede player instructions, installed native
definitions or current colony observations. No wiki corpus is preloaded into every
model request. No MCP transport or additional model/provider is introduced.

## Validation

Automated tests exercise persistent/scoped notes, goal deduplication, player
cancellations, domain restrictions, direct verified execution without a specialist
turn, pending receipts, advisor failure recovery, and the administrator tool loop.
Wiki tests cover caching, attribution, pagination, navigation removal and errors.
A live wiki smoke test successfully searched `bedroom`, followed the `Bedroom`
redirect to `Rooms`, and retrieved a revision-linked excerpt and section index.
These checks do not establish that a live model can yet complete the starter base.

The RimMolt/AutoRim tool-surface and fork comparison remains a separate pending audit.
MCP adoption is out of scope per player direction.
