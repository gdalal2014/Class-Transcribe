# ClassScribe

Records the sound your computer plays during a class. When you stop it, it sends the recording
to ElevenLabs (Scribe v2) and saves the transcript.

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

1. Start it before the class and leave the window in the background.
2. Play the class as usual. Keep the sound on: on some sound drivers, muting Windows also silences the recording.
3. Press **Q** in the ClassScribe window when the class ends.

The class folder opens when the transcript is ready:

```
%USERPROFILE%\ClassScribe\2026-10-11_14-05-00\
    recording-001.wav    the audio as captured (kept)
    transcript.txt       paragraphs with [hh:mm:ss] positions
    transcript.json      the full ElevenLabs response, with word timings
```

The positions are positions in the recording. While nothing at all is playing (a paused video,
for instance) Windows usually hands over no audio, so a pause can be shorter in the recording
than it was in the room.

To redo a transcript, or to transcribe a recording made with something else (an OBS video, for example):

```powershell
dotnet run --project src/ClassScribe -- transcribe "C:\path\to\class folder or file"
```

## Dry run before the first real class

The part that talks to Windows audio could only be compiled, not run, where this was written, and
no real request was sent to ElevenLabs from there. Take two minutes to prove both on your machine:

1. Start ClassScribe.
2. Play any video with speech for 30 seconds.
3. Press Q and read `transcript.txt`.

## How the recording is protected

- Audio goes to disk as it arrives, and the file's header is refreshed every 5 seconds. If the
  app is killed, about the last second is lost; after a power cut, at most those few seconds.
  `transcribe` reads such a file to its very end, whatever its header says.
- The transcript step starts only after the audio file is closed. If the upload fails, the audio is
  untouched and `transcribe` can be run again.
- If Windows switches output device (headphones plugged in, Bluetooth dropping), ClassScribe follows
  it and continues in a new segment file. The segments are joined before upload.
- If the device fails, it is reopened. While there is no output device at all, ClassScribe keeps
  trying every two seconds.
- If the disk cannot be written, it says so and continues in a new file as soon as writing works again.
- It warns when it has heard nothing for 60 seconds.
- An existing recording is never overwritten.

## The glossary

`keyterms.txt` (next to the app) lists terms ElevenLabs should spell as written, one per line.
It ships with a starter list for an Ashʿarī kalām class in IJMES transliteration. Edit it freely:

- up to 1000 terms, each under 50 characters and at most 5 words
- lines starting with `#` are comments
- if a term keeps coming out wrong, try a plainer spelling of it

After changing the glossary you can run `transcribe` on an old class folder to get a new transcript.
That is billed as a new transcription.

## Settings

`appsettings.json` sits next to the app. The ones you are most likely to change:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Recording:OutputFolder` | empty | Where class folders go. Empty means `%USERPROFILE%\ClassScribe`. |
| `Recording:SilenceWarningSeconds` | 60 | Warn after this much silence. |
| `Recording:KeepUploadFile` | false | Keep the 16 kHz copy that was uploaded. |
| `Recording:OpenFolderWhenDone` | true | Open the class folder in Explorer at the end. |
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

Run `publish\ClassScribe.exe`; double-clicking it starts a recording. Do not turn on trimming or
Native AOT, because NAudio reaches Windows audio through COM interop.

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
| The upload fails or times out | The audio is safe. Run `transcribe` on the class folder again. |
| The window was closed mid-class | The audio up to that moment is in the class folder. Run `transcribe` on it. |

## Layout

```
src/ClassScribe/
    Audio/            capture, WAV writing, resampling
    Audio/Windows/    the only Windows-specific code (WASAPI loopback through NAudio)
    Transcription/    ElevenLabs client, glossary, transcript layout
    Commands/         record and transcribe
tests/ClassScribe.Tests/    NUnit and Moq
```

In the tests, the transcription service and the upload preparation are Moq mocks. The audio device,
the clock and the HTTP transport are small hand-written stand-ins (`TestDoubles.cs`), because the
tests drive those step by step: deliver this audio, fail now, switch device.
