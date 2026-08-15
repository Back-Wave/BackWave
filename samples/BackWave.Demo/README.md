# BackWave.Demo

The public, live monitoring demo served at **https://demo.backwave.app**. It is a real BackWave
node cluster running a synthetic workload, with the Dashboard mounted at the root so a visitor can
watch jobs enqueue, execute, retry, fail, and dead-letter — and press the Operator Actions for real.

This is a deployed artifact, not a teaching sample. For a read-and-run playground that wires up every
storage adapter, see `../BackWave.Sample.Api` instead. Keeping the two separate is deliberate: they
have different dependency sets, lifecycles, and audiences.

## What it is

- A single ASP.NET host referencing `BackWave`, `BackWave.Hosting`, `BackWave.Sqlite`,
  `BackWave.Dashboard`, `BackWave.Pro`, and `BackWave.Pro.Dashboard`.
- Real worker nodes processing a synthetic workload driven by a BackWave recurring schedule. The
  handlers deliberately succeed, throw, run slow, and dead-letter so every Dashboard tab stays alive.
- The Dashboard mounted at `/` with all Operator Actions and `ViewSensitiveData` enabled.
- One Docker image, deployed to a container host. Netlify serves the marketing site and holds a
  `demo` DNS record pointing here; it never proxies or serves a byte of the demo.

## Decisions and why

**Its own container at a subdomain, not on Netlify.** The Dashboard is a stateful Kestrel app that
renders server-side and holds long-lived SSE connections for its live views. Static hosting can't run
it, and serverless can't hold the connections or the in-process state. So the demo is a Docker image
on a container host (Fly/Render/etc.) at `demo.backwave.app`, joined to the Netlify site by a DNS
record and a link. There is no client-runnable build of this Dashboard to host statically.

**Single shared instance to start.** Every visitor sees and mutates the same world, exactly like the
River demo. A visitor mid-read can see the view shift under them when someone else clicks or when the
hourly reset fires; the banner sets that expectation. Per-visitor isolation (each anonymous session
getting its own instance) is a real enhancement we can add later, but it costs one mini BackWave
instance per session — its own store, generator, and worker loop — so it is deferred until the shared
model proves insufficient.

**SQLite Embedded Adapter, co-resident.** The store is a file inside the container: no external
database, no second service, one image. It also dogfoods the flagship "zero operational overhead"
embedded story — the live demo is driven by the same adapter we tell people to deploy. The
single-host limit of the embedded adapter is free here, since a demo is single-host by definition.

**Seed-on-boot plus hourly container recycle.** Rather than write in-process wipe/reseed logic, the
container restarts on a schedule and re-seeds from scratch on boot. The ephemeral SQLite file makes
this the simplest correct reset there is. The recycle is also the primary cure for state griefing:
anything a visitor wrecks self-heals within the hour while the generator keeps refilling.

**Fully interactive, licensed, hardened.** All Operator Actions are on (Requeue, Cancel, PauseQueue,
TriggerSchedule) and `ViewSensitiveData` is on, because the data is synthetic and pressing the buttons
for real is the whole point. The demo carries a Pro license so the Workflows tab presents clean rather
than showing the soft unlicensed banner. Two guardrails protect the process (not the data, which the
reset already covers): a light per-IP rate limit on POST actions against a scripted-load DoS, and a
sane `LiveRefreshInterval` to bound SSE re-render cost. The subdomain is `noindex` + robots-disallow so
churning job rows are never crawled, and a persistent banner reads roughly: "Live demo · synthetic
data · shared by all visitors · resets hourly."

## Deferred

- Per-visitor isolated instances (no login), so one visitor's clicks don't affect another's view.
- In-process reset instead of a full container recycle, if the hourly blip proves disruptive.

## Run it

Locally, from this folder:

```
dotnet run
```

then open `http://localhost:5284/`. Give the workers a few seconds to churn and every tab fills in:
Overview, a full "Executing now" pool, Failures / Dead-Lettered, Recurring Schedules, Observers, and a
clean Workflows tab. The `generate-workload` recurring schedule tops the workload up every minute, so
"Executing now" never empties. Without a Pro licence key the Workflows tab still works; it just carries
the "running without a licence key" notice. Set `BackWave__ProLicense=<key>` to clear it.

As the container, from the **repo root** (the build context needs the referenced `src/*` projects):

```
docker build -f samples/BackWave.Demo/Dockerfile -t backwave-demo .
docker run --rm -p 8080:8080 -e BackWave__ProLicense=<key> backwave-demo
```

then open `http://localhost:8080/`.

Deploy is Fly.io (`fly.toml`): pushes to `main` that touch the demo or the library redeploy via
`.github/workflows/demo-deploy.yml`, and `.github/workflows/demo-recycle.yml` restarts the app hourly
for the reset. The Pro licence is a Fly secret (`BackWave__ProLicense`), never committed.
