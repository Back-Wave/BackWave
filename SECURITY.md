# Security Policy

We take the security of BackWave seriously and appreciate responsible disclosure.

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions, or pull
requests.**

Instead, report privately using either:

- **GitHub Security Advisories** - use the [Report a vulnerability](https://github.com/Back-Wave/BackWave/security/advisories/new)
  button on the repository's Security tab, or
- **Email** - **team@backwave.app**.

Please include as much of the following as you can, so we can reproduce and assess quickly:

- The affected package(s) and version(s).
- The type of issue (for example: injection, deserialization, authorization bypass, denial of service).
- Step-by-step instructions to reproduce, ideally with a minimal proof of concept.
- The impact - what an attacker could achieve.

## What to expect

- We aim to acknowledge your report within **3 business days**.
- We'll keep you informed as we investigate, and we'll let you know when a fix is released.
- With your permission, we're glad to credit you once the issue is resolved.

## Supported versions

Security fixes are made against the **latest released version**. If you're on an older version, the fix
is to upgrade.

## A note on license enforcement

BackWave's license check is intentionally offline and soft-fail: it makes no network calls and never
disables functionality. "Bypassing" the unlicensed notice is not a security vulnerability and does not
need to be reported - please don't submit reports about it.
