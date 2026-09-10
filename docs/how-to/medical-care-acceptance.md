# Verify medical care in Docker

[Documentation](../README.md) · [Medical contracts](../reference/medical-care.md)

Prepare private [Linux inputs](docker-inputs.md) and build the companion with
`-p:MedicalManagementFixture=true` into a disposable mod snapshot. Never install
the fixture DLL into an ordinary game. The setup creates disease patients, a
missing-leg patient, beds, medicine and capable doctors; it does not complete
treatment, surgery or recovery.

Build the task source using the Dockerfile's `worker` target. Run a fresh
`rimbot.container_worker` with the private inputs and this command:

```text
python /app/scripts/medical_management_acceptance.py --root /worker/run --seconds 1800
```

The default worker root is `/worker/run`; substitute the actual `--root` if using
container-local staging. Keep the worker's full output and logs. Inspect
`medical-management-result.json`, `medical-management.sqlite`, `inputs.json`,
`staging.json` and `HeadlessPlayer.log`. The result retains health samples, the discovered
surgical catalog, plan receipts and each assertion. Require `passed: true` and
successful owned-process cleanup. Preserve failed trials in fresh output directories.

The probe uses the semantic player surgery command and deterministic tending through
Hands, including enabling initially disabled bed rest. The care case checks disease
disappearance, observed bed use and preservation of an undirected chronic missing limb. The shortage case checks actual native
prosthetic installation. It exercises no language model and does not establish support for
all disease definitions, all recipes or arbitrary long-term survival.

Run `--case shortage` and `--case failure` in separate fresh workers. The shortage
case removes medicine access and drafts available doctors after admission, verifies
the original bill remains pending, then restores access and observes the outcome.
The failure fixture uses a native recipe failure outcome with guaranteed probability;
pawns still perform the operation and native workers apply injuries. It asserts a
blocked health outcome and no automatic reissue. These test-only definition changes
are excluded from production builds. Each report includes a source/input manifest.

Use `--case repeat` to disable routine Doctor work in the fixture and require at
least two completed Hands-issued native treatments for each disease patient.
Treatment receipts alone do not satisfy this assertion.
