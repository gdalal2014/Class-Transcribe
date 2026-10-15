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
  3  Transcribe a recent class
  4  Transcribe a file
  5  Open the recordings folder
  Q  Quit
```

- **1 Record only** records until you press Q and keeps the recording, to transcribe later.
- **2 Record and transcribe** records until you press Q, then sends the recording to ElevenLabs.
- **3 Transcribe a recent class** lists the last nine classes, newest first; press a class's number.
  A class that already has a transcript is only done again after you press Y, because it is billed again.
- **4 Transcribe a file** asks for a file: drag it into the window, or type its path, and press Enter.
- **5 Open the recordings folder** shows it in Explorer.

When a choice is done, the menu comes back. Q or Ctrl+C leaves it.

### While recording

Start recording before the class and leave the window in the background. Play the class as usual,
and keep the sound on: on some sound drivers, muting Windows also silences the recording.
These keys work in the ClassScribe window:

| Key | What it does |
| --- | --- |
| Q, or Ctrl+C | Stops. With choice 2 the transcript follows; a second Ctrl+C cancels the upload. |
| P | Pauses, for a break. Nothing is recorded until you press P again. |
| M | Marks the moment. The mark shows in the transcript at that point, as `*** Mark 1 ***`. |

Marks are kept in `marks.txt` in the class folder, one per line. You can type a label after a mark's
time, such as `00:42:13.250 Definition of jawhar`; the next transcript of that class then shows
`*** Mark 1: Definition of jawhar ***`.

### Where things go

Recordings and transcripts go to the folder set as `Recording:OutputFolder` in `appsettings.json`,
which is `%USERPROFILE%\ClassScribe` unless you change it:

```
%USERPROFILE%\ClassScribe\
    2026-10-14_19-30-00\        one folder per class
        recording-001.wav       the audio as captured (kept)
        marks.txt               the marks set with M, if any
        transcript.txt          paragraphs with [hh:mm:ss] positions
        transcript.json         the full ElevenLabs response, with word timings
    Lecture 5\                  one folder per transcribed file; the file itself stays where it is
        transcript.txt
        transcript.json
```

When a transcript is ready, its folder and the transcript open by themselves. Transcribing a class
again replaces its transcript; transcribing a file again gives it a new folder (`Lecture 5_2`).

The positions are positions in the recording. While nothing at all is playing (a paused video,
for instance) Windows usually hands over no audio, so a pause can be shorter in the recording
than it was in the room. Time paused with P is left out too.

### Without the menu

```powershell
dotnet run --project src/ClassScribe -- record           # record, then transcribe
dotnet run --project src/ClassScribe -- record-only      # record only
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\class folder or file"
```

## Dry run before the first real class

Take two minutes to check the whole path on your machine:

1. Start ClassScribe and press 2.
2. Play any video with speech for 30 seconds.
3. Press Q and read `transcript.txt` when it opens.

## How the recording is protected

- Audio goes to disk as it arrives, and the file's header is refreshed every 5 seconds. If the
  app is killed, about the last second is lost; after a power cut, at most those few seconds.
  Transcribing reads such a file to its very end, whatever its header says.
- Pausing with P saves the file first, so a crash during a break loses nothing recorded before it.
- The transcript step starts only after the audio file is closed. If the upload fails, the audio is
  untouched and the class can be transcribed again.
- If Windows switches output device (headphones plugged in, Bluetooth dropping), ClassScribe follows
  it and continues in a new segment file. The segments are joined before upload.
- If the device fails, it is reopened. While there is no output device at all, ClassScribe keeps
  trying every two seconds.
- If the disk cannot be written, it says so and continues in a new file as soon as writing works again.
- It warns when it has heard nothing for 60 seconds, except while paused.
- An existing recording is never overwritten.

## The glossary

`keyterms.txt` (next to the app) lists terms ElevenLabs should spell as written, one per line.
It ships with a starter list for an Ashʿarī kalām class in IJMES transliteration. Edit it freely:

- up to 1000 terms, each under 50 characters and at most 5 words
- lines starting with `#` are comments
- if a term keeps coming out wrong, try a plainer spelling of it

After changing the glossary you can transcribe an old class again (menu choice 3).
That is billed as a new transcription.

## Settings

`appsettings.json` sits next to the app. The ones you are most likely to change:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Recording:OutputFolder` | `%USERPROFILE%\ClassScribe` | Where class folders and transcripts go. Environment variables work. |
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
| `Could not write the recording`, `Still cannot write the recording` | The disk is full or went away. Free some space; the recording continues by itself in a new file. |
| `Storing the captured audio took ...` | The disk was too slow for a moment, and the recording has a gap there. Record to an internal drive. |
| `ElevenLabs rejected the API key` | Check `ELEVENLABS_API_KEY` in a new terminal, and that the key may use Speech to Text. |
| The upload fails or times out | The audio is safe. Transcribe the class again with menu choice 3. |
| The window was closed mid-class | The audio up to that moment is in the class folder. Transcribe it with menu choice 3. |

## Layout

```
src/ClassScribe/
    Audio/            capture, WAV writing, resampling
    Audio/Windows/    the only Windows-specific code (WASAPI loopback through NAudio)
    Transcription/    ElevenLabs client (Refit), glossary, transcript layout
    Storage/          class folders and marks
    Commands/         the menu, record and transcribe
tests/ClassScribe.Tests/    NUnit and Moq
```

In the tests, the transcription service, the upload preparation, the command runner and the desktop
are Moq mocks. The audio device, the keyboard, the clock and the HTTP transport are small hand-written
stand-ins (`TestDoubles.cs`), because the tests drive those step by step: deliver this audio, press P,
fail now, switch device.
