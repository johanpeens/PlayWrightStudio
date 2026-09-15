# Contributing

Thanks for taking a look. This is a small project, so there's not much process.

## Getting set up

```bash
dotnet run --project src/PlaywrightStudio
```

The app is at <http://localhost:5050>. First launch seeds a demo scenario pointed at a practice form
the app serves itself (`wwwroot/demo-form.html`) — that form is the fastest way to exercise a change
end to end without depending on a real site.

Your scenarios and settings live in `%LOCALAPPDATA%\PlaywrightStudio`, not in the repo, so a rebuild
never wipes them.

## The shape of the code

```
src/PlaywrightStudio
├── Assets/recorder.js     injected into the page being recorded - the heart of the thing
├── Assets/runbar.js       the progress bar drawn on the page during a run
├── Components/Pages       Scenarios, Editor, Runs, Manuals, Settings, Help
├── Components/Dialogs     step editor, import/export, folder picker
├── Models                 Scenario, TestStep, RunResult, Manual, settings
└── Services               RecorderService, RunnerService, PlaywrightHost, stores, exporters
```

Two files carry most of the weight:

- **`Assets/recorder.js`** decides what a step *is* and which selector describes it. Almost every
  "it recorded the wrong thing" bug is in `selectorFor`, `cssCandidates` or `semanticCandidates`.
- **`Services/RunnerService.cs`** decides what happens when a selector doesn't match cleanly —
  `ChooseSelectorAsync` and `DiagnoseAsync` are where replay robustness lives.

Both are commented where the behaviour is non-obvious. If you change one, please say in the PR what
real page made you do it.

## Testing a change

There is no unit test suite — the interesting behaviour is a browser driving another browser, which
tests poorly in isolation. What's expected instead:

1. Record a journey against the demo form (or a real page) and check the steps read sensibly.
2. Replay it and check every step passes.
3. If you touched the runner, deliberately break a selector and check the failure message still
   tells you *why*.

Say in the PR what you actually ran against. "Recorded and replayed the demo form, plus a signup
form on X" is worth more than a green tick.

## Style

- Match what's already there: file-scoped namespaces, `var` where the type is plain, four spaces.
- Comments explain **why**, not what. If a line looks wrong but is deliberate, that's exactly the
  line that needs one.
- UI text is lower-key than typical: sentence case, no exclamation marks, and say what actually
  happened rather than "Success!".

## Reporting a bug

The useful ones say which **page** it happened on. A selector that works everywhere except one
framework's date picker is the normal shape of a bug here, and without the page it can't be
reproduced. If the site is private, the relevant HTML snippet is enough.
