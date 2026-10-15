# ClassScribe

A .NET 10 console app for personal study. During an online class it records whatever the laptop
plays (WASAPI loopback of the default output device). When stopped, it sends the recording to
ElevenLabs Scribe v2 with a glossary of Arabic terms and saves `transcript.txt` and
`transcript.json`. `README.md` is the user guide. The full background, design decisions and
history are in `docs/HANDOFF.md` (kept out of git); read it before larger changes.

## Ground rules

- Never add anything that downloads from the course website or gets around its protection.
  ClassScribe only records audio the user is already playing. Keep it that way.
- The ElevenLabs API key lives only in the `ELEVENLABS_API_KEY` environment variable (user
  scope). Never print, log, write to a file or commit it, and never put it in `appsettings.json`
  or tests. To check it, print only whether it is set:
  `if ([Environment]::GetEnvironmentVariable('ELEVENLABS_API_KEY','User')) { 'set' } else { 'missing' }`.
  Terminals started before the variable was set do not see it; restart VS Code or the terminal.
- A real transcription request spends the user's ElevenLabs credits and sends audio to
  ElevenLabs, so ask before making one. Tests use `StubHttpHandler`; manual runs can point
  `ElevenLabs__BaseUrl` at a local stub.
- Do not turn on trimming or Native AOT: NAudio reaches Windows audio through COM interop.

## Conventions

- .NET 10 and C# 14, nullable on, file-scoped namespaces, primary constructors, records and
  `init` for immutable data, target-typed `new`, collection expressions, LINQ where it reads
  well, async/await, the built-in DI container.
- HTTP APIs are called through Refit interfaces registered with `AddRefitClient`, not requests
  built by hand with `HttpClient`.
- No `ArgumentNullException` guards on DI-injected parameters.
- XML `<summary>` comments on every class and method, tests included.
- Errors: log at each layer with the existing logger; rethrow with `throw;` (never `throw ex;`);
  never swallow exceptions. The deliberate exceptions are listed in `docs/HANDOFF.md` section 6.4,
  and each carries a comment saying why. Hold any new one to the same standard.
- TDD with NUnit and Moq: write the failing test first. Run `dotnet test` before handing code over.
- Keep `README.md` in step when behaviour changes.

## Working with Gulzar

- Balanced answers: not terse, not padded. No em dashes.
- When questions are needed, ask them all at once, as multiple choice where possible.
- Cite sources for research and other non-code answers.
- Arabic terms in IJMES transliteration (this governs `src/ClassScribe/keyterms.txt`).

## Commands (PowerShell, from the repo root)

```powershell
dotnet build
dotnet test
dotnet run --project src/ClassScribe                                   # record; Q or Ctrl+C stops and transcribes
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\folder-or-file"
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

- Exit codes: 0 success; 1 failure; 2 unknown command; 130 cancelled.
- Recordings go to `%USERPROFILE%\ClassScribe\<yyyy-MM-dd_HH-mm-ss>\`.
- Settings are in `src/ClassScribe/appsettings.json`, read from the exe's folder; environment
  overrides such as `ElevenLabs__BaseUrl` work.

## Layout

- `src/ClassScribe/Audio`: capture (`Windows/` holds the WASAPI code), `RecordingSession`, WAV
  writing, upload preparation.
- `src/ClassScribe/Transcription`: ElevenLabs client, glossary loader, transcript formatter, workflow.
- `src/ClassScribe/Commands`: the `record` and `transcribe` commands and keyboard signals.
- `tests/ClassScribe.Tests`: NUnit and Moq; hand-written fakes for the device, clock and HTTP
  transport are in `TestDoubles.cs`.
