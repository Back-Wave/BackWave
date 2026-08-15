# BackWave VOPR — running & reporting

VOPR is BackWave's continuous Deterministic Simulation Testing (DST) discovery engine: it runs the
`Simulator` over endless seeded worlds, and on any tripped oracle persists a **replayable Plan** rather than
halting (ADR 0018, PRD `.scratch/issues/0004`). This is the operator's guide — how to launch a run, read its
output, and turn a finding into a reproducible issue. For *why* each piece works the way it does, see
`docs/issues/008x–013x` and `docs/adr/0018`.

The console lives in `tests/BackWave.Vopr`. Run it from the repo root.

---

## Two engines

| Engine | Flag | What it does | When to use |
|---|---|---|---|
| **Uniform-random** (`VoprRunner`) | *(default)* | `ProcessorCount` workers draw independent random seeds, each a fresh `SwarmConfig.FromSeed` world. Broad, shallow, cheap. | Default. Best at finding shallow bugs fast across a wide world space. |
| **Coverage-guided** (`CoverageGuidedSwarm`) | `--guided` | Keeps an in-memory corpus of coverage-advancing Plans, mutates them (config-space, then trace-space), and climbs an edge/Situation + interaction-tuple gradient. Deeper, narrower. | When you want to drill interleavings the random engine rarely reaches. |

They are **exclusive** — one engine per process. To run both, run two processes (e.g. sequentially, as the
overnight script does). Both share the same on-disk bug sink, so they cross-dedup.

---

## Flags

```
dotnet run -c Release --project tests/BackWave.Vopr -- [flags]
```

| Flag | Meaning |
|---|---|
| *(none)* | Uniform-random, **forever** (Ctrl-C to stop). |
| `--guided` | Use the coverage-guided swarm instead of uniform-random. |
| `--duration <t>` | Wall-clock bound, then **self-terminate** and print the summary. `90s`, `30m`, `8h`, `1d`, or a bare number = seconds. This is the unattended-run bound — it drains gracefully and flushes the summary (a `kill`/SIGTERM does not). |
| `--max-runs <n>` | Stop after `n` runs/iterations. Composes with `--duration` (first bound wins). |
| `--replay <plan.json>` | Replay a persisted Plan once and report whether it still trips. See **Triage** below. |
| `<seed>` | Legacy single-seed shell — run one seeded simulation for quick inspection. |

> **Build once, run the binary.** For long runs use `-c Release` and run the compiled DLL directly so a phase
> boundary never triggers a rebuild:
> ```
> dotnet build -c Release tests/BackWave.Vopr
> dotnet tests/BackWave.Vopr/bin/Release/net10.0/BackWave.Vopr.dll --guided --duration 8h
> ```

### The bug sink: `VOPR_CORPUS_DIR`

Every tripped oracle writes **one JSON Plan per `InvariantId`** (first failure wins; repeats are tallied, not
re-written) to `$VOPR_CORPUS_DIR`, or a temp dir if unset. The file is the full replayable Plan (Scenario +
realized FaultMap + FailureStamp). Set it to a stable path for any run whose findings you want to keep:

```
export VOPR_CORPUS_DIR=/path/to/corpus
```

---

## Reading the output

### Uniform-random
```
VOPR forever-runner — 10 workers, corpus /…, stopping after 03:00:00 (Ctrl-C to stop early)
  240 runs, 0 unique failures, 16 runs/sec        ← live tally, every ~5s
entropy-base 0xAE6A9A2E6106E07E  (replay with VoprRunner.Run(entropyBase: …))
done: 240 runs in 15.4s (16 runs/sec), 0 unique failures across 0 failing runs
  DrainLiveness: 4 hit(s) → /…/DrainLiveness.json  ← per distinct InvariantId, with its sink path
```

### Coverage-guided (`--guided`)
```
VOPR coverage-guided swarm — 10 workers, bug sink /…
  178 iters, 18 iters/sec, 0 unique failures, corpus 13, tuples 25   ← productivity pulse
search-rng-base 0xB7262B268F8D720B  (replay with CoverageGuidedSwarm.Run(entropyBase: …, workerCount: 1))
done: 269 iterations (179 trace-mutations) in 15.0s …, corpus 14, 26 interaction-tuples, 0 unique failures
Coverage report
  edges:      11/11 (100%)
  situations: 11/14 (79%)
  never-hit situations:
    ScheduleMinted …                              ← the complement = where the search hasn't reached
```

What to watch:
- **`unique failures`** — distinct `InvariantId`s tripped = the count of finding JSONs you'll triage.
- **guided `corpus` / `tuples` pulse** — if these flatline, the search has gone cold (stop / re-seed).
- **`entropy-base` / `search-rng-base`** — replays the *whole single-threaded run*; it is **not** a per-bug
  repro (use the Plan JSON for that — see Triage).
- **coverage complement** — never-reached edges/Situations are the frontier for the next guided run.

---

## Reproducing & triaging a finding

A finding is a Plan JSON. Confirm it deterministically with `--replay`:

```
dotnet tests/BackWave.Vopr/bin/Release/net10.0/BackWave.Vopr.dll --replay /…/DrainLiveness.json
```

| Verdict | Exit | Meaning |
|---|---|---|
| `REPRO` | 0 | Re-tripped the same `InvariantId` — a real, reproducible bug to file. |
| `CLEAN` | 1 | Converged, no oracle tripped — likely an artifact or already-fixed. |
| `MISMATCH` | 2 | Tripped a *different* `InvariantId` than the Plan was stamped with — needs a look. |
| `LOAD-ERROR` | 3 | The Plan could not be loaded. |

> **Liveness oracles can false-positive.** `DrainLiveness` / `ExecuteLiveness` etc. can trip on over-intensity
> interleavings rather than a real product bug (cf. the issue 0130 resolution). `REPRO` means *reproducible*,
> not *necessarily a Core defect* — the next step (minimize, read the Plan) is what separates the two.

Per `REPRO` finding:
1. **Minimize** — `SeedMinimizer.Minimize(plan, invariantId)` shrinks the fault map (ddmin) to a 1-minimal repro.
2. **File** — `/to-issues` + `/triage`, attaching the (minimized) Plan JSON as the quick repro.
3. **Graduate** — commit the minimized Plan into `tests/BackWave.Tests/Simulation/fixtures/` and add a
   sabotage twin, so the bug stays fixed (see `RegressionFixturesTests`).

---

## Unattended overnight runs

The local, git-excluded `.vopr-overnight/` harness wraps all of the above for a hands-off run (see its script
headers for details):

- **`run-overnight.sh`** — regular 3h → guided 7h, sequential, shared fresh corpus, per-phase `tee` logs, and a
  SIGKILL watchdog at `--duration + grace` as a hard backstop. Launch sleep-proof + detached:
  ```
  nohup caffeinate -i bash .vopr-overnight/run-overnight.sh > .vopr-overnight/logs/driver.log 2>&1 &
  ```
- **`morning-triage.sh`** — runs `--replay` over the whole corpus and buckets findings into REPRO / CLEAN /
  MISMATCH with paste-ready repro commands:
  ```
  bash .vopr-overnight/morning-triage.sh
  ```

`.vopr-overnight/` is kept out of git via `.git/info/exclude` (local-only, so the tracked tree stays clean).
