# Contributing to BackWave

Thank you for your interest in BackWave. The source is public so you can read, audit, and modify it -
but contributions work a little differently than a typical open-source project. Please read this before
opening a pull request.

## Contributions are invitation-only

BackWave is dual-licensed (a free source-available tier and a commercial Pro tier), and code moves
between the two. To keep that model workable, code contributions are **by invitation only**, and every
merged contribution requires a signed Contributor License Agreement.

**Please do not open unsolicited pull requests.** We are not able to review or merge them, and we don't
want your effort to go to waste. This isn't a comment on the quality of your work - it's a legal and
maintenance constraint of the licensing model.

## What we always welcome

- **Bug reports.** Open an [issue](https://github.com/Back-Wave/BackWave/issues) with a clear
  reproduction: what you did, what you expected, what happened, and the versions involved. A minimal
  runnable repro is the single most useful thing you can include.
- **Feature requests and design discussion.** Open an issue or a
  [Discussion](https://github.com/Back-Wave/BackWave/discussions). We'd rather hear the problem you're
  trying to solve than a fully-specified solution.
- **Documentation feedback.** If something is wrong, unclear, or missing, tell us.

Good bug reports and sharp problem statements move BackWave forward more than most code would.

## If you are invited to contribute

If we invite you to submit a change, the flow is:

1. **Sign the [CLA](CLA.md).** We'll tell you how; nothing can be merged without it.
2. **Match the surrounding code.** Follow the style, naming, and structure already in the file you're
   editing. BackWave ships as NuGet packages, so every public member of a shipped assembly must carry
   complete XML documentation - see the guidance in the repository docs.
3. **Keep changes surgical.** Touch only what the change requires. Don't reformat or refactor adjacent
   code.
4. **Include tests.** New behavior needs coverage; bug fixes should include a test that fails before the
   fix and passes after.
5. **Green build.** `dotnet build` must be warning-free and `dotnet test` must pass.

## Questions

Reach us at **team@backwave.app** or on
[Discussions](https://github.com/Back-Wave/BackWave/discussions).
