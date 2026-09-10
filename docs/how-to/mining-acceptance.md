# Verify native surface mining

[Documentation](../README.md) · [Contracts](../reference/mining-contracts.md)

Prepare [private Linux inputs](docker-inputs.md) and compile the observation companion
with `-p:MiningFixture=true`. Copy those DLLs into a private test mod snapshot before
launch. Never install this fixture in a normal game or replace running game DLLs.

Run a fresh [Docker worker](docker-worker.md) with this command:

```text
python scripts/mining_acceptance.py --source-root /worker/run --output /worker/probe --seconds 480
```

The fixture clears natural roof/rock and fog only in a disposable surface test area,
places native ore at full hit points and enables ordinary mining work. It does not
grant mining skill, change extraction speed, finish pawn work or spawn recovered stock.
The scenario checks roof refusal, cancellation, a roof added after designation, shared
Hands dispatch, native recovered output and depletion refusal. Inspect `result.json`
and `progress.json`, native logs and the worker's input hashes. Every assertion must
pass; setup errors and timeouts are failures, and failed output directories are retained.

This scripted scenario does not exercise local-model interpretation, deep drilling or
general tunnel support. The [backlog](../BACKLOG.md) records remaining acceptance.
