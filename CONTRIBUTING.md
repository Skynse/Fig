# Contributing to Fig

Thanks for helping. Fig is a deliberately small editor ("simple, like CapCut"), so the most useful contributions are the ones that keep it that way: focused, understood, and built a step at a time.

## The one rule: understand what you're building

You should be able to explain your change: what it does, how it works, and where it sits in the code. If you can't, it isn't ready, however well it seems to run.

For a major feature, that means building it **incrementally**: a series of small pull requests, each one a step you understand completely, instead of one large drop.

### Building a major feature step by step

1. **Agree the plan first.** Open an issue that says what the feature is, why it belongs in Fig, and the steps you'll build it in. A few lines per step is enough.
2. **Start with the core.** The first steps go in `Fig.Core`: the model, then the commands that change it (undoable, through `TimelineEditor`), each with its tests. No UI yet.
3. **Then the UI.** Once the core works, wire up the smallest interface that uses it, then improve it in later steps.
4. **Every step stands on its own.** Each pull request builds, passes `dotnet test` and leaves the app working. A half-built feature stays out of sight until it is usable. Nothing should depend on a later PR to stop being broken.
5. **Keep each step small enough to review in one sitting.** If one is getting big, split it.

The existing code and the diagrams already in `uml/` are good maps of how things fit together. You don't need to add diagrams of your own, but draw one if it helps you or a reviewer follow the change.

### What counts as a "major feature"

Build these incrementally:

- A new subsystem, module, or pipeline stage (for example an effect graph, a new export path, a new kind of selection).
- A new editing command family, or a change to how the timeline model or undo history works.
- Anything that adds a new concept to the project file format (like an audio mixer).
- Anything that touches more than one of `Fig.Core`'s modules.

These can go in a single pull request:

- Bug fixes and small tweaks.
- New effects and transitions that follow the existing pattern (see [DEVELOPMENT.md](DEVELOPMENT.md)).
- Refactors that do not change behaviour, and docs-only changes.

If you are unsure, open an issue and ask before writing code.

## Getting set up

Requirements: .NET SDK 10.0 or later, and a desktop platform supported by Avalonia (Windows, macOS, Linux). To run with sound you also need an audio output device.

```bash
dotnet build
dotnet run --project src/Fig.App
dotnet test
```

## Code layout and conventions

- `src/Fig.Core` is the engine and has **no UI dependency**. Behaviour belongs here and is unit tested.
- `src/Fig.App` is the Avalonia application: views, view models, services. Keep logic out of views; they call into Core.
- Tests live in `tests/Fig.Core.Tests`. Behaviour is tested through Core, not through the UI.
- One namespace per module (for example `Fig.Core.Media`). Folders are for organisation only and do not change the namespace.
- Edits to the timeline go through commands on `TimelineEditor` so that they are undoable. Do not mutate the document directly from the UI.
- Match the style of the surrounding code: naming, comment density, and idiom.
- Adding an effect or transition has its own short guide in [DEVELOPMENT.md](DEVELOPMENT.md).
- Use `<summary></summary>` where possible to describe codeblocks.
### Tests

- Core behaviour gets unit tests before the UI is written.
- Fix a bug by adding a test that fails without the fix.
- Don't write headless UI tests (simulated clicks, key presses or drags). If something needs testing, move its logic into `Fig.Core` and test it there.
- Run `dotnet test` before opening a pull request, and say in the description if anything fails and why.

### Requirements document

`software_requirements.md` tracks features with IDs (`FR-nnn`) and a status: `X` complete, `O` partial, `-` not started. If your change adds or completes a feature, add or update its row in the same pull request.

## Pull requests

1. Open an issue first for anything major, so the direction is agreed before work starts.
2. Keep each pull request to one feature or fix. Unrelated cleanups go in their own.
3. Use a short, plain commit message in the imperative ("add beat detection").
4. In the description, explain **what** you changed, **how** it works, and **why** it belongs in Fig.
5. For a step of a major feature, link its issue and say which step this is and what the next one will be.

### Checklist

- [ ] I can explain what this changes and how it works
- [ ] For a major feature: this is one step of the plan in its issue, and the app still works with it alone
- [ ] Core behaviour has tests; `dotnet test` passes (or failures are explained)
- [ ] `software_requirements.md` updated if a feature was added or completed
- [ ] Docs updated if behaviour users see has changed
- [ ] AI use disclosed, if any (below)

## AI-assisted contributions

AI-assisted code if you:

1. **Hand-wrote the code or used AI in bits and pieces (no massive commits anymore)**

You can use AI to write tutorials for you if you want to implement something. It's a good way to learn. But as of now, commits have to be handwritten.

AI can be used for profiling, but there's really no need for that when profiling tools already exist. If you find it easier to explore flamecharts with AI, then that's fine.

The main rule is the same as everywhere else: you have to understand what you've written. Building major features in small steps is how that stays possible, with or without AI.

## Reporting bugs

Open an issue with the steps to reproduce, what you expected, and what happened. Include your OS, the .NET version, and a screenshot or log if it helps. For media problems, say what the file's container and codec are.

## License

By contributing you agree that your work is released under the project's [LICENSE](LICENSE).
