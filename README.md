# ClassScribe

Records the sound your computer plays during a class. When you stop it, it can send the recording
to ElevenLabs (Scribe v2) and save the transcript.

## Set up once

1. Check the .NET 10 SDK: `dotnet --version` should print `10.x`.
   If it does not: `winget install Microsoft.DotNet.SDK.10`
2. Store your ElevenLabs API key in an environment variable, then open a new terminal.
   A key that is restricted to Speech to Text is all the app needs.
   ```powershell
   setx ELEVENLABS_API_KEY "your-key"
   ```
3. From this folder, run the tests:
   ```powershell
   dotnet test
   ```

## Use it

```powershell
dotnet run --project src/ClassScribe
```

ClassScribe starts with a menu. Press the number of what you want:

```
  1  Record only
  2  Record and transcribe
  3  Transcribe a recent recording
  4  Transcribe a file
  5  Open the recordings folder
  6  Choose what to record: a device or an app
  Q  Quit
```

- **1 Record only** asks for a name for the recording, such as `Kalam`, then records until you press
  Q and keeps the recording, to transcribe later. Esc at the name goes back to the menu.
- **2 Record and transcribe** does the same, then sends the recording to ElevenLabs.
- **3 Transcribe a recent recording** lists the last nine recordings, newest first; press one's number.
  A recording that already has a transcript is only done again after you press Y, because it is billed again.
- **4 Transcribe a file** asks for a file: drag it into the window, or type its path, and press Enter.
- **5 Open the recordings folder** shows it in Explorer.
- **6 Choose what to record** lists the output devices and the apps with sound; see below.

When a choice is done, the menu comes back. Q or Ctrl+C leaves it.

### What is recorded

By default ClassScribe records whichever output device the class is playing through. It starts on
the device with sound, or else on Windows' default device, and looks at every output device once a
second. When the recorded device has been quiet for two seconds while sound has been playing
elsewhere for a second, it moves there and continues in a new segment file; the segments are joined
before upload. A short notification sound on another device does not move it.

Menu choice 6 lists the connected output devices and the apps with sound open, and pins one for the
rest of the run:

- **A device**: that device and no other, even when sound plays elsewhere. If it is not connected
  when the recording starts, ClassScribe waits for it.
- **An app**, such as `chrome` or `msedge`: that app's sound alone, whatever device it plays
  through, so a video in another browser or a notification stays out. Windows cannot tell two tabs
  of the same browser apart. If the app has no sound open yet, ClassScribe waits until it plays.

To keep a choice, set `Recording:Device` or `Recording:App` in `appsettings.json`.

### While recording

Start recording before the class and leave the window in the background. Play the class as usual,
and keep the sound on: on some sound drivers, muting Windows also silences the recording.
These keys work in the ClassScribe window:

| Key | What it does |
| --- | --- |
| Q, or Ctrl+C | Stops. With choice 2 the transcript follows; a second Ctrl+C cancels the upload. |
| P | Pauses, for a break. Nothing is recorded until you press P again. |
| M | Marks the moment. The mark shows in the transcript at that point, as a heading `## [00:42:13] Mark 1`. |

Marks are kept in the recording's `.marks.txt` file, one per line. You can type a label after a
mark's time, such as `00:42:13.250 Definition of jawhar`; the next transcript of that recording then
shows `## [00:42:13] Mark 1: Definition of jawhar`.

### Where things go

Recordings and transcripts go to the folder set as `Recording:OutputFolder` in `appsettings.json`,
which is `%USERPROFILE%\ClassScribe` unless you change it. Each name gets a folder, and every file of
a recording carries the name and the time the recording started, in UTC (the `Z`):

```
%USERPROFILE%\ClassScribe\
    Kalam\                                      one folder per name, shared by its recordings
        Kalam_2026-10-14_19-30-00Z.wav          the audio as captured (kept)
        Kalam_2026-10-14_19-30-00Z_part2.wav    only after a device switch, a failure or 1500 MB
        Kalam_2026-10-14_19-30-00Z.marks.txt    the marks set with M, if any
        Kalam_2026-10-14_19-30-00Z.md           the transcript
        Kalam_2026-10-14_19-30-00Z.json         the full ElevenLabs response, with word timings
    Lecture 5\                                  a transcribed file; the file itself stays where it is
        Lecture 5_2026-10-15_09-00-00Z.md
        Lecture 5_2026-10-15_09-00-00Z.json
```

The transcript is a Markdown file: the name as its title, when it was recorded, then paragraphs
that start with their `[hh:mm:ss]` position, and the marks as headings, so they show in the outline
of any Markdown viewer (VS Code, Obsidian). It is plain text underneath, so Notepad opens it too.
ElevenLabs itself answers with JSON (the text and every word's timing); that is kept as the `.json`.

When a transcript is ready, its folder and the transcript open by themselves. A transcript is never
overwritten: transcribing a recording again adds `Kalam_2026-10-14_19-30-00Z (2).md`.

The positions are positions in the recording. While nothing at all is playing (a paused video,
for instance) Windows usually hands over no audio, so a pause can be shorter in the recording
than it was in the room. Time paused with P is left out too.

### Without the menu

```powershell
dotnet run --project src/ClassScribe -- record "Kalam"          # record, then transcribe
dotnet run --project src/ClassScribe -- record-only "Kalam"     # record only
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\recording folder or file"
```

Without a name, `record` and `record-only` ask for one. `transcribe` on a recording's folder takes
the recording started last in it; on one of its `.wav` files, that whole recording.

## Dry run before the first real class

Take two minutes to check the whole path on your machine:

1. Start ClassScribe, press 2 and type a name, such as `Dry run`.
2. Play any video with speech for 30 seconds.
3. Press Q and read the transcript when it opens.

## How the recording is protected

- Audio goes to disk as it arrives, and the file's header is refreshed every 5 seconds. If the
  app is killed, about the last second is lost; after a power cut, at most those few seconds.
  Transcribing reads such a file to its very end, whatever its header says.
- Pausing with P saves the file first, so a crash during a break loses nothing recorded before it.
- The transcript step starts only after the audio file is closed. If the upload fails, the audio is
  untouched and the class can be transcribed again.
- When the sound moves to another output device (headphones plugged in, Bluetooth dropping),
  ClassScribe follows it within a few seconds and continues in a new segment file. The segments are
  joined before upload.
- If the device fails, it is reopened. While there is nothing to record (no output device, a chosen
  device not connected, a chosen app not playing), ClassScribe keeps looking every two seconds and
  says so every thirty.
- If the disk cannot be written, it says so and continues in a new file as soon as writing works again.
- It warns when it has heard nothing for 60 seconds, except while paused.
- An existing recording is never overwritten.

## The glossary

`keyterms.txt` (next to the app) lists terms ElevenLabs should spell as written, one per line.
It ships with a starter list for an Ashʿarī kalām class in IJMES transliteration. Edit it freely:

- up to 1000 terms, each under 50 characters and at most 5 words
- lines starting with `#` are comments
- if a term keeps coming out wrong, try a plainer spelling of it

After changing the glossary you can transcribe an old recording again (menu choice 3).
That is billed as a new transcription.

## Settings

`appsettings.json` sits next to the app. The ones you are most likely to change:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Recording:OutputFolder` | `%USERPROFILE%\ClassScribe` | Where recordings and transcripts go, one folder per name. Environment variables work. |
| `Recording:Device` | empty | Part of the name of the output device to record, such as `Pixel Buds`. Empty means the device the class plays on. |
| `Recording:App` | empty | The app whose sound to record, by its process name, such as `chrome`. Overrides `Recording:Device`. |
| `Recording:SilenceWarningSeconds` | 60 | Warn after this much silence. |
| `Recording:KeepUploadFile` | false | Keep the 16 kHz copy that was uploaded. |
| `Recording:OpenFolderWhenDone` | true | Open the folder in Explorer when the recording or transcript is ready. |
| `Recording:OpenTranscriptWhenDone` | true | Open the transcript when it is ready. |
| `ElevenLabs:LanguageCode` | empty | Empty lets ElevenLabs detect the language. |
| `ElevenLabs:NoVerbatim` | false | Drop filler words and false starts. |
| `ElevenLabs:TimeoutMinutes` | 60 | Longest wait for one upload and its transcript. |
| `Transcript:IncludeTimestamps` | true | Start paragraphs with `[hh:mm:ss]`. |
| `Logging:Console:FormatterName` | plain | Set to `simple` to see full stack traces. |

The API key is deliberately not in this file. It is read from `ELEVENLABS_API_KEY`.

## A single .exe

```powershell
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Double-click `publish\ClassScribe.exe` for the menu. Dropping an audio or video file on it
transcribes that file; the window then stays open until you press Enter. Do not turn on trimming
or Native AOT, because NAudio reaches Windows audio through COM interop.

## Cost and privacy

- ElevenLabs lists Scribe v2 at $0.22 per audio hour through the API, plus a surcharge of about 20% when keyterms are sent.
  A two-hour class is roughly 50 cents. Check your usage page after the first run.
- The audio is uploaded to ElevenLabs. Keep transcripts for your own study, in line with what your teacher allows.

## If something goes wrong

| What you see | What to do |
| --- | --- |
| `No sound for 60 seconds` | Check that the class is playing through the device named in the message and that Windows is not muted. |
| `Cannot capture an output device right now` | Windows has no usable output device at that moment. ClassScribe keeps trying; plug in or switch on a device. |
| `Nothing to record yet; waiting for ...` | The chosen device is not connected, the chosen app is not playing, or there is no output device. ClassScribe keeps looking; connect the device or start the sound, or press Q and choose something else with 6. |
| `Could not write the recording`, `Still cannot write the recording` | The disk is full or went away. Free some space; the recording continues by itself in a new file. |
| `Storing the captured audio took ...` | The disk was too slow for a moment, and the recording has a gap there. Record to an internal drive. |
| `ElevenLabs rejected the API key` | Check `ELEVENLABS_API_KEY` in a new terminal, and that the key may use Speech to Text. |
| The upload fails or times out | The audio is safe. Transcribe the recording again with menu choice 3. |
| The window was closed mid-class | The audio up to that moment is in the recording's folder. Transcribe it with menu choice 3. |

## Layout

```
src/ClassScribe/
    Audio/            capture, WAV writing, resampling
    Audio/Windows/    the only Windows-specific code (WASAPI loopback through NAudio)
    Transcription/    ElevenLabs client (Refit), glossary, transcript layout
    Storage/          recording folders, file names and marks
    Commands/         the menu, record and transcribe
tests/ClassScribe.Tests/    NUnit and Moq
```

In the tests, the transcription service, the upload preparation, the command runner and the desktop
are Moq mocks. The audio device, the keyboard, the clock and the HTTP transport are small hand-written
stand-ins (`TestDoubles.cs`), because the tests drive those step by step: deliver this audio, press P,
fail now, switch device.
