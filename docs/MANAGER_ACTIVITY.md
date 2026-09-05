# Manager activity checkpoint

The dashboard now exposes a colony-scoped recent activity feed with filters for each specialist, planning and the administrator. It includes successful tool results, model call duration, proposals and explicit arbitration decisions. Raw arguments/results are expandable. Drafts and approvals are distinguished from executed work. The feed retains 300 recent events; the full export includes older history. Refresh the browser after updating the controller.

Specialists now receive their native proposal tools directly and inspect shared observations independently, without inheriting earlier specialists' unverified claims. Role instructions distinguish missing local permission from a globally missing capability. Pawn needs absent in the game are null instead of misleading zero values; body size is populated.

Validation: 41 backend tests and 6 dashboard tests passed; frontend production build succeeded. Live activity endpoint returned 224 events after controller restart.

Known failure: broad startup test `.rimbot/startup-tests/20260905-172045/report.json` did not establish sleeping arrangements within 240 seconds. Infrastructure reached the native blueprint draft tool, omitted material, and hit the model output limit after validation feedback. This checkpoint does not establish reliable autonomous startup. Large tool schemas, unnecessary administrative inspections and recovery remain unresolved. Earlier bounded bed construction tests are not evidence that this broader scenario passes.
