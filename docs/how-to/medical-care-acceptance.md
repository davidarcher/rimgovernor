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
python /app/scripts/medical_management_acceptance.py --root /worker/run --seconds 900
```

The default worker root is `/worker/run`; substitute the actual `--root` if using
container-local staging. Keep the worker's full output and logs. Inspect
`medical-management-result.json`, `medical-management.sqlite`, `inputs.json`,
`staging.json` and `Player.log`. The result retains health samples, the discovered
surgical catalog, plan receipts and each assertion. Require `passed: true` and
successful owned-process cleanup. Preserve failed trials in fresh output directories.

The probe uses the semantic player surgery command and deterministic tending through
Hands. It checks actual native prosthetic installation, disease disappearance and
observed bed use. It exercises no language model and does not establish support for
all disease definitions, all recipes or arbitrary long-term survival.
