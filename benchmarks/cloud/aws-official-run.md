# Running the official benchmark battery on AWS (free-trial credit)

How to produce **publishable** BackWave-vs-Hangfire numbers — PostgreSQL *and* SQL Server — on a
short-lived AWS EC2 instance, paid from new-account trial credit. The whole thing is a disposable box you
stand up, run for an hour or two, and destroy.

- **Why a cloud box at all:** official mode is gated to native x86-64 (see
  [`docs/performance/README.md`](../../docs/performance/README.md)). A dev laptop (Apple Silicon / Rosetta)
  can only ever produce `local`/indicative numbers. This is the cheapest way to get a genuinely native
  x86-64 run — the one thing the laptop can't do, and exactly the gap on the SQL Server flagship.
- **Cost:** an 8-vCPU fixed-performance instance is ~$0.34–0.38/hr. The full battery runs well under an
  hour; even with setup and a couple of repeats you'll spend **a few dollars** of the ~$100–200 new-account
  credit. Out of pocket: ~$0.
- **Both engines, one box:** Postgres and SQL Server both run in Docker on this single VM, co-located on
  loopback (the methodology wants this — network latency is "additive-and-equal" and only adds noise). You
  do **not** use managed RDS / Azure SQL; you self-host both.

---

## Step 0 — Pre-flight (do these *before* launching, they're the usual blockers)

1. **Raise the EC2 vCPU quota.** New accounts cap On-Demand vCPUs (often at 5), which blocks an 8-vCPU
   launch. In the AWS console: **Service Quotas → Amazon EC2 → "Running On-Demand Standard (A, C, D, H, I,
   M, R, T, Z) instances" → Request increase → at least 16**. Usually auto-approved in minutes; do it first
   so you don't hit a wall mid-setup.
2. **Set a budget alert** so a forgotten instance can't drain the credit silently. **Billing → Budgets →
   Create budget → Cost budget → $10/month → alert at 80%** to your email. (Belt-and-suspenders; the real
   safety is destroying the box in Step 5.)

---

## Step 1 — Launch the instance

EC2 → Launch instance:

- **AMI:** *Ubuntu Server 24.04 LTS*, **64-bit (x86)** — double-check it's x86, not Arm. (Arm fails the
  official-mode gate *and* has no SQL Server image.)
- **Instance type:** **`c6i.2xlarge`** (8 vCPU / 16 GB, fixed-performance Intel). For RAM headroom with both
  engines resident through the whole battery, **`m6i.2xlarge`** (8 vCPU / **32 GB**) is ~$0.04/hr more and
  the safer pick. **Never a `t`-series** — those are burstable and CPU-credit throttling will poison a
  sustained-throughput run.
- **Key pair:** create/select one so you can SSH in.
- **Storage:** bump the root volume to **30 GB gp3** (the default 8 GB won't hold the SQL Server image +
  Postgres + the .NET SDK + NuGet + results).
- **Security group:** inbound **SSH (22) from My IP only**. Nothing else — the databases are loopback-only
  inside the box and never need an exposed port.

SSH in: `ssh -i your-key.pem ubuntu@<public-ip>`

---

## Step 2 — Get the repo onto the box

It's a private repo, so authenticate. Easiest is the GitHub CLI:

```sh
sudo apt-get update && sudo apt-get install -y gh git
gh auth login        # choose GitHub.com → HTTPS → device code, paste into your laptop browser
gh repo clone <owner>/BackWave
cd BackWave
```

(Alternatives: `git clone https://<token>@github.com/<owner>/BackWave.git`, or `rsync` from your laptop —
if you rsync, include `.git` so the run can be tied to a commit.)

---

## Step 3 — Provision and run

One script does the rest — installs Docker + .NET 10, brings up both databases, seeds the BackWave +
Hangfire databases, and runs the official battery:

```sh
./benchmarks/cloud/provision.sh
```

It refuses to run on anything but native x86-64 Linux, so you can't accidentally produce a non-publishable
"official" file. Default battery is `RUNS=5` across noop-drain, noop-sustained, and the 10 ms anchor for
both engines plus the scale-out curve — **budget 30–90 min**. For a quick smoke first:

```sh
RUNS=2 JOBS=20000 ANCHOR_JOBS=5000 ./benchmarks/cloud/provision.sh   # NOT for publication
```

SQL Server runs as **Developer edition** (the container default) — the same engine as Enterprise for
performance, free for this use. Note that in your write-up: published SQL Server numbers are Developer-edition.

---

## Step 4 — Retrieve and check the results

Results land in `benchmarks/results/*.json`, each self-labelled. Pull them back to your laptop:

```sh
# from your laptop
scp -i your-key.pem 'ubuntu@<public-ip>:BackWave/benchmarks/results/*.json' ./
```

Then **only** transcribe cells whose manifest reads `"Publishable": true` into `docs/performance/`. Record
the exact box in the writeup so the number is reproducible: **provider AWS, instance type (`c6i.2xlarge` /
`m6i.2xlarge`), region, AMI (Ubuntu 24.04 x86-64), 8 vCPU / 16–32 GB**, plus the DB image tags
(`postgres:17-alpine`, `mcr.microsoft.com/mssql/server:2022-latest`) — most of which each JSON's
environment manifest already stamps.

---

## Step 5 — Tear it down (don't skip this)

The credit only stays safe if the box is gone:

1. EC2 → Instances → select it → **Instance state → Terminate**.
2. Confirm the **root EBS volume** is deleted with it (delete-on-termination is the default for the root
   volume; verify under **Elastic Block Store → Volumes** that none linger — an orphaned volume keeps
   billing).
3. Optionally delete the budget and the key pair.

A *stopped* instance still bills for its EBS volume — **terminate**, don't just stop.

---

## Why the choices (FAQ)

- **Why not the always-free tier?** Those instances are 1 GB / burstable. SQL Server needs ≥2 GB just to
  boot and a sustained benchmark needs fixed (non-burstable) CPU — the always-free micros fail both. Trial
  credit on a proper dedicated instance is the only credible-and-free path.
- **Why co-locate the DBs instead of managed RDS?** The methodology deliberately co-locates app + DB on
  loopback so network round-trip is equal for both systems and only adds noise. Managed DB free tiers are
  also burstable/tiny. Self-hosting both engines in Docker is the documented method.
- **Why not Arm (Graviton / Oracle Ampere)?** It fails the native-x86-64 official-mode gate, and there's no
  SQL Server Arm image. x86-64 is non-negotiable here.
</content>
