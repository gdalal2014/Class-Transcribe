# ClassScribe

Records a class and, when you stop it, can send the recording to ElevenLabs (Scribe v2) and save
the transcript. It records in one of three ways: the sound your computer plays, which is an online
class as you hear it; that sound and your microphone together, so that what you say in the class is
in the transcript too; or your microphone alone, for a class held in a room. See
[Your microphone](#your-microphone). Once it is signed in to your own Google Drive, each class also
goes up there by itself, every file checked against Drive's own MD5, and the audio can then be
deleted from the laptop to free room (see [Google Drive](#google-drive)). It also takes YouTube
videos, one or a list of them, and makes each an ordinary recording of a course, transcribed like a
class: recorded as it plays in a Chrome window of ClassScribe's own, or, for videos you may download,
downloaded with yt-dlp (see [YouTube videos](#youtube-videos)).

## Set up once

1. Check the .NET 10 SDK: `dotnet --version` should print `10.x`.
   If it does not: `winget install Microsoft.DotNet.SDK.10`
2. Store your ElevenLabs API key in an environment variable.
   A key that is restricted to Speech to Text is all the app needs. To see the account's credits in
   the log after each transcript, also allow User (read) on it; without that, ClassScribe logs an
   estimate from the price list and says once that the key may not read the account (see
   [Cost and privacy](#cost-and-privacy)).
   ```powershell
   setx ELEVENLABS_API_KEY "your-key"
   ```
   `setx` only reaches programs started after it, so close your terminal and open a new one. In
   VS Code, quit every VS Code window and start VS Code again: a new terminal inside a VS Code that
   is still running keeps the old environment. To check that a terminal sees the key, without
   showing it:
   ```powershell
   if ($env:ELEVENLABS_API_KEY) { 'set' } else { 'missing' }
   ```
3. From this folder, run the tests:
   ```powershell
   dotnet test
   ```
4. For classes, publish a copy of ClassScribe outside OneDrive and start that copy, not `dotnet run`:
   ```powershell
   dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\ClassScribe"
   ```
   Make a shortcut to `%LOCALAPPDATA%\Programs\ClassScribe\ClassScribe.exe` (see
   [A program to double-click](#a-program-to-double-click)). A recording started from that copy runs
   in a background process of its own, so closing the window does not end it, and rebuilding the
   repo during a class never touches it. Under `dotnet run` the recording runs in the window
   instead, and ClassScribe says so: `dotnet run` ends every program it started when it ends.

   Your settings are your own and never part of the program folder: the published copy reads
   `%APPDATA%\ClassScribe\appsettings.json`, which ClassScribe makes at your first change, as a full
   copy of every setting, and which an update never changes. Until then the menu's header says it
   reads the defaults (`Settings: ...\appsettings.json (the defaults; your own file, ..., is made at
   your first change in S or C, or with settings set)`). There are three ways to change a setting:
   the menu (S for the recordings folder and the camera's picture size, C for saved classes, and S
   after a pick in choices 6 and 7), `ClassScribe settings set` in PowerShell, or Notepad
   (`ClassScribe settings open`). See [Settings](#settings).
5. In Bitdefender, add an exception for that `ClassScribe.exe` under Protection > Advanced Threat
   Defense (ATD) > Settings > Manage exceptions, with the exact path, and add it again after each
   publish, since a changed file may count as new. ATD watches programs that start copies of
   themselves without a window, which is what the background recorder is. If ATD ends the recorder
   anyway, ClassScribe continues the recording by itself while a window shows it, and the next start
   reports it with C to continue it (see [If a recording was interrupted](#if-a-recording-was-interrupted)).
6. If you will record your microphone, change two settings once; ClassScribe warns at the start of
   a recording with your microphone while they are not. In Lenovo Vantage, set Device Settings >
   Sound > Input to Off:
   its noise cancelling can cut out a lecturer across the room. In Windows, open Sound > More sound
   settings > Communications and choose "Do nothing": otherwise Windows lowers other sounds by 80%
   while a microphone is open, which makes the class quieter, in the recording too. Bitdefender's
   Microphone Monitor may tell you that `ClassScribe.exe` is using the microphone; that is the
   recording, and nothing to act on.
7. If you wear Bluetooth earbuds or a headset in class, keep the class page off their microphone. In
   Chrome, click the microphone icon at the right end of the address bar, shown while the page uses
   the microphone, and choose the laptop's built-in `Microphone Array`; the same choice is under
   Settings > Privacy and security > Site settings > Microphone, and the class page's own microphone
   setting, where it has one, does the same. Used as a microphone, Bluetooth earbuds switch to call
   mode (sound in one ear only, at call quality), and Bluetooth then carries the class's sound to
   them in a way Windows cannot record, so ClassScribe records silence. ClassScribe warns when it
   sees this, at the start of a recording and whenever it begins (see
   [While recording](#while-recording)).
8. Optional, for YouTube videos you may download (see [YouTube videos](#youtube-videos)): in a new
   terminal run `winget install --source winget --id yt-dlp.yt-dlp`, which brings Deno and FFmpeg
   with it, then `ClassScribe youtube check`, which should end with `yt-dlp and its helpers are in
   place.` If Bitdefender flags `yt-dlp.exe`, a program that unpacks itself each time it starts, add
   an exception for it as in step 5, with the path `youtube check` printed. Recording videos as they
   play needs nothing new: only Chrome.
9. Optional, if you have YouTube Premium: sign in to YouTube once in ClassScribe's own Chrome window
   for videos, so that the videos it records as they play come without ads (see
   [Your Premium sign-in in the video window](#your-premium-sign-in-in-the-video-window)).

### Google Drive (set up once)

ClassScribe can put every class in your own Google Drive: the audio of both tracks, the transcripts,
ElevenLabs' answers, the marks and the log, uncompressed, in `My Drive > ClassScribe`, in folders that
mirror the laptop's (`My Drive > ClassScribe > Mantiq > 2026-10-17`). It needs a Google Cloud project
of your own, which costs nothing, and one sign-in. It asks Google only for the files and folders it
makes itself: it cannot see anything else in your Drive, and it shares nothing.

1. Open https://console.cloud.google.com/ signed in with the Google account whose Drive should hold
   the classes. Make a new project named `ClassScribe` (no billing account is needed).
2. Open https://console.cloud.google.com/apis/library/drive.googleapis.com in that project and click
   **Enable**.
3. Go to **Google Auth platform** (Menu > Google Auth platform). Under **Branding**, click Get started:
   app name `ClassScribe`, your Gmail address as the support email; under **Audience** choose
   **External**; give your Gmail address as the contact; agree and create.
4. Under **Data Access**, click **Add or Remove Scopes**, tick `.../auth/drive.file` ("See, edit,
   create, and delete only the specific Google Drive files you use with this app"), then Update and
   **Save**. ClassScribe asks for this one scope and no other.
5. Under **Audience**, click **Publish app** and confirm, so the status reads **In production**. Do this
   before you sign in: while the app is in Testing, Google ends the sign-in after 7 days. An app that
   asks only for this scope, for your own use, does not need Google's verification.
6. Under **Clients**, click **Create client**, choose **Desktop app**, name it `ClassScribe desktop`,
   and click **Create**. In the box that opens, click **Download JSON**: Google shows the secret only
   this once. The file lands in Downloads as `client_secret_....json`.
7. Start ClassScribe, choose **9** (Google Drive), then **S**, and drag that file onto the window (or
   type its path) and press Enter. Your browser opens: choose your account, click Continue if Google
   says the app is being tested, and **Allow**. The window says
   `Signed in to Google Drive as ... Uploads go to My Drive > ClassScribe (...)`, and the folder is
   there in My Drive. The same from a terminal:
   `ClassScribe drive signin --client "$env:USERPROFILE\Downloads\client_secret_....json"`.
8. Delete the downloaded file: ClassScribe keeps its own copy of the client, encrypted for your
   Windows account (see [Where things go](#where-things-go)). Never put it in the repo; `.gitignore`
   keeps `client_secret*.json` out of git.
9. Upload a class that is already on the laptop, such as the class of 17 October once it is in
   `%USERPROFILE%\ClassScribe\Mantiq\2026-10-17` with its files named `Mantiq_2026-10-17_14-04-42Z.*`:
   choose 9, then R, and press its number; or 9, then F, and drag its folder onto the window; or, from
   a terminal:
   ```powershell
   ClassScribe drive upload "$env:USERPROFILE\ClassScribe\Mantiq\2026-10-17"
   ```
   It goes to My Drive > ClassScribe > Mantiq > 2026-10-17. Your own files in that folder, such as
   the class's PDFs and slides, stay on the laptop alone. The last line says where it is, with the
   folder's address: `Mantiq_2026-10-17_14-04-42Z is on Google Drive: My Drive > ClassScribe > Mantiq >
   2026-10-17 (4 files, 727 MB, each checked against Drive's MD5): https://...`. Then look in
   https://drive.google.com under My Drive > ClassScribe > Mantiq > 2026-10-17. The first upload
   of a class of two hours takes about 10 minutes on a line that uploads 10 Mbit/s.

Since the app is not verified, Google's page may name the project rather than show ClassScribe's
name and logo; that is expected. The browser comes back to ClassScribe at `http://127.0.0.1:<port>/`,
on this computer only, and the page there says you can close the tab. If the browser does not open,
the window shows the address to copy into it. ClassScribe waits up to 5 minutes; Esc gives up.

If the console will not publish the app, leave it in Testing, add your Gmail address under
Audience > Test users, and sign in again with 9 > S whenever ClassScribe says Google ended the
sign-in (every 7 days).

From then on each class goes up by itself after its transcript (with record only, after recording).
If the laptop is offline, it waits and goes up after the next class, or with U in the report at the
start, or 9 > U. A transcript made later (menu choices 3 and 4, `transcribe`, `rebuild`) sends its
new `.md` and `.json`, and a file transcribed on its own sends its transcript and answer. Deleting
files on the laptop never touches Drive. See [Google Drive](#google-drive) for what goes up, where,
and how to free room on the laptop afterwards.

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
  7  Microphone and camera: none, with the class, or in person
  8  Check the microphone, or a camera (a camera opens only after you type Y)
  9  Google Drive: upload, free room on this laptop, sign in
  Y  YouTube: record videos as they play, or download them, then transcribe
  C  Saved classes: start, add, change or remove one
  S  Settings: recordings folder, camera picture size, the settings file
  A  Show the recording in progress        (only while one records)
  Q  Quit
```

Before the menu, ClassScribe reports what happened while no window showed a recording: a transcript
made in the background (O opens it), or a recording that ended unexpectedly, with C to continue it,
T to transcribe it and F to forget it (see [If a recording was interrupted](#if-a-recording-was-interrupted)).
If a recording is running, the menu shows it at once; Esc comes back to the menu, which then offers
A to show it again. Above the choices, the header names what runs in the background, such as
`Recording now: "Kalam" since 19:00 (00:42:13). A shows it.` or
`Being transcribed in the background: "Fiqh" (uploading 40%).` A YouTube video's run is named as the
video, never as its course: `Recording now: the YouTube video "The creed, part 1" since 19:00
(00:12:13). A shows it.`, and a transcript that a YouTube batch makes in another window
`Being transcribed in another ClassScribe window (process 1234): the YouTube video "The creed, part 1" (uploading 40%).`

Once Google Drive is set up, the report at the start also says what waits to go up, such as
`Google Drive: 1 recording waits to go up (Mantiq_2026-10-17_14-04-42Z, 727 MB; last try 17 Oct 21:40:
Google Drive could not be reached (...)).`, or for several `Google Drive: 3 recordings wait to go up
(2.1 GB in all; last try ...).`, with `not tried yet` for one that has not been tried, and
`, not finished` for a try that did not finish and left no error. Below it come `U  upload it now` (or `them`) and
`Enter  go on to the menu`. When ClassScribe is not signed in, it says
`ClassScribe is not signed in to Google Drive: 9 > S in the menu signs in, then 9 > U uploads it.`
and asks nothing. Before a command the report asks nothing either and gives the command instead:
`To upload it: <full path of ClassScribe.exe> drive upload`, after the sign-in advice
(`... Then, to upload it: ...`) when ClassScribe is not signed in.

The header's last line says where Google Drive stands, from what ClassScribe knows on the laptop,
without asking Google: `Google Drive: signed in; nothing waits to go up.`,
`Google Drive: 2 recordings wait to go up (1.4 GB). 9 > U uploads them.`,
`Google Drive: uploading Mantiq_2026-10-17_14-04-42Z (41%); 1 more waits.` while a recorder or
another ClassScribe window uploads it, `Google Drive: not signed in; 1 recording waits to go up. 9 > S signs in.`, or
`Google Drive: the sign-in cannot be read. 9 > S signs in again.` While signed in with
`Drive:Enabled` set to `false`, the line ends ` Uploads after each class are off (Drive:Enabled).`
Before Drive is set up the header has no Drive line. A class being recorded or transcribed now is
never counted as waiting; it goes up once it has stopped.

- **1 Record only** asks for a name for the recording, such as `Kalam`, then records until you press
  Q twice and keeps the recording, to transcribe later. Esc at the name goes back to the menu. When a
  recording of that name stopped within the last hour (`Recording:ContinueWithinMinutes`) without a
  transcript, it asks first: `C continues it as its next part, N starts a new recording, Esc goes back`.
  The recording runs in a background process of its own (`Recording:SeparateProcess`), and the window
  says `Recording in the background: closing this window does not stop it. Esc leaves this view;
  start ClassScribe again to see it.` Only one recording runs at a time; while one runs, 1 and 2 say
  so, and A shows it. When the name you type is a saved class's, or its course (see
  [Saved classes](#saved-classes)), it asks first: `Mantiq is a saved class (chrome, with your
  microphone (...)). Enter records it as saved; O records with choice 6's source instead.` Enter
  records the class with its own source and folder; O records under that name as 6 and 7 say, into
  the settings' folder. A course that two saved classes share lists both, and a number picks one.
- **2 Record and transcribe** does the same, then sends the recording to ElevenLabs and, once the
  transcript is saved, says what it cost (see [Cost and privacy](#cost-and-privacy)).
- **3 Transcribe a recent recording** lists the last nine recordings, newest first, in the course and
  date folders or flat in the folder of their name, each with its length, its date folder for a
  later class of the same day (`in 2026-10-17 (2)`), `2 parts` when it was continued after an
  interruption, `lesson video` or `lesson video not made yet` for a lesson recorded with the camera,
  `audio on Drive` when its audio was deleted from the laptop after it went up to
  Google Drive, and `transcript ready`, `answer kept`, `RECORDING NOW` or `no transcript yet`; press
  one's number, or V then a lesson's number to make its video, free (see
  [The lesson's video](#the-lessons-video)). A recording whose audio is on Drive alone says where its audio is
  (`The audio of ... is on Google Drive, not on this laptop (My Drive > ...). To transcribe it again,
  download its .wav files from there into ... first.`) and offers only R, which makes its transcript
  again for free from its saved answer (see [Google Drive](#google-drive)). A recording that has a
  transcript, or an answer ElevenLabs was paid for (see [Where things go](#where-things-go)), is not
  sent again at once, because that is billed again:
  R makes its transcript again for free from ElevenLabs' saved answer (see
  [A transcript again, for free](#a-transcript-again-for-free)), Y transcribes it again, which is
  billed again, and any other key goes back. A recording that is being recorded now, in another
  window, is not sent until it has stopped. A recording whose files cannot be read just now,
  because another program holds one open, is left out of the list with a warning; close that
  program and choose 3 again.
- **4 Transcribe a file** asks for a file: drag it into the window, or type its path, and press
  Enter. A WAV file, 24-bit, floating-point and surround ones included, is first reduced to a small
  16 kHz mono copy; one that another recorder never finished is read to its end, with a warning.
  Any other audio or video file, or a WAV ClassScribe cannot read, is uploaded as it is. One of a
  recording's own `.wav` files, or a folder of recordings (a course's folder, which means its class
  started last, in its date folders or flat in it; a class's date folder; or a folder of the old
  layout), gets the same care as in choice 3: R or Y first when it would be billed again, only R for
  a class whose audio is on Google Drive alone, and nothing sent while it is being recorded.
- **5 Open the recordings folder** shows it in Explorer.
- **6 Choose what to record** lists the output devices and the apps with sound; see below. After a
  pick it says `Press S to keep this as the default (saved in ...\appsettings.json), or any other key
  to use it until ClassScribe is closed.` S saves it as `Recording:App` or `Recording:Device` (the
  other emptied), or both emptied for A, automatic.
- **7 Microphone and camera** asks whether to record your microphone with the class (W), alone for a
  class in the room (I) or not at all (N), then which microphone, and with the class which app it
  follows; see [Your microphone](#your-microphone). In person it then lists the cameras for a lesson
  video (a number, or Enter for none) and, for a camera, the picture's size; see
  [Recording with the camera](#recording-with-the-camera). The pick holds until ClassScribe is closed,
  unless you press S when asked, which saves it as the default: `Recording:Mode`, the microphone
  picked as `Recording:Microphone`, the app it follows as `Recording:MicrophoneFollows` and the
  picture's size as `Recording:CameraResolution`. The camera itself is never a setting.
- **8 Check the microphone, or a camera** asks W or C. **W** watches what the programs on your computer
  do with the microphone and writes a line for each change, without opening it; see
  [Checking what your class page does with your microphone](#checking-what-your-class-page-does-with-your-microphone).
  **C** is the camera check (`ClassScribe camera-check`): see [Checking the cameras](#checking-the-cameras).
  Esc goes back.
- **9 Google Drive** says whether ClassScribe is signed in to Google Drive, and as whom
  (`Google Drive: signed in as you@gmail.com since 17 Oct 2026.`), without asking Google. While
  signed in: U uploads what waits to go up (its line says how many and how much, such as
  `U  Upload what waits to go up (1 recording, 727 MB)`, or `nothing waits now`); R lists the recent
  recordings, each with `on Drive`, `waiting`, `not uploaded` or `RECORDING NOW`, and uploads the one
  whose number you press; F uploads a recording's file, a class's date folder, a course's folder or
  the recordings folder that you drag onto the window or type; L frees room on this laptop: it lists
  the recent recordings, says which audio files of the one you pick would be deleted, how much room
  that frees and what stays, and deletes them when you press Y, each first checked again against its
  copy on Drive (see [Free room on the laptop](#free-room-on-the-laptop)); a recording that is not
  all on Drive yet keeps its audio (`... is not all on Google Drive yet, so its audio stays here.
  Upload it first: R, then its number.`); I checks with Google (the account, the storage used,
  ClassScribe's folder in My Drive and what waits, as `drive status` does); S signs in again and O
  signs out. Not signed in, S signs in: drag the client file Google gave you onto the window, or press
  Enter alone to sign in again with the client ClassScribe keeps (see
  [Google Drive (set up once)](#google-drive-set-up-once)). Esc goes back.
- **Y YouTube** takes YouTube links, pasted one or more on a line or as a text file of links dragged
  onto the window (an empty line ends the list), then the course they belong to (Enter takes the
  last one named while the window is open), then R to record each as it plays in a Chrome window of
  ClassScribe's own or D to download its audio with yt-dlp, then T to transcribe each one (the line
  says about what an hour costs, such as `about $0.27 an hour of audio with the glossary`) or O to
  fetch only the audio. The plan follows; nothing is fetched, recorded or sent before you confirm it.
  Esc goes back at every step. The videos go to `Recording:OutputFolder`, as the menu's classes do.
  See [YouTube videos](#youtube-videos).
- **C Saved classes** lists your saved classes, each with what it records, or why it cannot be used
  (see [Saved classes](#saved-classes)). A class's number records it now, as saved: its course, its
  source and microphone, its own folder or the settings', transcribed unless it says to record only
  (`Recording Mantiq (saved class): chrome, with your microphone (...), into D:\Classes\Mantiq,
  then transcribed.`). N adds a class: L starts from the Lesson video preset (in person, with a
  camera), Enter answers each question; then the name, the course, what it records (A an app, D an
  output device, W whatever device the class plays on, P in person), your microphone and the app it
  follows, the recordings folder, whether to transcribe, and its own glossary last (a file's path, N
  for none, `-` for `ElevenLabs:KeytermsFile`'s; see [The glossary](#the-glossary)). E and a number changes a class, with its
  own answers for Enter; for a class that cannot be used it says why first, and Enter keeps each
  field as the file holds it, so a field the questions do not ask about is emptied with `ClassScribe
  class set "<name>" --clear <field>` or changed in the file (S, then O). D and a number removes it
  after Y (no recording is touched). Esc goes back.
- **S Settings** shows the settings file and offers 1, the recordings folder (drag a folder onto the
  window or type it, then Enter; a relative path is taken from your user folder, as in the setting):
  it says where the next class would go (`The next class of Mantiq would go to
  D:\Classes\Mantiq\2026-10-21.`), saves it, gives the warnings about that folder (see
  [Settings](#settings)), and says that nothing moved (`Recordings made before stay in
  C:\Users\you\ClassScribe; nothing was moved. ClassScribe list --output "C:\Users\you\ClassScribe"
  lists them.`); P, the camera's picture size (1 480p, 2 720p, 3 1080p, T to type a size); and O,
  which opens the settings file in Notepad. S opens even while the file has a problem, so that it can
  be fixed there.

When a choice is done, the menu comes back, after an error too. Q or Ctrl+C leaves it.

Above the choices, the menu shows the settings file it reads (`Settings: ...`), where recordings go
and what is recorded (`Recording from: the device the class plays on`, or with the microphone
`..., and Windows' default microphone (following any app that holds the microphone)`, or `your microphone "OBSBOT", in person`).
Each choice reads the settings again first, so an edit counts from the next
choice, without a restart (see [Settings](#settings)). When the file cannot be used, `ERROR` lines
under its name say why, then `ERROR  Until the file is fixed, only recordings run (1, 2 and C),
without the settings above; the other choices wait.` A recording then runs with every setting that
passes and leaves out each one that does not, with a warning that names the value used in its place
(see [Settings](#settings)); the other choices start nothing, say what to fix, and the menu comes
back. Once the file is fixed, the next choice runs. The header also names a saved class that cannot
be used (`WARNING  The saved class "Broken" cannot be used: ...`), and says when your file holds a
setting whose default has changed since (`1 setting holds an older default; settings list shows it`).
While your own settings file is missing, an `ERROR` line says so and how to put it back.

If the recordings folder cannot be used, a `WARNING` line right under its name says why:
`Recording:OutputFolder` uses a `%VARIABLE%` that is not set, its drive is not there (an unplugged
USB drive, say), or a file stands where the folder should be. If choice 3 then finds no recordings,
it says `No recordings found.` with the same reason. Fix the setting before you record.

### What is recorded

By default ClassScribe records what your computer plays, and not the microphone; menu choice 7, the
settings or the command line add your microphone, or record it alone (see
[Your microphone](#your-microphone)). For the class it records whichever
output device the class is playing through. It starts on the device with sound, or else on
Windows' default device, and looks at every output device once a second. When the recorded device
has been quiet for two seconds while sound has been playing elsewhere for a second, it moves there
and continues in a new segment file; the segments are joined before upload. A short notification
sound on another device does not move it.

Menu choice 6 lists the connected output devices and the apps with sound open. ClassScribe itself is
never among the apps (a recording's own capture used to show up there), so `Recording:App` cannot
pick it either. What you pick holds until ClassScribe is closed, whatever the settings say:

- **A device**: that device and no other, even when sound plays elsewhere. If it is not connected
  when the recording starts, ClassScribe waits for it.
- **An app**, such as `chrome` or `msedge`: that app's sound alone, whatever device it plays
  through, so a video in another browser or a notification stays out. Windows cannot tell two tabs
  of the same browser apart. If the app has no sound open yet, ClassScribe waits until it plays.
- **A**: back to whichever device the class plays on, which also holds until ClassScribe is closed.

Without a pick from choice 6, `Recording:Device` and `Recording:App` in `appsettings.json` decide,
and they are read again before each choice. To keep a pick for good, set the setting the menu names
after it, or leave both empty for automatic.

The sound is saved in mono. On a surround device the centre channel, where films and players often
put the voice, and the surround channels are mixed in with the front ones.

### Your microphone

ClassScribe can record your microphone beside the class, or your microphone alone for a class held
in a room:

- **With the class**: your microphone is a second track beside the class, on the same clock, so the
  two line up in the files themselves. At upload the two are mixed into one track, and the transcript
  carries both without saying who spoke, at the usual price. A microphone track that never rose above
  a very low level (100 on a scale of 32,767) is left out, the upload is then exactly the class's, and
  the log says `Your microphone stayed silent; only the class is sent.` The microphone's files stay on
  disk either way (`.mic.wav`, see [Where things go](#where-things-go)).
- **In person**: your microphone alone, as the recording's main track. There is no class app, so
  nothing is followed: the microphone is recorded whenever it is on, and V switches it off and on.
- **None**: the class alone, as before.

Choose with menu choice 7: W (with the class), I (in person) or N (none); then a numbered list of the
microphones Windows can record from, each with notes (`built in`, `default`, `USB`, `raw`, and for a
Bluetooth one `never chosen unless picked here; call-quality sound`), for a number, or Enter for
Windows' default microphone (or the one `Recording:Microphone` names); then, with the class, which app
the microphone follows: `Follow the class app: Enter keeps "chrome", type another app's name, A follows
any app that holds the microphone, N follows none (V only).` Listing the microphones opens nothing.
The pick holds until ClassScribe is closed, whatever the settings say, and the menu prints the settings
that would keep it: `Recording:Mode` (`Class`, `ClassAndMicrophone` or `Microphone`),
`Recording:Microphone` and `Recording:MicrophoneFollows`. On the command line, `--mic` beside
`--device` or `--app` adds your microphone, `--in-person` records it alone, and `--mic-follows` names
the app (see [Without the menu](#without-the-menu)).

**Which microphone.** With no name, ClassScribe takes Windows' default microphone, unless that is a
Bluetooth microphone (using it switches earbuds to call quality, for the class too) or an input that
records what the computer plays (Stereo Mix, which would record the class twice); then the laptop's
own microphone array; then another microphone that is neither. A Bluetooth microphone is recorded
only when you name it, with a warning; Stereo Mix never. The choice is kept for the whole recording,
so earbuds that connect mid-class and become Windows' default are not followed. If the chosen
microphone is unplugged, another stands in after 10 seconds (`The microphone "..." has been gone for
10 s; recording "..." instead until it returns`), and the recording moves back when it is plugged in
again, each time in a new file. With the class, when the class app is found listening on another
microphone than the one ClassScribe picked, ClassScribe switches to the app's microphone, so that your
voice is recorded as the class hears it: `Recording the microphone "OBSBOT ...", the microphone the
class app uses, instead of "Microphone Array ..."`. The microphones are looked at every 10 seconds, and
at once when Windows reports a change among them; a look opens nothing. ClassScribe asks Windows for
the microphone's audio without Windows' sound processing (RAW) while `Recording:MicrophoneProcessing`
is `Raw`, the default; when the microphone does not offer it, or Windows refuses it, the processed
audio is used and the log says why: `Microphone: "Microphone Array (...)", with Windows' sound
processing (RAW was not available: 0x88890027)`. `Microphone: "...", without Windows' sound processing
(RAW)` confirms RAW.

**The start lines.** Before the first audio, with the class: `Your microphone is recorded too, and
uploaded to ElevenLabs with the class.`, then the line that says how your mute in the class app is
followed (below), then `Following: chrome (Recording:MicrophoneFollows)` (or `Following: chrome, the
app being recorded`, `Following: chrome (chosen for this run)`, or `Following: any app that holds the
microphone (Recording:MicrophoneFollows auto)`), then the microphone line with why that microphone was
chosen, such as `Microphone: "Microphone Array (...)" (Windows' default microphone), without Windows'
sound processing (RAW)`. Then the warnings, which are also a Notice in the title (`3 warnings at the
start`) for five minutes:

- `Lenovo Vantage's microphone noise cancelling is on (mode 1, probably "Multiple Voices"); it can cut
  out a lecturer across the room. Set Vantage > Device Settings > Sound > Input to Off.` The labels
  are inferred, so the number is shown too. While the laptop's own array is recorded, the mode is read
  again every 30 seconds, because Vantage's scenes can switch it mid-class: `... has switched on (mode
  4, probably "Only My Voice") ...`, `... has changed to mode ...`, and `... is now off.` when you turn
  it off.
- `Windows lowers other sounds by 80% while a microphone is open, which can make the class quieter, in
  the recording too. Set Sound > More sound settings > Communications to Do nothing.` (with the class
  only; `by 50%` or `Windows mutes other sounds` for the other settings).
- `The microphone "Headset (...)" is Bluetooth: using it switches the earbuds to call quality, for the
  class too. The laptop's microphone array avoids that.`
- `The class plays on "Speakers (Realtek(R) Audio)", so the microphone records it a second time (an
  echo). Use headphones.` Use headphones whenever your microphone records with the class. The check is
  made again whenever the class moves to another device; in app mode the app's device is looked at
  every 30 seconds.

In person the lines are `Your microphone records the room, and the recording is uploaded to
ElevenLabs. Ask before recording others.`, the microphone line, the Vantage and Bluetooth warnings when
they apply, and `Plug in and keep the lid open; the screen stays on while on battery. For a break
press P; V switches the microphone off but keeps the clock running, and that silence is uploaded.`

**Your mute in the class app.** With the class, ClassScribe records your voice only while the class
app is listening. Four times a second it looks, in Windows' own lists, whether the app has the
recorded microphone open and taking sound from it, and whether the microphone is muted in Windows;
every ten seconds it also looks at the other microphones, to find the app listening on another one.
While the app is not listening, nothing from the microphone goes into the recording: that stretch is
silence in the file, so positions and marks do not move. Each packet from the microphone is judged by
the looks on both sides of it, so nothing from after a mute and nothing from before an unmute gets
through, at the price of up to a quarter of a second at each edge, which is nearly always the silence
around a click. The watch only reads Windows' lists; it never opens the microphone itself.

Which app is followed: in app mode (`--app chrome`, `Recording:App` or menu choice 6) the app being
recorded; otherwise `Recording:MicrophoneFollows`, `auto` unless you change it (`auto` follows any
app that has the microphone open; `none` records whenever V is on, as in person). Menu choice 7 and
`--mic-follows` can name another app for a run. Chrome takes the microphone in a helper process of its
own, so the app is matched by its process name, as `--app` matches it.

One thing Windows cannot see: many apps keep the microphone open while you are muted and only stop
sending your voice. Chrome pages usually do, and so do Zoom and Teams. Windows then shows the app as
listening the whole time, and ClassScribe cannot tell your mute from your unmute, so muting yourself
in the class page may not keep your voice out of the recording. `Recording:ClassAppMute` says which
kind your class app is:

- `Unknown`, the default until your class page has been measured: your microphone starts switched
  off, and V switches it on. The start says so: `ClassScribe has not been told whether chrome lets go
  of the microphone when you mute (most class apps and pages keep it open), so it cannot follow your
  mute yet: your voice is recorded only after you press V, and only while chrome holds the microphone.
  Muting the microphone in Windows (the microphone key) stops it too. Run the microphone check (menu
  choice 8, then W) during a class, or set Recording:ClassAppMute.` When ClassScribe sees the app let go of
  the microphone and take it again, it tells you once: `chrome let go of the microphone at 19:12:03
  and took it again at 19:12:40. If that was you muting and unmuting in the class page, your mute is
  visible to Windows: set Recording:ClassAppMute to Visible and ClassScribe will follow it by itself.`
  The summary at the end counts those: `chrome let go of the microphone 7 times`.
- `Visible`: the app lets go of the microphone when you mute, so ClassScribe follows your mute by
  itself; the microphone starts on, and V switches it off by hand. The start says `Your voice is
  recorded while chrome is listening: when you mute in chrome, ClassScribe stops too. V switches it
  off by hand.`
- `Invisible`: the app keeps the microphone open while you are muted. The microphone starts off, V
  switches it on, and your voice is then recorded while the app holds the microphone. The summary
  still counts the times the app let go of the microphone, `although Recording:ClassAppMute says
  Invisible`, so a wrong setting is noticed.

The setting describes your class app, not a run, so it has no command-line option. To measure your
class page once, run the microphone check during a class and set the setting from its log (see
[Checking what your class page does with your microphone](#checking-what-your-class-page-does-with-your-microphone)).

Whatever the setting, two hard stops hold while the microphone is on: nothing is written while the
followed app has not got the microphone open, and nothing while the microphone is muted in Windows.
That Windows mute is the one mute to use: the laptop's microphone key (the F key with the microphone
picture), or Sound settings > Input > Mute. It silences the class app too, ClassScribe reads it at
every look, and so follows it exactly. It mutes one microphone, so the app and ClassScribe must use
the same one, which is why ClassScribe switches to the app's microphone. If you set `Visible` and still
find your own voice in a recording from a stretch when you were muted, the app does not let go of the
microphone after all: set `Invisible` and use the microphone key or V.

When Windows will not describe an app's session on the microphone, as happened with the Zoom app in a
meeting, ClassScribe reads who the session belongs to first. Another app's session that cannot be read
changes nothing. The followed app's own session, when its state cannot be read, is judged by Windows'
record of which apps use the microphone (the one behind Settings > Privacy & security > Microphone):
while the record says the app is using the microphone, it counts as listening, and the log says so
once (`Windows would not give the state of zoom's session on "..." (...); until it does, Windows'
microphone usage record says whether zoom is listening`); when the record says it closed the
microphone, it counts as not listening. With `auto`, the default, which follows any app, every app's
session counts as the followed app's, and each one whose state cannot be read is judged by the
record's entry for its own program, found by its name (`Zoom` for the Zoom app), so a class in the
Zoom app needs no `--mic-follows zoom` for this. If the record says nothing of the app, or the session
cannot even be told apart, nothing is written, and after 10 seconds the title shows `CANNOT READ THE
MICROPHONE SESSIONS`, naming the session and the Windows call that failed.

**V** switches your microphone off and on by hand, in any mode. Off releases the microphone, so
Windows' microphone icon goes out unless the class app holds it, and the recording keeps its place:
the time off is silence in the microphone's file. On opens it again within a moment, after a fresh
look at the class app, so that the first packets are judged by it. The log says `Switching the
microphone off (V); its capture is released and the track keeps its place on the clock` and
`Microphone off; V switches it on again`, then `Microphone on`, with what it still waits for when it
does (`Microphone on; waiting for chrome to open the microphone`). Where the app's mute cannot be
seen, the microphone starts off: `The microphone starts switched off (V switches it on)` as the
recording starts, then among the start lines `Microphone: "..." (Windows' default microphone),
switched off until you press V; RAW will be asked for`, and the title says `MIC OFF` until you press
it.

**What you see while it records.** The title shows `REC 01:12:40 Kalam` while your voice is being
recorded, `REC 01:12:40 MIC OFF Kalam` while the microphone is switched off, `MIC MUTED` while the
class app is not listening or Windows mutes the microphone, and `MIC WAITING` while the app has not
opened the microphone, uses another one, or its sessions cannot be read; the taskbar colour does not
change for these. The status line, every minute, ends with the microphone's state: `; microphone:
following chrome, recording`, `..., muted (chrome is not listening)`, `..., muted in Windows`, `...,
waiting for chrome to open the microphone`, `; microphone: chrome uses "OBSBOT ..."; ClassScribe
records "Microphone Array ..."`, `; microphone: cannot read chrome's microphone session: ...`,
`; microphone: off (V records your voice while chrome holds the microphone)` or `off (V switches it
on)`, `; microphone: waiting for the microphone "OBSBOT"`, or, with no app followed, `; microphone:
recording "Microphone Array ..."`. The log gets a line when a state has held for a second:
`Microphone: chrome is listening; your voice is being recorded from 00:12:03` and `Microphone: chrome
is not listening; your voice is not recorded from 00:12:40`; shorter changes are counted, not logged,
and the summary says `chrome's microphone session changed 14 times for under a second` when there
were any. Nothing about mutes, V or the Windows mute goes into the marks file or the transcript.

At the end, after `Recorded 01:58:10 of audio`, comes the microphone's sentence: `Microphone: heard
for 00:12:40 in 9 stretches while chrome listened, muted for 01:40:10, off (V) for 00:05:00, muted in
Windows for 00:00:00`, with the counts above when there are any; with no app followed, `Microphone: on
for 01:50:00, off (V) for 00:05:00`.

**In person.** Plug the laptop in and keep the lid open; the screen stays on while on battery (dim it
by hand). Sit near the front, or put a USB or lapel microphone near the lecturer and name it
(`Recording:Microphone`, `--in-person "OBSBOT"`, or menu choice 7). For a break press P: V's silence
is uploaded and billed, a pause is not. ClassScribe warns after a minute without sound (`No sound from
the microphone "..." for 60 seconds. Check that it is not muted in Windows (the microphone key) and
that Windows lets desktop apps use it (Settings > Privacy & security > Microphone)`), and when the
microphone hands over nothing but exact zeros for 2 minutes it says so, with `ONLY SILENCE FROM THE
MICROPHONE` in red in the title (`Only silence from the microphone "..." for 2 minutes: it is muted in
Windows (the microphone key, or Sound settings > Input)` when Windows says it is muted). A webcam's
microphone, such as the OBSBOT's, has rules of its own for its zeros (see [A webcam's
microphone](#a-webcams-microphone-the-obsbot)). Everyone in
the room is recorded and uploaded to ElevenLabs, so ask before recording others, and keep to what your
teacher and your institution allow.

### While recording

Start recording before the class and leave the window in the background. Play the class as usual,
and keep the sound on: on some sound drivers, muting Windows also silences the recording.
These keys work in the ClassScribe window:

| Key | What it does |
| --- | --- |
| Q, twice | Stops. The first Q only shows `Press Q again within 5 seconds to stop`, so a Q typed into this window by mistake changes nothing. With choice 2 the transcript follows. |
| Ctrl+C, twice | Stops, like Q: the first press only shows `Press Ctrl+C again within 5 seconds to stop`. After the stop, one Ctrl+C cancels the preparation and the upload that follow (within 2 seconds of a stop with Ctrl+C it still counts as part of the stop). The recording is kept either way. |
| P | Pauses, for a break. Nothing is recorded until you press P again. |
| M | Marks the moment. The mark shows in the transcript at that point, as a heading `## [00:42:13] Mark 1`. |
| V | Switches your microphone off and on by hand (see [Your microphone](#your-microphone)). With the class, your voice is normally recorded only while the class app is listening; V off keeps it out whatever the app does, and V on is needed first where the app's mute cannot be seen. In a recording of the class alone it only says `There is no microphone in this recording`. |
| Esc | Leaves the view of a recording that runs in the background, without stopping or pausing anything: from the menu, the menu comes back; from a command, ClassScribe exits 0 and says `The recording carries on in the background. Start ClassScribe to see it.` In a recording that runs in the window, Esc only says that Q twice stops it. |

The first lines of a recording say where it goes (`Recording "Kalam" to D:\Classes\Kalam\2026-10-15`, or
`Continuing "Kalam" as its next part (Kalam_2026-10-15_23-00-02Z, 00:47:10 recorded so far), in ...`),
what is recorded (`Recording from the app "chrome"`, `the device the class plays on`, `the device
the class plays on, and your microphone "OBSBOT"`, or in person `your microphone`), the keys (V among
them with your microphone) and what closing a window does, the reminder about the lid, and, with
`--minutes`, `This recording stops by itself at 20:30 (--minutes 90); Q twice stops it sooner`. With
your microphone, the lines about it follow at once (see [Your microphone](#your-microphone)). At the
end they say how much was recorded: `Recorded 01:58:10 of audio`, or for a continued recording
`Recorded 00:30:00 in this part, 01:17:10 in all`. With your microphone, a line comes first that sums
the clock up: `Recorded 01:58:10 in this run: 00:06:12 paused, 00:00:21 left out while the laptop
slept or ClassScribe was held up, 3 parts and 3 microphone files, 4 corrections for drift`, and the
microphone's own sentence follows the length.

The window's title shows the state at a glance, such as `REC 01:12:40 Kalam | ClassScribe`,
`PAUSED ...`, `! NOT RECORDING (waiting for "chrome") Kalam`, `REC 01:12:40 Kalam (no sound 2 min)`,
`REC 01:12:40 MIC MUTED Kalam` with your microphone (see [Your microphone](#your-microphone)),
`UPLOADING 40% Kalam`, `TRANSCRIBING Kalam`, then `DONE Kalam`, `! FAILED Kalam` or
`CANCELLED Kalam`, with `(stops 20:30)` while `--minutes` or the safety stop will end it, and
`(in this window)` when the recording runs in that window. The taskbar button is green while
recording or uploading, yellow when paused or when something is worth a look, red when something
needs attention or the run failed, and a bar runs back and forth while ClassScribe waits for
ElevenLabs. It flashes once for each new problem that needs attention now, until the window comes
forward; something only worth a look turns it yellow without a flash. Windows Terminal shows the
colour on its tab and its taskbar button, also when ClassScribe was started from a shortcut or a
double-click; the classic console shows it on its own button; the VS Code terminal shows the title
only if its `terminal.integrated.tabs.title` setting includes `${sequence}`, gets no colour, but
its window flashes. Nothing is shown when the output goes to a file. ClassScribe never makes a
sound, and when it exits the window gets its own title back and the colour is cleared.

Those seconds, like the status line and the silence warning, count the time that has really passed,
so a change of the Windows clock neither shortens nor stretches them.

Marks are kept in the recording's `.marks.txt` file, one per line. You can type a label after a
mark's time, such as `00:42:13.250 Definition of jawhar`; the next transcript of that recording then
shows `## [00:42:13] Mark 1: Definition of jawhar`, and you can make that transcript for free (see
[A transcript again, for free](#a-transcript-again-for-free)). Type labels after the class, or
reload the file in your editor before you save it: an editor that saves an older copy would drop
the marks set since. The file also holds notes ClassScribe writes itself, with `~` after the time,
such as `00:47:10.000 ~ Resumed at 23:51 UTC, about 4 minutes not recorded` or
`00:42:10.000 ~ Not recorded: the laptop slept from 23:42:10 to 23:50:31 UTC`. A note shows in
the transcript as a heading in its own words, without a number, and never changes the marks'
numbers; you can type one by hand the same way, with `~ ` after the time. A line that starts with
neither a time nor a note is skipped with a warning, a time written in another script's digits
included.

The status line, every minute, says how much has been captured, and ends with
`; stops by itself at 20:30` while `--minutes` or the safety stop will end the recording. While
paused, a reminder comes every 5 minutes (`Still paused, for 5 minutes now; P records again`), and
from 10 minutes the title says `PAUSED 10 MIN` in red; when sound has kept arriving for 30 seconds
while paused, ClassScribe says `The class seems to be playing while paused; P records again`. At the
start it also reads the free space of the recording's drive and warns when less than 3 hours of
recording fit there; every 5 minutes after that it warns when less than 30 minutes fit
(`Only about 20 minutes of recording still fit on D:\ (2.1 GB free). Free some space there now, or
the recording will stop being saved`), with `DISK NEARLY FULL` in the title. Once a second
ClassScribe also checks that audio keeps arriving. An app's sound arrives all the time, silence included, and a
device's whenever Windows' meter shows sound on it, so a quiet class sets off nothing. When nothing
at all has arrived from an app for 5 seconds, or from a device whose meter has shown sound for 10,
ClassScribe says so and reopens the capture in a new segment file, at most once every 30 seconds:
`No audio has arrived from "chrome" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it`.
For a device the line says `although Windows shows sound playing there` instead. An app is watched
during a pause as well; the line then says the capture is reopened so that the recording can go on
when you press P again.

Every 10 seconds, from the start, ClassScribe also looks whether the class app is taking sound from
a Bluetooth microphone, a headset's or earbuds'. Used as a microphone, a Bluetooth headset or earbuds
switch to call mode (you hear it: sound in one ear only, or at call quality), and Bluetooth then
carries the class's sound straight to them, past the loopback Windows offers, so the recording is
silent while it lasts. The warning names the app, the microphone and the fix:
`chrome is taking sound from the Bluetooth microphone "Headset (2- Pixel Buds Pro 2)": that puts the
headset or earbuds into call mode, and the class's sound may be going to them in a way Windows cannot
record, so this recording may be silent. In chrome, set the microphone to the laptop's built-in
microphone ("Microphone Array (...)"), or to another that is not Bluetooth`, with `chrome on a
Bluetooth microphone` in the title until the app lets go (`chrome has let go of the Bluetooth
microphone ...`). In Chrome, the quickest fix is the microphone icon at the right end of the address
bar, shown while the page uses the microphone: choose the laptop's `Microphone Array` there, or under
Settings > Privacy and security > Site settings > Microphone; the class page's own microphone
setting, where it has one, does the same. The earbuds then leave call mode and the recording picks
the class up again. With a device recorded and no app followed, any app counts, since any could be the class's;
with an app recorded, or your microphone following one, that app alone. In person nothing is
watched. Nothing is opened: the look reads Windows' own lists, as the `devices` command does.

### The background recorder

With the published copy, a recording started from the menu or the command line runs in a
ClassScribe process of its own, without a window: the same `ClassScribe.exe`, started as
`ClassScribe.exe --recorder <run id>`. The window hands it the settings it has just read and
checked, so the recorder never reads `appsettings.json` itself, and an edit made during a class
counts from the next recording. The recorder takes the API key from the environment it inherited
from the window, and from nowhere else; nothing on disk holds the key. The window shows the
recording through a link to the recorder and passes the keys on to it, so closing the window, a
crash in it, or a key typed into it by mistake can no longer end a class. The window says
`Recording in the background: closing this window does not stop it. Esc leaves this view; start
ClassScribe again to see it.`

To see the recording again, start ClassScribe: the menu shows it at once (what it has logged so far,
then what happens), and A shows it again after Esc. `record` with the same name shows it too
(`"Kalam" is being recorded already, since 19:00 (00:42:13); showing it.`), and so does the
hidden command `ClassScribe attach`, which the window that comes back by itself runs as
`ClassScribe attach --alert <run id>`. Up to four windows can show one recording, and the keys
from any of them reach it. If a window loses its link to the recorder it tries for 10 seconds to
make it again, then says `Lost the connection to the recording; it carries on. Start ClassScribe
again to see it.`; if the recorder sends nothing for 10 seconds while its process runs, the window
says `The recorder is not answering (process 1234, started 19:00). Its audio so far is on disk. W
keeps waiting, S stops it.`, and S stops the recording through a signal that does not need the
link.

Once Google Drive is set up (and `Drive:Enabled` is on, as it is unless you change it), the recorder
lives on after the window has been shown the end, for as long as the upload of its class and of what
else waits takes, about 10 minutes for a class of two hours on a line that uploads 10 Mbit/s (see
[Google Drive](#google-drive)). Its lines go into the recording's `.log` and `recorder.log`, closing
the window does not stop it, and the next class may start meanwhile: the upload then waits between
pieces until that class has stopped recording.

Only one recording runs at a time, through a lock that is held only while it captures, so the next
class can start while the last one is still being uploaded. The menu's header then says
`Being transcribed in the background: "Fiqh" (uploading 40%).`, and that recording cannot be sent
again until it is done (`... is being transcribed in the background; its transcript will appear
in ...`). A second recording is refused: `Only one recording at a time: "Fiqh" has been recording
since 19:00 (00:42:13). Start ClassScribe to see it, or stop it first.` (in the menu, `A shows it.`),
with exit code 1 on the command line. A YouTube video being recorded as it plays is the one
exception: a class started meanwhile stops it and records (see [A class comes first](#a-class-comes-first)).

At the end, the window that started the recording opens the folder and the transcript as the
settings say; when it is gone, the last window you opened to see the recording does. With no window
showing it, the recorder opens them itself (`No ClassScribe window was showing this recording at its
end, so the recorder opens the results itself`), so the transcript appearing is the sign that the
work is done. `--no-open` opens nothing anywhere. A window that came back by itself (below) never
opens anything, and the next start of ClassScribe then shows the outcome once, with O to open the
transcript (see [If a recording was interrupted](#if-a-recording-was-interrupted)).

The recording runs in the window instead, as it did before, and the window says so first, when:

- `Recording:SeparateProcess` is `false`: `Recording in this window (Recording:SeparateProcess is
  false): closing it ends the recording; the audio so far is kept.`
- ClassScribe was started under `dotnet run`, or in another way that ends every program it started
  when it ends: `ClassScribe was started in a way that ends every program it starts when it ends, as
  dotnet run does. So this recording runs in this window: closing it ends the recording, and the
  audio so far is kept. For classes, start ClassScribe.exe itself (README, Set up once).`
- `%LOCALAPPDATA%\ClassScribe` cannot be written: the reason, then `One recording at a time and
  the report after a crash are not available this time.`
- The recorder would not start twice in a row: the reason, `Starting the recorder once more.` after
  the first failure, then `Recording in this window instead. ...` and `If Bitdefender showed a
  notice, add the exception described in the README.`

A recording in the window adds `(in this window)` to the title, and everything else works the same:
one recording at a time, the report after a crash (which then says `It ended when its window was
closed`), the keys, the laptop kept awake.

If you close the last window showing a recording while it records, the recording goes on, and 3
seconds later a minimized ClassScribe window comes back on the taskbar without taking the focus,
flashing once, so that the recording is not forgotten (`Recording:ReopenWindow`). It comes back the
same way, at once, when something needs attention (nothing recorded for 15 seconds, say) or when the
run fails, and once something only worth a look, such as no sound, has lasted 2 minutes (with the
silence warning at 60 seconds, that is about 3 minutes of silence). One such window at a time, and
at most one every 2 minutes. Its first lines say why it opened, such as `The window was closed; the
recording goes on. A ClassScribe window comes back, minimized, so that the recording is not
forgotten; its keys do nothing until Enter is pressed in it`. That window ignores every key until
you press Enter in it (`ClassScribe opened this window by itself. Keys do nothing here until you
press Enter.`, and `Press Enter to control the recording from this window.` when a key is pressed),
so text typed into it by mistake cannot stop or pause the class; after Enter, Q, P, M, Esc and
Ctrl+C work there as in any window. If you close it again within 2 minutes, no window comes back
for a mere close again in that recording (`The ClassScribe window that came back was closed within
2 minutes, so a closed window brings none back again in this run; something that needs attention
still does`). Leaving the view with Esc brings no window back. With `Recording:ReopenWindow` set to
`OnProblems` only a problem brings a window back; with `Never`, none does, and the next start of
ClassScribe reports what happened.

### Keeping the laptop awake

From the start of a recording until its transcript is saved (with record only, until its files are
closed; for a lesson with the camera, until its video is made too), while a recording or a file is
transcribed, while `finish-video` makes a lesson's video, while ClassScribe uploads to Google Drive, and
for the whole of a batch of YouTube videos, from its plan to its summary, it asks Windows to keep
the laptop awake. `powercfg /requests`, in a terminal run as administrator, lists the request as
`ClassScribe is recording a class`, `ClassScribe is transcribing a recording`,
`ClassScribe is making a lesson's video`,
`ClassScribe is uploading to Google Drive` or `ClassScribe is working through YouTube videos`; each
video's recording holds its own request as a class does. Windows ends the request when ClassScribe ends, however
it ends. After a class, the background recorder lives on to upload it and holds the last of these;
the next class can start at once all the same. The screen is kept on as `Recording:KeepScreenOn`
says:

- `OnBattery`, the default: only while the laptop runs on battery. There a laptop with Modern
  Standby goes into standby once its screen turns off, whatever a program asks, so the screen is
  what keeps it recording. On mains, keeping the laptop awake is enough, and the screen may turn
  off. ClassScribe looks again every 30 seconds, so the screen follows when you unplug the laptop
  or plug it in.
- `Always`: on battery and on mains.
- `Never`: not at all. On battery the laptop may then go into standby a few minutes after your last
  key press, and nothing is recorded until it wakes.

Dim the screen by hand if you like. If Windows refuses a request, ClassScribe says so and carries
on all the same; the message advises keeping the laptop plugged in, with sleep set to Never in
Settings > System > Power & battery.

Closing the lid, the power button and Start > Sleep still put the laptop to sleep, which no program
can prevent; a recording starts with the reminder `Keep the lid open and the laptop plugged in;
closing the lid still puts it to sleep`. If the laptop slept during a recording, or Windows paused
ClassScribe, for 3 seconds or more, the log says when, in local time, such as
`The laptop slept from 19:42:10 to 19:50:31; nothing was recorded then` or
`ClassScribe was held up from 19:42:10 to 19:42:28 (Windows paused it); nothing was recorded then`.
The silence warning and the check that audio keeps arriving count from the end of such a gap, so the
time asleep is not taken for a fault of the device.

### Where things go

Recordings and transcripts go to the folder set as `Recording:OutputFolder` in your settings, which
is `%USERPROFILE%\ClassScribe` unless you change it (menu S, then 1), or to a saved class's own
folder, or to the one `--output` names for a run. Each name is a course and gets a folder;
each class of it gets a folder inside that one, named after the local date the class started on;
and every file of a recording carries the name and the time the recording started, in UTC (the `Z`):

```
%USERPROFILE%\ClassScribe\
    Mantiq\                                          the course: one folder per name
        2026-10-17\                                  one class: the local date it started on
            Mantiq_2026-10-17_14-04-42Z.wav          the audio as captured (kept)
            Mantiq_2026-10-17_14-04-42Z_part2.wav    only after a device switch, a failure or 1500 MB
            Mantiq_2026-10-17_14-04-42Z.mic.wav      your microphone, when recorded with the class (kept)
            Mantiq_2026-10-17_14-04-42Z_part2.mic.wav  the microphone's next file, after a microphone change, a failure or 1500 MB
            Mantiq_2026-10-17_14-04-42Z.marks.txt    the marks set with M, and ClassScribe's own notes, if any
            Mantiq_2026-10-17_14-04-42Z.log          everything the recording's runs logged
            Mantiq_2026-10-17_14-04-42Z.glossary.json  the glossary decided as it started, and what each transcript sent
            Mantiq_2026-10-17_14-04-42Z.md           the transcript
            Mantiq_2026-10-17_14-04-42Z.json         the full ElevenLabs response, with word timings
            Mantiq_2026-10-17_14-04-42Z.upload.wav   the 16 kHz copy that is uploaded, while it is needed
            Mantiq_2026-10-17_14-04-42Z.unread.json  only when a paid answer did not become a transcript
            Mantiq_2026-10-17_14-04-42Z.cam.mp4      a lesson's camera picture, when recorded with the camera (never uploaded)
            Mantiq_2026-10-17_14-04-42Z.cam@00-41-07.250.mp4  a later piece of it, beginning 41:07.25 into the recording
            Mantiq_2026-10-17_14-04-42Z.mp4          the lesson's finished video, the picture with its sound (to Drive only with Drive:LessonVideos)
            Mantiq_2026-10-17_14-04-42Z.mp4.partial  the lesson's video while it is being made (never uploaded)
            Class_1_slides.pdf                       your own files: left alone, never uploaded
        2026-10-17 (2)\                              a second class of the name on the same day
            Mantiq_2026-10-17_22-00-05Z.wav
        Slides\                                      your own folders are passed over
    Kalam\                                           a course recorded before the date folders
        Kalam_2026-10-14_19-30-00Z.wav               flat in the folder of its name; still read everywhere
    Lecture 5\                                       a transcribed file; the file itself stays where it is
        2026-10-15\                                  the day it was transcribed
            Lecture 5_2026-10-15_13-00-00Z.md
            Lecture 5_2026-10-15_13-00-00Z.json
    mic-check\                                       the microphone check's logs (menu choice 8, mic-check)
        mic-check_2026-10-17_19-00-12.log            one per run, named by the local start time
```

The date folder is the local date, while the files keep the UTC time: an evening class in US
Eastern time that starts at 21:30 on 17 October goes to `2026-10-17`, although its files say
`2026-10-18_01-30-00Z`. A second, separate class of the same name that day goes to
`2026-10-17 (2)`, a third to `(3)`, and so on. A date folder counts as taken only when it holds a
class: a recording's audio, a camera's piece, a transcript or ElevenLabs' answer, or a recording whose
audio is on Google Drive alone. One that is empty, holds only the `.log`, `.glossary.json` and marks of a start that recorded
nothing, or holds only your own files (the class's PDFs and slides, say), is used as it is, and
those files stay where they are. A class that runs past midnight stays
in the folder it started in, and a recording continued as its next part (C, `--continue`, or after
a lost recorder) goes on in its own folder, whatever the date is now. A file transcribed on its
own gets the same layout: a folder named after the file, and in it the date folder of the day it
was transcribed. The same layout is used under a folder given with `--output`. A batch of YouTube
videos is an exception only in one way: all its videos share one date folder, the one its first
video takes, so the day's class after it goes to `(2)` however many videos there were (see
[Where a video's files go](#where-a-videos-files-go)).

Recordings made before the date folders, flat in the folder of their name
(`Kalam\Kalam_2026-10-14_19-30-00Z.wav`), are still listed, continued, transcribed and rebuilt
where they are: ClassScribe never moves anything. In a course's folder ClassScribe looks only into folders
named as a date (`2026-10-17` or `2026-10-17 (2)`), so a folder of your own there, such as
`Slides`, is passed over; files that are not a recording's are ignored by every list and never
touched.

On Google Drive, once you are signed in, the same tree is under `My Drive > ClassScribe`
(`Drive:FolderName`): a class in a course and date folder goes to `ClassScribe > Mantiq >
2026-10-17`, a date folder's own name, `(2)` included, and a recording still flat in the folder of its
name to `ClassScribe > Kalam`. The rule comes from the recording's own folder, so it holds under any
recordings folder and under `--output`. Only a recording's own files go up, never your PDFs or slides
beside it, and never the `.upload.wav` (see [Google Drive](#google-drive)). Deleting files on the
laptop, by hand or with the free-space option, never changes anything on Drive.

In person your microphone is the recording itself, so its files carry the plain names (`.wav`,
`_part2.wav`). With the class, the upload copy is the class and your microphone mixed into one track
(see [Your microphone](#your-microphone)).

The transcript is a Markdown file: the name as its title, when it was recorded, then paragraphs
that start with their `[hh:mm:ss]` position, and the marks as headings, so they show in the outline
of any Markdown viewer (VS Code, Obsidian). It is plain text underneath, so Notepad opens it too.
ElevenLabs itself answers with JSON (the text and every word's timing); that is kept as the `.json`.

When a transcript is ready, its folder and the transcript open by themselves. A transcript is never
overwritten: transcribing a recording again, or making its transcript again for free, adds
`Kalam_2026-10-14_19-30-00Z (2).md`, then `(3).md`, even when two ClassScribe windows save at once.

The `.upload.wav` is the smaller copy (16 kHz mono) that is sent to ElevenLabs. It is removed when
the transcript is saved, and also after a failed or cancelled upload, unless
`Recording:KeepUploadFile` is on; if another program holds it open, ClassScribe only warns, and you
can delete it later. A copy left behind by a ClassScribe that was closed during the upload is
replaced when that recording is transcribed again.

ElevenLabs charges for every transcript it sends back. When such an answer arrives but no
transcript can be made from it, ClassScribe says so and keeps the answer. One that cannot be read,
or cannot be saved as the `.json`, is kept as `<name>_<time>Z.unread.json` beside the recording (a
second one gets `(2)` before `.unread.json`), or under the same name in the recordings folder when
the recording's folder will not take it. When the `.json` was saved but the transcript could not be
made from it, that `.json`, with no `.md` beside it, is the kept answer. Either way its `"text"` is
the transcript's text, so look there before transcribing again, which is charged again. Menu
choice 3 shows a recording that has such an answer, and no transcript, as `answer kept`. If the
answer cannot be kept anywhere, ClassScribe says that too. The `Transcript received` line in the
log, and that message, give the answer's ElevenLabs transcription ID: ElevenLabs keeps the
transcript under it, and
[its API can return it again](https://elevenlabs.io/docs/api-reference/speech-to-text/get).

A name is made safe for Windows: characters Windows does not allow in a file name are left out, and
a name Windows keeps for a device, such as `NUL` or `COM1`, gets an underscore (`NUL_`). A file with
nothing usable in its name, such as `.mp3`, gets an `Untitled` folder.

The positions are positions in the recording. In a recording of the class alone, while nothing at
all is playing (a paused video, for instance) Windows usually hands over no audio, so a pause can be
shorter in the recording than it was in the room; where a minute or more of that was left out
(`Transcript:ShowGapsLongerThanSeconds`), the transcript says so with a heading such as
`## [00:41:02] About 3 minutes with nothing playing were left out here`. Time paused with P is left
out too, in every mode, and so is a time the laptop slept. With your microphone nothing else is
left out: both tracks follow one clock, with the pauses and the sleeps taken out of it, so a
position is time on that clock, a mark gets that time, and the tracks line up in the files
themselves.

The `.log` beside a recording holds every line its runs logged, from the first line to the
transcript, in the background or in the window alike; a run that continues the recording adds to
it. It is the record of a class after a crash, a closed window or a restart, and it can be read
while ClassScribe writes it.

#### ClassScribe's own folder

The bookkeeping of the background recorder lives in `%LOCALAPPDATA%\ClassScribe`, which never roams
or syncs, and nothing in it holds the API key:

```
%LOCALAPPDATA%\ClassScribe\
    recording.lock                   held open while a recording captures; Windows frees it when the process ends
    settings-home.txt                the full path of your settings file, written when ClassScribe makes it, so
                                     that a file missing later is said rather than silently replaced
    settings.lock                    held open for the moment a change to your settings is written, so that two
                                     ClassScribe windows never write it at once
    recorder.log                     the recorder's lines until the recording's folder is known; recorder.old.log
                                     keeps the generation before, at about 1 MB
    runs\<run id>.json               one file per run: its state, a heartbeat every 10 seconds, and how it ended
    runs\<run id>.request.json       what the window handed the recorder; deleted once the recorder has read it
    drive\folders.json               the ids of ClassScribe's folders in your Google Drive, so each is found at once
    drive\recordings\<base name>.<8 characters>.json
                                     one per recording: each file that went up, checked, and whether it waits
                                     in the queue; an upload stopped half way goes on from it, by an address
                                     Google keeps for a week, which is never logged
    drive\upload.lock                held open while an upload runs, so only one runs at a time
    youtube\youtube.lock             held open while a batch of YouTube videos runs, so only one runs at a time
    youtube\converting.json          the downloaded video being turned into a recording, for the clean-up after a crash
    youtube\staging\<video ID>\      a video's read and download until it is a recording; deleted then, or after 7 days
    youtube\yt-dlp-cache\            yt-dlp's cache: YouTube's player code and the challenges it solved
    video-browser\                   the profile of ClassScribe's own Chrome window for videos: private once you
                                     sign in there (see Your Premium sign-in in the video window)
```

A run id is the start time in UTC and eight random digits, such as `20261015-230002Z-1f3a9c07`. A
run that needs nothing more (seen, continued or forgotten) is deleted 30 days after its last update.
The recorder also runs in this folder, so it holds nothing under OneDrive or in the repo open.
`video-browser` is Chrome's own, made by Chrome the first time a video's window opens: ClassScribe
never reads, lists, copies, uploads or deletes anything in it, and once you sign in to YouTube there
it holds your Google sign-in, so keep it out of anything you copy or share.

Your own things live in `%APPDATA%\ClassScribe`, which roams with your Windows account:

```
%APPDATA%\ClassScribe\
    appsettings.json                 your settings: a full copy of every setting, made at your first change
    appsettings.previous.json        the file as it was before ClassScribe's last change to it
    keyterms-islamic-sciences.txt    a glossary of your own, if you keep one here, named in your settings
                                     (see [The glossary](#the-glossary)); ClassScribe ships none
    google-drive.dat                 the Google Drive sign-in (below)
```

The Google Drive sign-in lives in `%APPDATA%\ClassScribe\google-drive.dat`: the Google client you
gave ClassScribe (its id and secret) and, while you are signed in, the sign-in itself, the account's
email and when you signed in. It is encrypted with Windows' data protection for your Windows account,
so only you, on this laptop, can open it; it is never in `appsettings.json`, a log or a run file. If
Windows cannot open it (another Windows user, another computer, or a password reset by an
administrator), ClassScribe counts that as not signed in and says so: sign in again with the client
file. A sign-out forgets the sign-in and keeps the client, so signing in again needs no file.

### Without the menu

```powershell
ClassScribe record "Kalam" --device auto --output "D:\Classes"          # record, then transcribe
ClassScribe record-only "Kalam" --app chrome                            # record only, into Recording:OutputFolder
ClassScribe record "Kalam" --app chrome --mic --output "D:\Classes"     # the class and your microphone
ClassScribe record "Kalam" --in-person --output "D:\Classes"            # your microphone alone, in person
ClassScribe record --class "Mantiq"                                     # a saved class, as it is saved
ClassScribe class add "Mantiq" --app chrome --mic --mic-follows chrome  # save a class
ClassScribe class list                                                  # the saved classes
ClassScribe settings set Recording:OutputFolder "D:\Classes"            # change a setting
ClassScribe settings list                                               # every setting, its value and its default
ClassScribe transcribe "C:\path\to\recording folder or file"
ClassScribe rebuild "C:\path\to\recording folder or file"
ClassScribe finish-video "C:\path\to\lesson folder or file"             # a lesson's video made again, free
ClassScribe list                                                        # the recordings, newest first
ClassScribe devices                                                     # the devices, apps with sound, microphones and cameras
ClassScribe mic-check                                                   # watch what apps do with the microphone
ClassScribe camera-check                                                # the cameras and what the camera needs here; opens one only after you type Y
ClassScribe drive signin --client "$env:USERPROFILE\Downloads\client_secret_....json"   # the first Google sign-in
ClassScribe drive signin                                                # sign in again, with the client kept
ClassScribe drive status                                                # the account, storage, folder on Drive and what waits
ClassScribe drive signout                                               # end ClassScribe's access; keep the client
ClassScribe drive upload "$env:USERPROFILE\ClassScribe\Mantiq\2026-10-17"   # a class, a course's folder or the recordings folder
ClassScribe drive upload                                                # every recording that waits to go up
ClassScribe drive free-space "$env:USERPROFILE\ClassScribe\Mantiq" --yes   # delete the audio Google Drive has, checked again first
ClassScribe youtube "https://youtu.be/abcdefghijk" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --yes   # record a video as it plays
ClassScribe youtube --list "risala-links.txt" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --download --plan   # the plan of a download
ClassScribe youtube check                                               # yt-dlp and its helpers, their versions
ClassScribe --help                                                      # the commands; also record --help and so on
```

`ClassScribe` here stands for the published `ClassScribe.exe` (see
[Set up once](#set-up-once)), by its full path when it is not in the current folder, as in
`& "$env:LOCALAPPDATA\Programs\ClassScribe\ClassScribe.exe" record ...`. With
`dotnet run --project src/ClassScribe -- record ...` the same lines work, but the recording then
runs in the window, and closing the window ends it.

In short: `record "<name>" SOURCE [--output "<folder>"] [--mic ["<microphone>"] [--mic-follows <app>|auto|none]]
[--camera ["<camera>"] [--resolution <size>]] [--minutes <n>] [--no-open] [--continue | --new]` (`--camera` only
with `--in-person`; refused before anything records only when a camera set-up line refuses the camera), or `record --class "<saved class>" ["<name>"]`
with any of those options over the class's own, `record-only` with the same options,
`transcribe <path> [--output "<folder>"] [--again] [--keyterms "<file>" | --keyterms none] [--no-open]`, `rebuild <path> [--no-open]`,
`finish-video <path> [--no-open]` (see [The lesson's video](#the-lessons-video)), `list [--output "<folder>"]`, `devices`, `mic-check [--seconds <n>] [--log-folder "<folder>"]`,
`camera-check ["<camera>"] [--resolution <size>]`, `drive signin [--client "<file>"]`, `drive signout`, `drive status`, `drive upload ["<path>"]`,
`drive free-space "<path>" --yes`,
`youtube <link>... [--list "<file>"] --name "<course>" [--output "<folder>"] [--download] (--yes | --plan)
[--no-transcribe] [--no-open] [--again] [--allow-unlisted]` and `youtube check`, `settings path|list|get|set|reset|open|fill`
and `class list|show|add|set|rename|remove` (see [Settings from PowerShell](#settings-from-powershell) and
[Saved classes](#saved-classes)),
where the parts in brackets may be left out, `(--yes | --plan)` means exactly one of the two, and SOURCE is `--device "<part of its name>"`,
`--device auto`, `--app <process>` or `--in-person ["<microphone>"]`. `<command> --help` (also `-h`,
`-?`, `/?` or `help <command>`) lists a command's options with examples.

`drive signin` opens Google's sign-in in your browser and waits up to 5 minutes for it (Esc gives
up, with exit code 130); the first time it needs `--client` with the client file you downloaded (see
[Google Drive (set up once)](#google-drive-set-up-once)), and after that the client ClassScribe keeps
is used. `drive status` prints the account, since when, the end of the client id (never the secret),
the storage used, ClassScribe's folder with its address, and what waits to go up, a line each
(`Waiting: Mantiq_2026-10-17_14-04-42Z (4 files, 727 MB), last try 17 Oct 21:40: ...`, or
`not tried yet`, or `Nothing waits to go up.`); it asks Google but changes nothing, and ends with
exit code 1 when ClassScribe is not signed in or Google cannot be reached. `drive signout` asks
Google to end ClassScribe's access, then forgets the sign-in whatever Google answers; when Google
could not be asked, it says how to end the access yourself and ends with exit code 1. Neither
touches anything on Drive.

`drive upload` takes a recording's file (that recording), a class's date folder, a course's folder
(each class in it, in either layout) or the recordings folder (each course in it; a folder in it
without a recording is named and passed over, and a recording's file lying at its top, such as a
paid answer kept there, is left out with the command that lays it out beside its recording); then it
uploads the rest of the queue that is due. Without a path it uploads every recording that waits. It
ends with exit code 0 when everything it took is on Drive (a recording on Drive already and unchanged
since says `... is already on Google Drive: ...`), and 1 when anything waits (not signed in, another
upload holding the lock, a recording being recorded, Google out of reach, Drive full, a file Drive
refused) or the path names no recording. `drive free-space` takes the same paths and needs `--yes`,
since it asks nothing; it ends with 1 when any recording kept its audio or Drive could not be asked.
Ctrl+C stops either with exit code 130; an upload then goes on from where it stopped the next time,
and free space deletes nothing it has not checked. See [Google Drive](#google-drive).

`youtube` needs `--name`, the course, at least one link or a `--list` file, and exactly one of `--yes`, which goes ahead after the plan, and `--plan`, which prints
the plan and stops; without `--output` the videos go to `Recording:OutputFolder`. Without `--download` each video is recorded as it plays, at most 10 videos, each
by its own link, and `--yes` still waits for Enter before each video; with `--download` the audio is
downloaded with yt-dlp, at most 500 videos, playlists included. `youtube check` takes nothing else. A
mistake is named with the command's help and exit code 2, before anything starts. See
[YouTube videos](#youtube-videos).

For `record` and `record-only` the name and a source are required, or `--class` and a saved class's
name, and nothing is ever asked: every missing piece and every value ClassScribe cannot use is named
at once, followed by that command's help, with exit code 2, before the settings are read and before
anything starts. A saved class's name given as the name, without a source, is pointed at `--class`:
`Mantiq is a saved class: ClassScribe record --class "Mantiq"`. Without `--output` the recording goes
to the saved class's own folder, else to `Recording:OutputFolder`, and its first line names the
folder. With `--class`, a name after `record` replaces the class's course, and options beside it
replace the class's own in groups: a source (`--device`, `--app` or `--in-person`) replaces its whole
source, `--mic` its microphone, `--mic-follows` the app followed, `--output` its folder. A class that
cannot be used, or that the options would make unusable, is refused with why, exit code 2.
`transcribe` on a folder takes the recording started last in it and in its date folders: for a
course's folder, its newest class in either layout; for a class's date folder, that class; for a
folder of the old layout, its newest recording. On one of a recording's files it takes that whole
recording; any other audio or video file, on its own, as menu choice 4 does. Given a
folder, it also names the other recordings there that have no transcript, each with its length,
the local time it started and the command that transcribes it (or, for one whose paid answer is
kept, the free `rebuild` command): `Also in this folder, without a transcript: ...`. It does so
before the upload and again after it, so that a class split in two by a crash is not half
transcribed. When `--output` names the same folder as `Recording:OutputFolder`, `record` and
`record-only` give the same warning as the menu about a folder that cannot be used, before they
record.

A recording of the same name that is running already is shown instead of started; another name
gives `Only one recording at a time: ...` and exit code 1. `record` with a name that stopped within
`Recording:ContinueWithinMinutes` without a transcript starts a new recording at once and says how
to continue the other one instead (`--continue`); it never asks and never waits. Only a recording
made with the same tracks is offered: one of the class alone is never continued with your
microphone, nor the other way round.

On the command line nothing is asked, so `transcribe` refuses two kinds of recording, with exit
code 1 and a message that says what to do instead:

- one that already has a transcript, or an answer ElevenLabs was paid for: transcribing it again is
  billed again. The message gives the free `rebuild` command (see
  [A transcript again, for free](#a-transcript-again-for-free)), then the same `transcribe` command
  with `--again`, which sends it all the same;
- one that is still being recorded, in another window: transcribe it once it has stopped.

A file dropped on `ClassScribe.exe` counts as the command line too. A recording whose audio was
deleted from the laptop after it went up to Google Drive is refused everywhere, with where its audio is
and the free `rebuild` command (see [Google Drive](#google-drive)); given its course's folder,
`transcribe` says so rather than send an older class in its place.

| Option | With | What it does |
| --- | --- | --- |
| `--device "<part of its name>"` | `record`, `record-only` | Records that output device and no other, found by part of its name, such as `"Pixel Buds"`. `--device auto` records whichever device the class plays on. |
| `--app <process>` | `record`, `record-only` | Records one app's sound, whatever device it plays on, by the app's process name, such as `chrome` (`chrome.exe` works too). One source only: `--device`, `--app` and `--in-person` cannot go together. |
| `--in-person ["<microphone>"]` | `record`, `record-only` | Records your microphone alone, for a class in the room: part of its name, such as `"OBSBOT"`, or nothing for `Recording:Microphone`, else Windows' default microphone (never a Bluetooth one). It is the third kind of source. The recording's name must come right after the word `record`, before `--in-person` or `--mic`, which would otherwise take it as the microphone's name. |
| `--mic ["<microphone>"]` | `record`, `record-only` | Records your microphone too, beside `--device` or `--app`, as a second track mixed into one transcript; it takes a microphone's name the same way. Not with `--in-person`, where the microphone is already the source. |
| `--mic-follows <app>` | `record`, `record-only` | With `--mic`: the app whose microphone use your microphone follows, by part of its process name, such as `chrome`; `auto` follows any app that holds the microphone; `none` records whenever V is on. Without it, the app recorded with `--app`, else `Recording:MicrophoneFollows`. Not with `--in-person`, and never ClassScribe itself. |
| `--class "<saved class>"` | `record`, `record-only` | Records the saved class of that name as it is saved, the name matched in any case (`ClassScribe class list` shows them; see [Saved classes](#saved-classes)). A name after `record` replaces its course; the other options replace its own in groups. |
| `--output "<folder>"` | `record`, `record-only`, `transcribe`, `list`, `youtube` | The recordings folder: the recording goes to the folder of its name inside it, and there to the date folder of the day it started (see [Where things go](#where-things-go)). Optional: without it, `record` and `record-only` use a saved class's own folder, else `Recording:OutputFolder`, and `youtube` uses `Recording:OutputFolder`. For `transcribe` it is used only for a file that is not a recording's; a recording's transcript goes beside the recording. |
| `--name "<course>"` | `youtube` | Required. The course the videos belong to, such as `"Risala"`: the folder they go to, as a class's name names it. |
| `--list "<file>"` | `youtube` | A text file of links, several on a line, separated by spaces, `#` starting a comment (see [Links and a list file](#links-and-a-list-file)). It may be given beside links on the line; they are joined in order. |
| `--download` | `youtube` | Downloads the audio with yt-dlp instead of recording each video as it plays: only for videos you uploaded or whose owner allows you to download them, never your course's (see [Downloading with yt-dlp](#downloading-with-yt-dlp)). |
| `--yes` | `youtube` | Goes ahead after the plan is printed. Recording as they play, Enter is still asked before each video, and you play it yourself. Downloading, it takes only the videos that need no word from you. |
| `--plan` | `youtube` | Prints the plan and stops, with exit code 0: nothing is downloaded, recorded or sent to ElevenLabs. Not with `--yes`. |
| `--no-transcribe` | `youtube` | Only fetches the audio; nothing is sent to ElevenLabs, and the summary gives each recording's `transcribe` command. |
| `--again` | `youtube` | Fetches videos that were fetched before again, as new recordings; transcribing them is billed again. |
| `--allow-unlisted` | `youtube` | With `--download` only: your word, given once for the batch, that every video yt-dlp says is unlisted is one you uploaded or may download. Refused (exit code 2) while `YouTube:CourseChannels` is empty; it never overrides another refusal (see [Downloading with yt-dlp](#downloading-with-yt-dlp)). |
| `--minutes <n>` | `record`, `record-only` | Stops by itself after that many minutes, counted from the start with pauses included, such as `90` or `1.5` (a dot, never a comma; more than 0 and at most 1440). Q twice still stops it earlier. |
| `--continue` | `record`, `record-only` | Continues, as its next part, the recording of that name that stopped within `Recording:ContinueWithinMinutes` without a transcript; when there is none, says so and starts a new one. |
| `--new` | `record`, `record-only` | Starts a new recording without a word about one that could be continued. Not with `--continue`. |
| `--again` | `transcribe` | Transcribes a recording that already has a transcript or a paid answer. This is billed again. |
| `--keyterms "<file>"` | `transcribe` | Sends this glossary with this transcript, instead of the one decided when the recording started; `--keyterms none` sends none. A relative path is taken from where you type the command, and `%APPDATA%` works. A file that is not there is a mistake on the command line (exit code 2), before anything is sent. The recording's `.glossary.json` notes it, and keeps what was decided (see [The glossary](#the-glossary)). Not with `rebuild`, which sends nothing. |
| `--no-open` | `record`, `record-only`, `transcribe`, `rebuild`, `finish-video`, `youtube` | Opens nothing at the end, whatever `Recording:OpenFolderWhenDone` and `Recording:OpenTranscriptWhenDone` say. Without it, `youtube` opens the course's folder once at the end, as `Recording:OpenFolderWhenDone` says, and never a transcript. |
| `--seconds <n>` | `mic-check` | Stops the check by itself after that many seconds, a whole number from 1 to 86400 (a day), such as `600`. Without it, Q or Ctrl+C stops it. |
| `--log-folder "<folder>"` | `mic-check` | Puts the check's log there, instead of the `mic-check` folder under `Recording:OutputFolder`. |
| `--client "<file>"` | `drive signin` | The client file you downloaded from Google (Google Auth platform > Clients > Download JSON). Needed for the first sign-in; ClassScribe then keeps the client, encrypted for your Windows account, and the file can be deleted. A file that is not there, or a folder, is a mistake on the command line. |
| `--yes` | `drive free-space` | Goes ahead and deletes the audio files from this laptop, once each is checked again against its copy on Google Drive. Needed, since the command asks nothing; without it nothing is deleted and the exit code is 2. |

An option may come before or after the path or the name, in any letter case; `--` ends the options,
for a name that starts with `--`. An argument that starts with `@` is a path, not a file of
arguments, and a first word in square brackets is a path from the current folder. A line
ClassScribe cannot read starts nothing: the mistakes are named, then that command's help is shown.
The menu is shown only when someone can type into the window: started without a command from a
script, ClassScribe says so, shows the help and exits 2.

`list` prints every recording under the recordings folder, newest first, one line each
(`Recordings in D:\Classes, newest first (times are local):`), in the course and date folders or
flat in the folder of its name: its name, when it started in local time, its length, its date
folder for a later class of the same day (`in 2026-10-17 (2)`), `2 parts` when it was continued,
`class + microphone` when your microphone was recorded with the class, `lesson video` or `lesson video
not made yet` for a lesson recorded with the camera, `audio on Drive` when its
audio is on Google Drive alone (its length is then the one it had when its audio left the laptop),
`transcript`, `transcript (2)` for one made twice, `answer kept` or `no transcript yet`, and
`RECORDING NOW`, `TRANSCRIBING` or `ENDED UNEXPECTEDLY` where that applies; then a line for each
recording being transcribed in the background. It changes nothing. `devices`
prints the output devices, marked `default` or `playing`, then the apps with sound
(`chrome  (app on Speakers (Realtek(R) Audio), playing)`), each with the `--device` or `--app`
option that records it, leaving out ClassScribe itself; then `Microphones:`, each with what it is
(`built in`, `default`, `USB`, `raw`, `Bluetooth: never chosen unless named; call-quality sound`, or
`records what the computer plays; never recorded`), `muted in Windows` when it is, and which apps hold
it now (`chrome is listening`, `vibe holds it without listening`), with the `--mic "<its name>"` option
that picks it (`--in-person` takes the same name), then the row `Windows' default microphone, never a
Bluetooth one  --mic`, or `None is connected now.` when there is none; then `Cameras:`, the colour
cameras Windows lists (never the infrared one Windows Hello uses), each with where it is (`built in,
front`, `USB`, or `virtual, such as a phone's camera through Phone Link: recorded only when named`)
and, for a webcam, its own microphone, which shares the camera's container in Windows
(`OBSBOT Meet Flip StreamCamera  (USB; its microphone: OBSBOT Meet Flip Microphone (OBSBOT Meet Flip
Audio))`), with the `--camera "<its name>"` option, then the row `A USB camera, else the built-in front
camera  --camera`. It opens nothing (`Nothing was opened or recorded to list these.`): no camera's
light comes on. It ends with example lines for `--device auto`, `--app chrome --mic` and `--in-person`.
A list Windows does not give at that moment says so in its place (`Could not be read now:
EnumAudioEndpoints: The parameter is incorrect. (0x80070057)`, the Windows call that failed first;
for the cameras, `The cameras could not be listed: ...`) and the others are listed in full, with exit
code 0; when only the apps that hold each microphone cannot be read, the microphones are listed with
`Which apps hold each microphone could not be read now: ...` below them. What a list could read only
in part is said below what it is about, with whose it was and which call failed: below the apps,
`Could not be read now: Zoom's audio session on "Speakers (...)" (GetState: The parameter is
incorrect. (0x80070057))` (an app Windows names is still listed; a session of no app named is left
out), and below a microphone, `Could not be read now: session 2 of 3 (Zoom, pid 9090) not readable:
GetState: ...`. Whenever anything could not be read, the cameras included, the line before `Nothing
was opened ...` says `Not everything could be read from Windows just now: the lines above say what,
and why.`

At the end, ClassScribe hands Windows an exit code, which a script can check (in PowerShell,
`$LASTEXITCODE` holds it):

| Exit code | Meaning |
| --- | --- |
| 0 | Done: the recording (`record-only`) or the transcript (`record`) was saved; also after Esc left the view of a recording that carries on, when `mic-check` stopped normally, when `drive upload` found everything it took on Drive, when `drive free-space` freed the audio of every recording it took (or found none left), when `settings` or `class` did what was asked (a setting set to the value it had included), when `class show` found the class usable, and when `finish-video` made the lesson's video. A lesson's video that was not made at the stop does not change a run's exit code: the pieces are kept for `finish-video`. For `youtube`: every video ended as asked (recorded with a confirmed end, or downloaded, and transcribed when asked) or was done before; `--plan` printed its plan; `youtube check` found all four tools, and yt-dlp at most 90 days old. An upload to Google Drive after a class or a transcript never changes the exit code. |
| 1 | It failed, or it was refused, and the message says why: settings ClassScribe cannot use (except for a recording, which runs without them), nothing recorded (a class track that holds nothing while the microphone's does counts here, with the command that transcribes the microphone's files alone), a transcript that failed (the audio is kept), another recording running, a recording with `--camera` whose camera a set-up line refuses before anything records (such as a camera that Windows keeps from desktop apps), `camera-check` when a set-up line is a problem or the camera it opened sent no picture, a recorder that was lost and not continued, a recording that already has a transcript or a paid answer (without `--again`), a recording being recorded now, `rebuild` without a saved answer, `finish-video` when the video was not made, or for a path that names no lesson, a lesson being recorded or having its video made, one without camera pieces, or one whose sound is on Google Drive only, `mic-check` unable to create its log, a Google sign-in that did not go through, `drive status` while not signed in or with Google out of reach, `drive signout` when Google could not be asked to end the access, `drive upload` with anything left waiting or a path that names no recording, `drive free-space` when any recording kept its audio or Google Drive could not be asked, and `settings` or `class` when your settings file is not valid JSON, has comments of your own, is missing, cannot be written, or keeps changing while ClassScribe writes it (`class show` answers 1 only for a file that is not valid JSON or cannot be read, and 2 while it is missing). For `youtube`: any video refused, left out, waiting for your word, not fetched, recorded but possibly cut short, not transcribed, or left recording in the background; another YouTube batch running; Chrome, yt-dlp or a helper not found; no API key while transcripts are asked; too little room for a download; `youtube check` finding a tool missing, or yt-dlp more than 90 days old. |
| 2 | A mistake on the command line, named with that command's help: a missing name or source, two sources, a saved class's name without `--class`, an unknown saved class or one that cannot be used, `--mic` with `--in-person`, `--mic-follows` without `--mic`, `--camera` without `--in-person`, `--resolution` without `--camera` or with a size it cannot take, a value that cannot be used, an unknown word, a path that is not there, `drive` alone, or `drive free-space` without a path or `--yes`. For `settings` and `class`: an unknown setting or class, the ElevenLabs key, or a value the check refuses (and `class show` for a class that cannot be used, or while your settings file is missing, so that a launcher records with its own values). For `youtube`: no `--name` or link, a word that is no link it takes, neither or both of `--yes` and `--plan`, a playlist's or `/live/` link or more than 10 videos without `--download`, more than 500 videos with it, and `--allow-unlisted` without `--download` or while `YouTube:CourseChannels` is empty. Nothing was started. (A batch whose playlists turn out to hold more than 500 videos once they are read ends with 2 as well, before anything is downloaded.) |
| 130 | Cancelled with Ctrl+C (`finish-video` included, its pieces kept), or with Esc while `drive signin` waits for Google; Ctrl+C during `drive upload` too, which goes on from where it stopped the next time, and during `drive free-space`. For `youtube`: Ctrl+C twice while videos are recorded as they play (no transcripts follow), Ctrl+C or Q twice during a download, and Ctrl+C during a transcript. |
| none | The window was closed while the recording ran in the background; the next start reports how it ended. Likewise for a `youtube` batch whose window was closed: a video being recorded carries on in its recorder to its end, nothing else goes on, and the same command later picks up where it stopped. |

When `record-only` ends, its last message gives the command that transcribes the recording; so does
`record` when there is no API key. When the transcript after `record` fails, or you cancel it, the
command ends the `WARNING` line that starts `The audio is safe in`, just above the final error (or
`Cancelled`). When ElevenLabs had already charged for an answer that is kept, two lines follow that
warning instead: the `rebuild` command that lays that answer out for free, then the
`transcribe ... --again` command for a new try, which is billed again. A recording with nothing
audible in it gets no command, as trying again would find the same silence, and neither does one
longer than ElevenLabs takes in one file, which would be refused again. Menu choices 1 and 2 do the
same. Each command names the program by its full path, so you can paste it into PowerShell as it
is, from any folder.

### If a recording was interrupted

A recording that runs in the background keeps a run file in `%LOCALAPPDATA%\ClassScribe\runs`, with
a heartbeat every 10 seconds. When its process is gone while the file still says it was recording,
every start of ClassScribe reports it until you deal with it:

```
The recording "Kalam" (Kalam_2026-10-15_23-00-02Z) ended unexpectedly at about 19:47, 00:47:10 in. Its audio is kept.
If you did not stop it, check Bitdefender's Notifications (Protection > Advanced Threat Defense).
  C  continue it now as its next part     T  transcribe what was recorded
  F  forget this                          Enter  go on to the menu
```

Enter goes on; so do Esc and Ctrl+C. After the runs, once Google Drive is set up, the report says
what waits to go up there, with U to upload it now (see [Use it](#use-it)). A class whose recorder
ended unexpectedly went into that queue as it started, so it goes up later too, from where it is.

The second line names the cause when it is known: `Windows shut down or signed you out at about
19:47`, `Windows restarted at about 19:47`, `It ended when its window was closed at about 19:47`
(a recording that ran in the window), or `It stopped because of an error at about 19:47; <base>.log
says which`. C is offered while the recording can still be continued (within
`Recording:ContinueWithinMinutes`, with no transcript and no paid answer kept, shorter than 9 and a
half hours, and no other recording running) and continues it with the run's own name, source and
folder. On the command line the report prints the `record ... --continue` and `transcribe` commands
instead, then `To forget it: start ClassScribe without a command, then press F.`, and asks nothing.

A YouTube video's recording is never continued, by C or by the window (see
[YouTube videos](#youtube-videos)): the report names it as the video (`The recording of the YouTube
video "The creed, part 1" (Risala_2026-10-18_14-02-11Z) ended unexpectedly at about 19:47, 00:12:10
in. Its audio is kept.`) and offers T and F, never C; the same `youtube` command records the video
again, and transcribes what it finds whole.

A transcription that was interrupted (the background process ended during the upload) is reported
the same way, with T and F: `The transcript of "Kalam" (Kalam_2026-10-15_23-00-02Z) was not
finished; its audio is complete. If the upload had finished, ElevenLabs may bill it again.` So is
an end nobody saw, once: `"Kalam" was transcribed in the background: <path>. O opens it; any other
key goes on.`, `"Kalam" was recorded in the background: <folder>.` with the command that
transcribes it, or `"Kalam" ended in the background: <why>` with the command to try again. A run
that has been dealt with since (continued by a later run, transcribed, or its audio deleted) is
not reported, and one whose recording is on a drive that is not there now, such as an unplugged USB
drive, waits for a start that finds the drive.

A lesson recorded with the camera whose video is still to be made (its pieces are there and its `.mp4`
is not, or a piece is newer than it), and whose process is gone, gets **V** beside the others: `V  make
the lesson's video now, free`. T is offered for a lesson only when it was to be transcribed and has no
transcript, and C only when it ended while recording; a lesson whose video's making was cut off when
that was all it had left to do (record-only, or its transcript saved already) is said as `The lesson
video of "Tajwid" (...) was not finished; its sound and its camera pieces are kept.` After an end nobody saw, a lesson without its video is said once, with V: `The lesson video
of "Tajwid" was not made; its sound and its camera pieces are kept.` On the command line the report
prints `To make its lesson video, free: ... finish-video "..."`. A lesson is dealt with once it has its
transcript (when it was to have one) and its video.

A recording continued as its next part stays one recording, in its own folder, whichever layout it
was made in and whatever the date is now: the new audio goes into the next part file, the marks go
on, and the transcript says `Recorded 15 October 2026, 23:00 UTC, in 2 parts.`
with a heading `## [00:47:10] Resumed at 23:51 UTC, about 4 minutes not recorded` at the join. A
recording that can no longer be continued, such as one whose audio was moved, costs nothing: the
run warns (`Could not continue ...; recording "Kalam" as a new recording instead`) and records a
new one. Only a recording made with the same tracks is continued: one of the class alone is never
continued with your microphone, nor the other way round, and the report and the menu then offer
nothing to continue. With your microphone, each track carries on from its own length on disk, and
the shorter one (cut short by a crash) is first padded with silence up to the recording's length, so
the tracks stay in step.

While a window shows a recording whose background process ends unexpectedly, the window continues
the recording by itself within a few seconds (`The recorder ended unexpectedly at 19:47:12;
continuing the same recording.`), with the rest of `--minutes` when there was one, the window that
came back by itself included, with its keys still waiting for Enter. After a second loss within 10
minutes, which most likely means Bitdefender is ending the recorder, a window you started continues
in itself (closing it then ends the recording), while a window that came back by itself declines
(`ClassScribe opened this window by itself, so the recording is not continued in it. Its audio so
far is kept; start ClassScribe to continue it, or to transcribe it.`) and leaves the report for the
next start.

A restart, shutdown or sign-out you start during a recording or its upload makes Windows ask first
and name ClassScribe (`Recording:BlockShutdown`); if Windows ends the session anyway, the files are
finished and the next start offers to continue. A recording also stops by itself after
`Recording:StopAfterHours` (9 hours), with a warning 15 minutes before, because ElevenLabs takes at
most 10 hours in one file.

### A transcript again, for free

ClassScribe keeps ElevenLabs' answer (the `.json`) beside each transcript, so a transcript can be
made again without sending anything or paying again: after you have typed labels into the marks
file, say, or changed the `Transcript` settings. Press R in menu choice 3 or 4 for a recording that
has a transcript, or run `rebuild`:

```powershell
dotnet run --project src/ClassScribe -- rebuild "C:\path\to\recording folder or file"
```

`rebuild` takes a folder (its newest recording with a saved answer, in it or in its date folders, so
a course's folder, a class's date folder or a folder of the old layout, even one whose audio has
been deleted; a newer recording it passes over is named, with the reason), any file of a
recording (`.wav`, `.marks.txt`, `.md`), or an answer saved as `.json`. It lays out the recording's
newest saved answer, or the `.json` given, needs no API key, uploads nothing, and says so:
`Rebuilt from Kalam_2026-10-14_19-30-00Z.json into Kalam_2026-10-14_19-30-00Z (3).md; nothing was uploaded`,
ending with `Nothing was sent to ElevenLabs, so nothing was charged` where a transcription's last
line says what it cost. The new transcript goes beside the old ones as the next free copy, with the marks (labels typed in
since included) and the `Transcript` settings as they are now; nothing is ever overwritten. A
recording that was never transcribed has nothing to lay out yet:
`... has no saved answer from ElevenLabs yet; transcribe it first`.

For a recording whose paid answer is kept without a transcript (`answer kept`), R lays that answer
out. An answer ClassScribe set aside as `.unread.json`, because it could not be read or saved in its
place, is used only when you give `rebuild` that very file: given the recording, `rebuild` names it
and gives the command. The words are ElevenLabs' answer as it was: to use a changed glossary, the
recording has to be transcribed again, which is billed again (`transcribe <path> --again`, with
`--keyterms "<file>"` for another glossary than the one decided when it started).

### Google Drive

Once ClassScribe is signed in (see [Google Drive (set up once)](#google-drive-set-up-once)), each
class goes up to your Google Drive by itself, once its transcript is saved and shown (with record
only, once its files are closed), and also after a transcript that failed, so that the audio is safe
first. Not after a cancel, a run that recorded nothing, a recorder that failed, or a run Windows
ended: such a class waits in the queue. A run that recorded nothing leaves little more than its
`.log` in its folder, which goes up with the next upload and is counted as waiting until then. In
the background recorder the upload happens after the window has been shown the end, while the next
class may already record, and its lines go into the recording's `.log`; closing the window does not
stop it. A recording that runs in the window (`dotnet run`, or `Recording:SeparateProcess` set to
`false`) uploads in the window after the end, and Ctrl+C there stops the upload, with everything
left waiting. Nothing about Google Drive ever changes how a class or its transcript ends, or the
exit code. A transcript made later (menu choices 3 and 4,
`transcribe`, `rebuild`) sends only its new `.md` and `.json`; a file transcribed on its own sends
its transcript and answer, not the file itself. `Drive:Enabled` set to `false` stops the uploads
after each class and after a transcript, and nothing goes into the queue by itself; 9 and
`drive upload` still upload.

**What goes up**, uncompressed: every file of the recording but the temporary `.upload.wav`, in this
order: the transcripts (`.md`, `(2).md`), ElevenLabs' answers (`.json`, a kept `.unread.json`), the
marks, the class's audio (`.wav`, `_part2.wav`), your microphone's (`.mic.wav`), and the log; and last,
only with `Drive:LessonVideos` set to `true` (it is `false` by default), a lesson's finished video
(`.mp4`, sent as video/mp4, read from the file as it goes, never into memory), with the line `Sending the
lesson's video (2.4 GB) to Google Drive; at this connection's speed so far, about 30 minutes.` A lesson's
camera pieces and its `.mp4.partial` never go up. At the start of a lesson with Drive set up a line says
where its video goes; with `Drive:LessonVideos` off: `The lesson's video stays on this laptop; to send
lesson videos to Google Drive too, run: & "...\ClassScribe.exe" settings set Drive:LessonVideos "true".
Its camera pieces are deleted once the video passes its check.` (A free 15 GB Drive holds about six hours
of 720p video.) A lesson's video made again since its copy went up (a continued lesson's, or one made
with `finish-video`) goes up whole, as a new revision of that copy, with the line `<base>.mp4 was made
again since its copy went up to Google Drive, so it goes up whole, as a new revision of that copy`: an
hour of video takes as long again to send. Google Drive keeps the older revision for 30 days, under the
file's Manage versions.
Your own files in the folder, such as the class's PDFs, never go up: only files named as the
recording's are taken. With `Drive:Upload` set to `WithoutAudio` the `.wav` files stay on the laptop
alone, and so does a lesson's video, which holds the same sound. A YouTube video's recording goes up the same way, its `.source.json` with it, but
`YouTube:DriveUpload` decides about its audio instead of `Drive:Upload`: `Everything` by default,
or `WithoutAudio` (see [YouTube videos](#youtube-videos)). The window, the `.log` and `recorder.log` say it as it goes:
`Google Drive: uploading Mantiq_2026-10-17_14-04-42Z (4 files, 727 MB) to ClassScribe/Mantiq/2026-10-17`,
then `Mantiq_2026-10-17_14-04-42Z.wav: 30% (217 MB of 722 MB, 3.1 MB/s)` for each tenth of a file
that goes in more than one piece, and `...: already on Drive` for one that is there already. Sizes
are in the units Google Drive shows, where 1 MB is 1,048,576 bytes.

**Where**: Drive mirrors the laptop (see [Where things go](#where-things-go)): `My Drive > ClassScribe >
Mantiq > 2026-10-17` for a class in its course and date folder, `2026-10-17 (2)` for the second class
of the day, and `My Drive > ClassScribe > Kalam` for a recording still flat in the folder of its name.
ClassScribe finds each folder again by an id it remembers (in
`%LOCALAPPDATA%\ClassScribe\drive\folders.json`), or by its name, and makes it only when it is not
there; only one upload runs at a time, so a folder is never made twice.

**Each file is checked**: after its last piece, Google Drive is asked for the copy's size and MD5,
which must be the file's. A copy that differs is said, with both
(`... does not match Drive's MD5 after it went up (here ..., ... bytes; on Drive ..., ... bytes);
sending it again as a new revision`), and sent once more as a new revision of itself; if it still
differs, the recording waits. The last line says where the class is:
`Mantiq_2026-10-17_14-04-42Z is on Google Drive: My Drive > ClassScribe > Mantiq > 2026-10-17 (4 files, 727 MB, each checked against Drive's MD5): https://drive.google.com/drive/folders/...`.
In the background recorder, and in a run in the window, it is written into the `.log` just before
the log goes up, so Drive's copy of the log holds it. Lines logged after that, such as those about
other recordings the same upload takes from the queue, stay in the laptop's `.log`; they go up, as a
new revision of the log, the next time that recording goes up (after a transcript made later, say).

**Nothing goes up twice**: ClassScribe remembers each file it sent (in
`%LOCALAPPDATA%\ClassScribe\drive\recordings`), so another upload of the recording sends only what is
new. A new transcript copy or a new part is a new file; a text file that has grown, such as the log
after a continued class or the marks, goes up as a new revision of ClassScribe's own copy, and as a
new file when that copy was deleted on Drive or is in the trash, which ClassScribe never touches. A
lesson's video made again goes up the same way, as a new revision of its own copy, larger or smaller.
Nothing else on Drive is ever replaced: a file on the laptop that changed in any other way is left
out, with a warning, and the copy on Drive stays as it is. When the record of a recording is lost
(another laptop, `%LOCALAPPDATA%` cleared), the files already in its folder on Drive are taken for
its own when name and MD5 match. An upload stopped half way (offline, the laptop asleep, a closed
window, Ctrl+C) goes on from where it stopped next time, even after a restart, for up to six days.

**The queue**: with `Drive:Enabled` on, a class goes into it as its run starts, so a class whose
recorder crashed still goes up later. What could not go up waits there until the next class, whose
recorder uploads that class first and then the rest of the queue that is due, or until you press U
in the report at the start, or 9 > U, or run `drive upload` without a path. A transcript made later
takes what is due in the queue along too. Nothing uploads by itself at any other time: there is no
uploader in the background. Only one upload runs at a time, across ClassScribe's windows and
recorders; another says `Another ClassScribe is uploading to Google Drive; ... waits and goes up after
it`, and the one uploading takes it before it stops, even a recording it was uploading as a
transcript of it was made. While a class records, an upload of an earlier one waits between pieces
(`Google Drive: paused while "Kalam" records`) and goes on once it stops (`Google Drive: going on, now
that "Kalam" has stopped recording`), so the class's own sound and your microphone are not held up.
It waits the same way while a class is transcribed, or a lesson's video made (`Google Drive: paused
while "Kalam" is transcribed`, then `Google Drive: going on, now that "Kalam" is done`), so that the
transcript's own upload to ElevenLabs has the connection to itself. A lesson whose video is being made by
`finish-video` waits too (`... is having its lesson video made now; it goes up once it has stopped`).
The laptop is kept awake meanwhile (see [Keeping the laptop awake](#keeping-the-laptop-awake)).

A recording asked for again that is on Drive already and has not changed is not put back in the
queue. One whose folder is on a drive that is not there now, such as an unplugged USB drive, waits for
an upload that finds it. One whose folder you moved or deleted on a drive that is there is done when
every file of it went up before; otherwise it is taken out of the queue, with how to upload it from
where it is now (9 > F, or `drive upload "<its folder>"`).

#### Free room on the laptop

Once a class is on Drive, 9 > L, or `drive free-space "<path>" --yes`, deletes its audio from the
laptop: its `.wav`, `_partN.wav`, `.mic.wav` and `_partN.mic.wav` files, and nothing else. Before
anything is deleted, every one of them is checked again: its upload was checked when it went up, its
MD5 is worked out afresh from the file itself, and Google Drive is asked now for its copy, which must
be outside the trash with the same size and MD5. If any one fails, none of that recording's audio is
deleted, and the reason is given. The transcripts, ElevenLabs' answers, the marks and the log stay on
the laptop, and so do your own files; a recording being recorded or transcribed keeps its audio,
and so does one that has not gone up yet (`... has not gone up to Google Drive yet, so none of its
audio was deleted. Upload it first: ...`, with the `drive upload` command for it). The line says
what was freed: `Freed 722 MB on this laptop: deleted 1 audio file of Mantiq_2026-10-17_14-04-42Z
(Mantiq_2026-10-17_14-04-42Z.wav). Their copies stay on Google Drive, in My Drive > ClassScribe >
Mantiq > 2026-10-17; the transcripts, ElevenLabs' answers, the marks and the log stay here`. Given
several recordings, such as a course's folder, it frees each one that passes, keeps the audio of
each one that does not, and ends with `Freed ... on this laptop in all`. A YouTube video's recording
whose audio never went up, under `YouTube:DriveUpload` `WithoutAudio`, keeps it, which is no failure
and leaves the exit code as it is: `Risala_2026-10-18_14-02-11Z is a YouTube video's recording; its
audio is not on Drive (YouTube:DriveUpload), so it stays. Delete it by hand if you need the room; the
video can be fetched again.`

A lesson whose video went to Drive (`Drive:LessonVideos` on) kept its camera pieces for this: once its
video's copy on Drive is checked the same way (its MD5 worked out afresh, Drive's copy outside the trash
with the same size and MD5), the pieces are deleted with the audio: `Freed the camera's 2 pieces of
... (4.6 GB): its lesson video is on Google Drive, checked, in My Drive > ...; the video stays here too`.
The finished video itself is never deleted. A video whose copy on Drive no longer matches keeps the
pieces, with why (`The camera's pieces of ... stay: ... differs from its copy on Google Drive ...`); a
video not on Drive keeps them too. Only the pieces the video holds go: a piece newer than the video
stays, named (`... is newer than the lesson video of ..., which does not hold it, so it stays on this
laptop`). A lesson whose video is still to be made (`lesson video not made yet` in `list`), such as a
continued lesson whose new part's video was cut off, keeps its audio and its pieces alike, since making
the video needs both: `The lesson video of ... is still to be made, so none of its audio or camera pieces
were deleted: making it needs them. Make it first, free: ...`, with the `finish-video` command.

Deleting audio, this way or by hand, never touches Drive. A recording whose audio is gone from the
laptop after it went up counts as done, never as waiting, and is never uploaded again: `list` and menu
choice 3 show it with `audio on Drive` and its length as it was; its transcript stays readable, and R
or `rebuild` make its transcript again for free from the saved `.json`; transcribing it again is
refused with where the audio is: `The audio of ... is on Google Drive, not on this laptop: My Drive >
... (https://...). To transcribe it again, download its .wav files from there into ... first`, followed
by the free `rebuild` command.

### Recording with the camera

For a lesson video, ClassScribe records your camera's picture beside your microphone, in person: the
laptop's own camera or a USB webcam such as the OBSBOT Meet Flip. The sound is recorded exactly as
without a camera, and is still what is transcribed; the picture goes into the camera's own files, its
**pieces**, on the same clock as the sound, so the two stay together through a pause, a sleep and a long
lesson; at the stop the pieces and the sound become the lesson's finished video, one `.mp4` (see
[The lesson's video](#the-lessons-video)). Run [the camera check](#checking-the-cameras) once first, so
that Bitdefender asks about ClassScribe while you watch.

- In the menu: choice 7, I, your microphone, then the camera's number and the picture's size
  (`Picture size: Enter for 720p (your setting), 1 480p, 2 720p, 3 1080p, T to type a size`). The
  header then reads `Recording from: your microphone "OBSBOT", in person, with the camera "OBSBOT Meet
  Flip StreamCamera" (720p)`. The camera is no setting: it holds until ClassScribe is closed, and
  every start from the menu asks first: `Camera "OBSBOT Meet Flip StreamCamera" (720p): Enter records
  with the camera; S records the sound only.` (Esc goes back.)
- On the command line: `record "Tajwid 4" --in-person "OBSBOT" --camera "OBSBOT" --resolution 720p
  --output "<folder>"`. `--camera` takes part of a camera's name, or nothing for a USB camera when one
  is plugged in, else the built-in front camera (a phone's virtual camera only when named), and only
  with `--in-person` (`--camera records only in person in this build: give it with --in-person`, exit
  2). `--resolution` takes `480p`, `720p`, `1080p` or a size such as `1024x576`, only with `--camera`
  (`--resolution needs --camera`, exit 2). The command line is your word: it records with the camera
  without asking.
- The picture's size: `--resolution`, else the menu's pick, else `Recording:CameraResolution`, else
  720p, decided by the window before the recording starts and kept in its run file.
- Before the size counts, the start refuses the camera when Windows is older than 10 version 2004,
  when Windows' privacy switches keep desktop apps from the camera, or when no H.264 encoder is
  installed, with the same words as the camera check: in the menu Enter then records the sound only;
  on the command line nothing is recorded, exit 1.

What happens while it records:

- The start says `Recording from your microphone "OBSBOT", in person, and the camera "OBSBOT" at 720p`,
  then, once the camera opens, `Camera: "OBSBOT Meet Flip StreamCamera" (USB), 1280x720 at 30 frames a
  second, 5 Mbit/s, hardware encoder (...). Its light is on while it records; P lets it go during a
  pause`. The camera is opened **shared**, never taken from another app: a class app can use it at the
  same time. Shared, ClassScribe takes the camera's pictures at the rate it is running (another app may
  have set it), so the line gives that rate, up to the 30 a second a piece keeps; when Windows does not
  say it, the line says `at up to 30 frames a second`, and the status line gives the rate measured.
- The picture is H.264 at the size you chose, at most 30 frames a second, at a constant bitrate: about
  2.5 Mbit/s at 480p, 5 at 720p, 8 at 1080p (a typed size between them in proportion to its pixels).
  The encoder's settings are fixed (Main profile, the level the size needs, no B-frames, a keyframe every
  second). The graphics' own encoder is used when Windows offers it; when it refuses, Windows' software
  encoder takes over, with the line `The hardware encoder could not start (...); the camera's picture is
  encoded by Windows' software encoder instead`.
- **P** pauses the sound and the picture together, and lets the camera go, so its light goes off during
  the break; P again opens it, and the picture carries on where the sound does.
- **The status line** adds `camera 1280x720, 29.9 frames a second, 3 dropped`, or `camera not
  recording`. The title adds `CAM WAITING` while the picture is not recording (not open yet, unplugged,
  refused, restarting) and `CAM DARK` while it is black.
- **The stop** (Q twice): the camera hears at once where the sound ends, so that no picture after it is
  kept; the sound is stopped and saved first; then the camera's piece is finished at the very end of the
  sound, within 15 seconds, after which its camera process is ended. The stop waits for the camera 25
  seconds at most in all, even for a camera process stuck inside Windows, so a camera can never hold up
  or cost the sound, the transcript or the next class. Then the lesson's video is made beside the
  transcript (see [The lesson's video](#the-lessons-video)).
- **A camera that fails** costs the picture only. Unplugged or folded, its picture stops: a black frame
  marks where, the line `The camera "..." stopped sending its picture; the sound records on. Plug it back
  in` comes (with `Its microphone may be silent too while the camera is away or asleep` when its
  microphone is the one recorded), and ClassScribe opens it again every 2 seconds into the same piece.
  There is never a stand-in camera. If the camera's process stops working three times within 10
  minutes, the rest of the lesson is recorded without its picture (`The camera stopped working three
  times; ...`).
- **Room on the disk** counts the picture twice (its pieces, and later the finished video): two hours at
  720p take about 10 GB with the sound. When the disk has room for less than 30 minutes of sound and
  picture together, the camera is let go and the sound records on: `Little room is left on D:\ (...
  free), so the camera is let go: the rest of this lesson is recorded without its picture. The sound
  records on`.
- **Continuing** a lesson (C, or `--continue`) is offered only to a run with the camera, and a recording
  without one only to a run without; the continued part's picture goes into a new piece. A lesson
  recorded with the camera is still one even when its camera never gave a picture (it was not
  connected, say), so a run with the camera continues it and writes its first piece.

The pieces are `<base>.cam.mp4`, and `<base>.cam@hh-mm-ss.fff.mp4` for one that begins later on the
clock (a continued part, or after the camera's process was started again), the name holding where it
begins. Each is a fragmented MP4 of the picture alone: if the laptop loses power or ClassScribe is
ended, a piece is readable up to its last second or so. A piece holds the picture alone, without sound.
The pieces are never sent to Google Drive.

**Matching the picture to the sound.** Every camera shows its picture a little early or late against the
sound. To measure yours: record a short lesson clapping once in view of the camera, open the piece and
the recording's `.wav` side by side (any video editor), and note how many milliseconds the clap's
picture comes before (negative) or after (positive) its sound. Save it under `Recording:CameraOffsets`,
by part of the camera's name: `ClassScribe settings set Recording:CameraOffsets:OBSBOT -60`, which
writes `"CameraOffsets": { "OBSBOT": -60 }` into your settings file. The start then says `The picture
is placed 60 ms earlier for "OBSBOT Meet Flip StreamCamera" (Recording:CameraOffsets)`.

#### The lesson's video

Once the sound has stopped (Q twice), ClassScribe makes the lesson's video, `<base>.mp4`, beside its
transcript and under the same name, so players pair the two. Nothing is uploaded or billed for it. It is
made in a camera process of its own while the transcript is made, and the end of the run waits for both:

- **What it holds.** The picture from the pieces in their order, each at its place on the lesson's clock,
  a gap holding the last picture; the sound is the lesson's `.wav` tracks mixed as the transcript's upload
  is (your microphone left out when it stayed silent), at 48 kHz, AAC mono at 128 kbit/s, as YouTube asks.
  When every piece was made by the same encoder at the same size, the picture is joined as it is, about a
  minute for two hours; when they differ (the software encoder took over after the hardware one failed, or
  a continued lesson was recorded at another size), the whole picture is encoded again once, which takes
  longer, and a line says so: `... was encoded differently from ... (another encoder or another picture
  size), so the whole picture is encoded again, which takes longer than joining the pieces as they are`.
- **The check.** It is written first as `<base>.mp4.partial`, then checked: it must open, hold a picture
  and a sound, be as long as the lesson's longest `.wav` track within a tenth of a second, and a picture
  must decode at its start, on each side of every join between pieces, and at its end. Only then is it
  renamed to `<base>.mp4`, replacing an older video of the lesson. The end of the run says `The lesson's
  video: Tajwid 4_2026-10-19_14-00-02Z.mp4 (1:02:13, 2.4 GB), with its sound. Watch it before you publish
  it.` Its index sits at the end of the file, where Windows writes it: players read it there, and a video
  streamed from Google Drive starts once the whole file has arrived.
- **The pieces afterwards.** Once the video has passed its check, the pieces are deleted, with the line
  `Deleted the camera's 2 pieces of ...: the lesson's video passed its check, and lesson videos do not go
  to Google Drive (Drive:LessonVideos), so the video is the one copy of the picture kept`. Only when the
  video goes to Google Drive (Drive set up, `Drive:LessonVideos` on, `Drive:Upload` sending audio) are they
  kept until [free room](#free-room-on-the-laptop) finds the video safe on Drive, and deleted then with the
  `.wav` files. The finished video itself is never deleted. A piece whose picture cannot be read (as one
  whose camera process was ended before it wrote anything) is left out of the video with a line, and stays
  on the laptop, since its picture is in no video. A piece Windows cannot open at that moment (another
  program holds it), or an older video that cannot be read, stops the making with every piece kept, so
  that no picture is lost to a video made without it; the line says what to do.
- **While it is made** the run's file says the recording is complete and being transcribed or its video
  made, so Google Drive, `transcribe` and a move wait, and the next class may start. The laptop is kept
  awake until the video is made. **Ctrl+C** after the stop cancels the video with the transcript: the
  unfinished file goes, the pieces stay.
- **When it is not made** (a failed check, Ctrl+C, a camera process that says nothing of its progress for
  2 minutes, a crash, Windows ending the session), the pieces are kept and the line names the command that
  makes it again, free. `list` and menu choice 3 say `lesson video not made yet` (and `lesson video` once it
  is there); menu choice 3's **V** then the lesson's number makes it; and the report at the next start
  offers **V** (`make the lesson's video now, free`).
- **Again, free:** `ClassScribe finish-video "<the lesson's folder or one of its files>" [--no-open]` makes
  it again in this window, from the pieces that are still there, positions before the first of them taking
  the older video's picture. It needs the lesson's `.wav` files: after free-space it says `The sound of this
  lesson is on Google Drive only, so its video cannot be made again here.` (exit 1). It refuses a lesson
  being recorded or having its video made, and one without camera pieces. Exit 0 made, 1 not made, 130
  cancelled.

### Checking the cameras

Menu choice 8 then C, or `ClassScribe camera-check`, shows what ClassScribe knows of the cameras,
opening none of them until you type Y:

- `Cameras (from Windows' own lists; none was opened):`, numbered, each with where it is and, for a
  webcam, its own microphone (the one that shares its container in Windows), as `devices` lists them;
- which apps use a camera now, from Windows' camera record (`Using a camera now, as Windows' camera
  record shows: Zoom.`, or `No app is using a camera now, ...`);
- the camera and the picture size a lesson recorded with `--camera` would take: `A lesson recorded
  with --camera would take the camera "OBSBOT Meet Flip StreamCamera", at 720p (1280x720;
  Recording:CameraResolution).` `camera-check "Integrated" --resolution 1080p` shows another camera
  and size; a name that fits no camera says the lesson would wait for it;
- `Set-up:`, a line each, `OK`, `PROBLEM`, `NOTE` or `OFF`: Windows' version (10.0.19041, version
  2004, or later), Windows' camera privacy switches (Settings > Privacy & security > Camera, Let
  desktop apps access your camera), an H.264 video encoder (a Windows N edition needs the Media
  Feature Pack), the antivirus that is on, with Bitdefender's Webcam Protection step (`Bitdefender:
  Privacy > Video & Audio Protection > Webcam Protection`: allow ClassScribe when it asks), each
  webcam's own microphone, and whether `Recording:WakeCameraMicrophone` is on.

Then it offers to open that camera: `To see that "OBSBOT Meet Flip StreamCamera" opens, ClassScribe can
open it now for 5 seconds, shared, at 720p, as a lesson will: ...` and `Type Y and Enter to open it, or
just Enter to leave it closed:`. Only Y opens it; anything else ends with `The camera was not opened.`,
and a window that takes no typing with `The camera is opened only after you type Y, and this window takes
no typing, so it was not opened.` After Y, a camera process of ClassScribe's own opens the camera
shared, never exclusively, so a class app can still use it, and keeps none of the picture. Its light
comes on, and a security program may ask about ClassScribe now, while you watch: allow it (Bitdefender:
Webcam Protection). Every call is shown in order with its outcome and how long it took, such as `4.
MediaCapture.InitializeAsync (SharedReadOnly, video only): OK (412 ms)`, or with the code that failed
and Windows' words, such as `failed (35 ms): 0x80070005 E_ACCESSDENIED (Windows: "Access is
denied.")`; then the camera's format as it runs, the frames counted, and the end: `The camera "..."
opened, shared, and sent its picture: 148 frames in 5 seconds.` Do this once before turning on
`Recording:WakeCameraMicrophone`, and after installing a new build if Bitdefender asks again.

The exit code is 0, or 1 when a `PROBLEM` line keeps the camera from being used, or the camera that was
opened sent no picture or could not be opened.

### A webcam's microphone (the OBSBOT)

A webcam with its own microphone, such as the OBSBOT Meet Flip, is told by the container its camera and
its microphone share in Windows: never the laptop's own camera, whose container the laptop's microphone
array shares, and never a phone's camera through Phone Link. Two things differ when its microphone is
recorded, beside the class or in person.

- **Its silence.** The OBSBOT's noise reduction sends exact zeros whenever the room is quiet, even
  awake, and sends nothing but zeros once the camera has fallen asleep (Device Auto Sleep in OBSBOT
  Center; an open microphone does not keep it awake). So the warning about only silence that other
  microphones get gives way to two rules that a quiet room does not set off. They count exact zeros as
  the camera sends them, before ClassScribe rounds the sound to 16 bits, so Windows' faint background
  noise counts as sound.
  - At the start: 60 seconds of exact zeros since the microphone opened gives `The microphone of the
    camera "OBSBOT Meet Flip StreamCamera" has sent only exact zeros since it opened, 1 minute ago. If
    the camera's light is off, the camera is asleep and its microphone off: ...`, once each time the
    microphone opens.
  - After it has carried sound: 2 minutes of exact zeros gives `The microphone of the camera "..." has
    sent only exact zeros for 2 minutes. If its light is off, the camera has fallen asleep ...`, at most
    once a recording; a quiet class gives no more than this one line.

  Each is a Notice with the class, and an Alert (in red) in person. The camera's light is the test: off,
  the camera is asleep and your voice is not recorded; on, the room is just quiet and nothing is lost.
  Windows' own silence (a microphone muted in Windows, or withheld by its privacy settings) keeps the
  warning every microphone gets.
- **Keeping it awake.** With `Recording:WakeCameraMicrophone` on (it is off unless you turn it on),
  ClassScribe holds the webcam's picture open from before its microphone opens until the recording
  stops, through P and V, in a camera process of its own, so the camera cannot fall asleep. The camera
  is opened shared, never exclusively, so the class app can still show your face; ClassScribe asks it
  for no size and no format, lets every frame go unread and keeps none of the picture. The camera's light
  stays on while recording. The microphone opens once the first frame has come, or after 5 seconds; its
  track is on the recording's clock, so the class's track never waits. While the held picture's frames
  arrive, the later zeros rule is off: the camera cannot be asleep. When the recording follows the class
  app to another microphone, the hold follows, or lets go for a microphone of no webcam. Nothing the
  camera does can keep the microphone from recording: when Windows does not list the cameras within 5
  seconds, or a camera process cannot start, the microphone is recorded all the same and the hold tries
  again by itself. Run `camera-check` and type Y once first (see
  [Checking the cameras](#checking-the-cameras)).

### Checking what your class page does with your microphone

Menu choice 8 then W, or `ClassScribe mic-check`, watches what the programs on your computer do with the
microphone and writes a line for each change, until you press Q (or Ctrl+C once), or until
`--seconds` runs out. It only reads Windows' own lists: which microphones there are, which programs
have a session on them, and Windows' record of which program used the microphone and when (the same
record as Settings > Privacy & security > Microphone > Recent activity). It never opens a microphone,
records no sound, sends nothing anywhere, needs no administrator rights, takes no lock, and may run
beside a recording. One run of it during a real class, with you muting and unmuting in the class
page, tells what the page does to the microphone when you mute, which is what `Recording:ClassAppMute`
needs (see [Your microphone](#your-microphone)).

The log goes to the `mic-check` folder under the recordings folder, as
`mic-check_2026-10-17_19-00-12.log` (the local start time; `--log-folder` puts it elsewhere), and its
path is printed at the start and at the end. Every line reaches the file as it is written, so the
file is complete even if the window is closed, and the window's title shows
`MIC CHECK 00:12:40, 23 lines  Q stops`. M writes a numbered `MARK` line.

The steps, before or during a class:

1. Start the check: choice 8 then W, or `ClassScribe mic-check` in a terminal. It prints a few lines about
   how things are right now, then waits. Leave it open; it is fine behind Chrome, for the whole class.
2. Join the class as usual, with headphones, and start muted. If the page asks for microphone
   permission, answer as you normally do. Note which microphone you expect to be used; the first `MIC`
   lines say which is Windows' default.
3. Unmute with the page's own button, say a few words (five seconds is plenty), mute again, and wait
   about ten seconds. Do that three times. Before each click, if you can, press M in the check's
   window and go back to Chrome; otherwise note the clock time of each click.
4. During one unmuted stretch, press the keyboard's microphone-mute key once, wait five seconds, and
   press it again, with M before each press if you can. That is a different kind of mute from the
   page's button, and it shows whether the key flips Windows' own mute.
5. If you leave and rejoin the class, press M before and after, so a rejoin is not read as a mute.
6. At the end press Q in the check's window. It prints why it stopped, how many looks it made, what
   it cost the laptop, and where the log is.

The log holds the names of your microphones and of the programs that touched the microphone, with
times. It holds no sound and nothing that was said.

**What the lines mean.** The first lines are a header: the build, when the check started, its own
process id, what it reads, and the log's path. After that every line starts with the local time to
the millisecond and a word for its kind. Nothing is written while nothing changes, so a quiet class
page gives a quiet log, which is a result in itself.

- `MIC` lines are about one microphone: `at start` for how things were at the start, later `became
  active` (plugged in or enabled), `changed`, or `no longer active`. `Windows default` lists the
  roles for which it is Windows' default (Console for most programs, Multimedia, and Communications
  for calls, or `no`); `muted in Windows` is the switch a microphone key or the Sound settings flip,
  `YES` when muted; `input volume` is its level in Windows. A changed value is followed by `(was ...)`.
  What Windows would not give about it follows as `problem: ...`, such as `problem: session 2 of 3
  (Zoom, pid 9090) not readable: GetState: The parameter is incorrect. (0x80070057)`: whose session
  it was, when Windows said, and which call failed.
- `SESSION` lines are about one program's session on one microphone, which is what Windows gives a
  program when it opens the microphone: `#4 chrome (pid 24800) on Microphone Array (...): appeared:
  state: Active; session muted: no; program C:\...\chrome.exe; session id ...`, later `changed:
  state: Inactive (was Active)` or `gone (Windows no longer lists it)`. `Active` means the program is
  taking sound from the microphone right now, `Inactive` that it holds the microphone open but takes
  none, `Expired` that it has closed it. The sessions of `Windows' system sounds` are always there;
  ignore them. A ClassScribe recording running beside the check shows as `ClassScribe, a recording in
  progress (pid ...)`, which is expected.
- `PRIVACY` lines come from Windows' usage record: `Windows settings: microphone access: Allow;
  desktop apps: Allow`, each program's state at the start, then `STARTED using the microphone at ...`
  and `STOPPED using the microphone at ..., after 21.8 s`, with Windows' own times; `used the
  microphone briefly between two checks` for a use shorter than a look; `listed with no use recorded`
  names the programs that have an entry but never used the microphone.
- `MARK` lines are your M presses, numbered. `NOTE` lines are the check's own remarks, such as `no
  check for 18.0 s: the laptop slept or the check was held up, so a change that came and went in that
  time is not in the log.`, or that a list can be read again. `ERROR` lines mean something could not be
  read (`Windows' list of microphones could not be read: ... The check keeps trying.`). `SELF` lines
  should never appear: they would mean ClassScribe's own process was seen using the microphone.

**What the log decides.** At your mute and unmute clicks:

- A `SESSION` line for chrome going `Inactive`, `Expired` or `gone` on mute and back to `Active` (or
  `appeared`) on unmute, with `PRIVACY` lines `STOPPED` and `STARTED` to match: the page lets go of the
  microphone when you mute. Set `Recording:ClassAppMute` to `Visible`, and ClassScribe follows your
  mute by itself.
- Nothing between your `MARK` lines (chrome's session stays `Active`): the page keeps the microphone
  open while you are muted. Set `Invisible`: the microphone starts off, V switches it on, and the
  Windows mute is the one ClassScribe follows exactly.
- A `MIC` line with `muted in Windows: YES` at the key press and `no` at the next: the microphone key
  flips Windows' own mute, so it is the one mute to use. No `MIC` line at the press: the key is handled
  by the laptop's own software; use Sound settings > Input > Mute, or V.
- A `SESSION` line naming a different microphone than you expected: the page and ClassScribe may not
  agree on the microphone; ClassScribe switches to the page's when they do not.

### YouTube videos

Menu choice Y, or the `youtube` command, takes one YouTube video or a list of them, makes each an
ordinary recording of the course you name, and transcribes it like a class. There are two ways to
get a video's audio:

- **Record it as it plays** (R in the menu; `youtube` without `--download`). ClassScribe opens each
  video in a Chrome window of its own, you press play there, and it records that window's sound
  alone until the video ends. Nothing is downloaded, it works for any video you can watch, your
  course's own videos included (from their YouTube link), and it takes as long as the videos do. It
  needs nothing but Chrome. See [Recording as it plays](#recording-as-it-plays).
- **Download it** (D in the menu; `youtube --download`). yt-dlp fetches the audio, which is fast,
  but it is only for videos you uploaded or whose owner allows you to download them, and never for
  your course's: ClassScribe refuses what it can tell is your course's, asks for your word on each
  video that is not public, and asks for `YES` on the batch. It needs yt-dlp (step 8 of
  [Set up once](#set-up-once)). Read [YouTube's terms and the law](#youtubes-terms-and-the-law)
  first. See [Downloading with yt-dlp](#downloading-with-yt-dlp).

Either way the batch runs in the window that started it, in three steps. First the plan: every video
is looked at, and what will be done is shown, with the cost, the time and what is refused and why;
nothing is fetched, recorded or sent before you confirm it. Then the fetching: each video becomes a
recording of the course, all of one batch in one date folder, with a `.source.json` beside it that
keeps the video's title, link and how its audio was obtained. Then the transcripts, one after
another, unless you chose only the audio (O, `--no-transcribe`); each goes up to Google Drive as a
class's does. A summary at the end says what came of each video, with what to do about anything
left, and the course's folder opens once (`--no-open` opens nothing). Only one batch runs at a time,
across ClassScribe's windows; the laptop is kept awake for the whole batch; and a class always comes
first (see [A class comes first](#a-class-comes-first)).

```powershell
ClassScribe youtube "https://youtu.be/abcdefghijk" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --yes
ClassScribe youtube --list "risala-links.txt" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --download --plan
ClassScribe youtube --list "risala-links.txt" --name "Risala" --output "$env:USERPROFILE\ClassScribe" --download --yes
ClassScribe youtube "https://www.youtube.com/playlist?list=PL..." --name "Lectures" --output "$env:USERPROFILE\ClassScribe" --download --no-transcribe --yes
ClassScribe youtube check
```

The first records one video as it plays and transcribes it; the second shows the plan of a download
and stops; the third downloads and transcribes what that plan showed as ready; the fourth downloads a
playlist's audio without transcribing it; the last checks yt-dlp and its helpers. In the menu, Y asks
for the same things one at a time.

#### Links and a list file

| Link | Taken as |
| --- | --- |
| `https://www.youtube.com/watch?v=ID` (also with `m.` or `music.`, or without `www.`) | The video. A `t=` in it is kept when it is recorded as it plays, and its window opens there; `list=` and everything else are dropped, so a link from a playlist means that one video. |
| `https://youtu.be/ID`, `https://www.youtube.com/shorts/ID` | The video. |
| `https://www.youtube.com/live/ID` | The video, with `--download` (D) only, whose read tells a live stream from a finished one. |
| `https://www.youtube.com/playlist?list=PL...` | Every video of the playlist, with `--download` (D) only. |
| A channel's link (`/@name`, `/channel/...`, `/c/...`, `/user/...`) | Refused: a channel's uploads can be hundreds of hours. Give a playlist's link, or the videos' own. |
| An embedded player's link (`/embed/ID`, `youtube-nocookie.com`) | Refused: it was most likely copied from a web page such as your course's, which ClassScribe never reads. Use the YouTube button on the player for the video's own link. |

The `https://` may be left out. A watch link copied with that YouTube button says so in its address
(`embeds_referring_euri`, `embeds_referring_origin` or `feature=emb_...`): recording as it plays takes
it, as it takes any video you can watch, but a download never reads or downloads it (`copied from a
player embedded in a web page: never downloaded`). Every link is rebuilt from the video's ID before
it reaches Chrome, yt-dlp, a file or a transcript, so nothing you type reaches them as you typed it.
Recording as they play takes at most 10 videos at a time, each by its own link; downloading takes at
most 500, playlists included, once duplicates are taken out.

A list file is a text file (UTF-8, with or without a byte-order mark) with one or more links on a line,
separated by spaces; `#` at the start of a line, or after a space, starts a comment that runs to the
end of the line, and blank lines are skipped:

```text
# Risala, the creed
https://youtu.be/abcdefghijk    # part 1
https://youtu.be/bcdefghijkl https://youtu.be/cdefghijklm
https://www.youtube.com/playlist?list=PLxyz
```

Give it with `--list "<file>"`, alone or beside links on the line (they are joined in order), or drag
it onto the window when the menu asks for links. A video given twice is taken once
(`Line 3 of risala-links.txt lists the video abcdefghijk again; it is taken once`). A word that is no
link the batch takes is named by where it stands, with why, such as
`Line 4 of risala-links.txt: "..." is not a YouTube link.`: on the command line that is a mistake
(exit code 2, and nothing starts); in the menu it is left out (`Left out: ...`) and the rest go on.

#### Recording as it plays

After the plan, Y in the menu (or `--yes` on the command line) starts. ClassScribe opens a video's
window only when you ask: in the menu Y opens the first at once; before every other video, and before
each on the command line, it asks
`Next: 02 The creed, part 2. Enter opens it; S stops here and goes on to the transcripts.` The window
is ClassScribe's own Chrome, with a profile of its own (`%LOCALAPPDATA%\ClassScribe\video-browser`),
so your everyday Chrome is never in it: one window on the video's page, without tabs or an address
bar. ClassScribe never makes it play: press play there (on a fresh profile Chrome lets a page play
sound only after a click on it). While it plays you may pause it, skip an ad with YouTube's own
button, or answer YouTube's "Continue watching?" question, which it may ask after about an hour without
a click; ClassScribe never clicks or reads anything in the page. The recording starts as the window
opens, so it begins with the moment before you press play, and its positions run from there.

What is recorded is that window's sound alone: Chrome's process and everything it started, through
the same Windows process loopback `--app chrome` uses, so your everyday Chrome and its tabs stay
out, and a class recorded with `--app chrome` leaves out a video window left open. Ads are recorded
and transcribed with the video, unless you are signed in there with YouTube Premium (see
[Your Premium sign-in in the video window](#your-premium-sign-in-in-the-video-window)). The
transcript's positions are positions in the recording, so ads and pauses shift them from the
video's own times. Keep YouTube's player volume and Chrome's volume in Windows' volume mixer up:
lowering them lowers the recording. Whether the speakers' own volume and mute reach the recording
has not been checked on this laptop yet, so until it has, listen through headphones at night rather
than turning the laptop's volume down.

The run's first lines say which video it is and what to do: `Recording the YouTube video "The creed,
part 1" (1 of 3) as it plays in ClassScribe's video window, into ...`, then `Press play in the video
window. The recording stops when the video ends or its window is closed; Q twice stops it sooner, P
pauses or resumes, M marks a moment. ...`. A video's recording ends in one of these ways, looked at
once a second:

| What happens | What the log says | Its end |
| --- | --- | --- |
| You close the video's window | `The video window was closed; stopping the recording` | Confirmed |
| The window moves on to the next video, as YouTube's Autoplay does when one ends: one title for at least 5 seconds with sound, then another for two looks | `The window moved on to another video ("..."); the recording of "..." stops here` | Confirmed |
| Q twice in the window that shows it | as for a class | Confirmed: your choice, even midway |
| A class starts (see [A class comes first](#a-class-comes-first)) | `A class is starting, so this video's recording gives way to it` | May be cut short |
| 2 minutes of silence after sound, never counted while paused with P | First `Nothing has played for 60 seconds; the recording stops at 2 minutes unless it plays again. Press P here to pause the count` (at `Recording:SilenceWarningSeconds`, at most 60, with `nothing plays` in the title), then `Nothing has played for 2 minutes; the video has most likely ended, or YouTube paused it` | May be cut short |
| Nothing at all in the first 5 minutes | From 30 seconds `Nothing plays in ClassScribe's video window yet: press play there. If it plays, check that YouTube's player and Chrome's volume in Windows' mixer are up` (`press play in the video window` in the title); at 5 minutes `Nothing has played in ClassScribe's video window for 5 minutes; stopping` and `Nothing played in ClassScribe's video window, so nothing of "..." was kept to transcribe` | Nothing recorded |
| `YouTube:LongestVideoHours` (4) of recording | `This video has played for 4 hours (YouTube:LongestVideoHours); stopping`; the summary gives the command that records the rest, from where it stopped | May be cut short |
| The safety stop, `Recording:StopAfterHours`, when it is the lower of the two | as for a class | May be cut short |

Leave YouTube's Autoplay on: its move to the next video is the clearest end ClassScribe can see.
With it off, a video ends when you close its window or press Q twice, or else by the 2 minutes of
silence. A second or two of the next video, or of its ad, may be at the end of a recording before
the new title is seen. YouTube's "Continue watching?" question pauses a long video, which the
2 minutes of silence then take for its end, so stay nearby, as for a class.

At the stop the log says how it ended (`"The creed, part 1": the window moved on to the next video`),
and the window is asked to close, as its close button does, never killed; the next opens on your
Enter. A recording that **may be cut short** is kept and transcribed like any other, and the summary
says so (`recorded (00:58:10), but ended after 2 minutes of silence, so it may be cut short; the same
command records it again`): the same command run again records that video again, as a new recording,
and leaves the old one as it is. A video recorded with a confirmed end is done.

| Key | While a video records | Between videos |
| --- | --- | --- |
| Q twice within 5 s | Stops this video's recording; its end counts as confirmed. | nothing |
| P | Pauses and resumes the recording and the count of the 2 minutes of silence; pause the video in its window too. | nothing |
| M | A mark, as in a class. | nothing |
| Enter | nothing | Opens the next video, once no class records. |
| S | nothing | Stops opening videos; the transcripts of those recorded follow. |
| Esc | Leaves the view: the video records on in the background to its end, its window open until then; no more videos open; the others are transcribed, and this one is left for the same command later. | As S. |
| Ctrl+C twice within 5 s | Stops this video and the batch, with no transcripts (exit code 130). | Stops the batch, with no transcripts (exit code 130). |
| Closing the window | With the published copy the video records on in its background recorder to its end, and its run closes its window; nothing else goes on, and the same command later transcribes it. Under `dotnet run` the recording ends with the window. | The batch ends. |

During the transcripts that follow, Q stops after the transcript in hand (Q twice within 5 seconds
stops at once), and one Ctrl+C cancels the upload in hand, as `transcribe` does.

Things that can go wrong: Chrome not found refuses the batch before anything opens. A video window
left open from an earlier run takes the next video in its place
(`ClassScribe's video window from an earlier run is still open, so Chrome sent the video there. Close every ClassScribe video window, then press Enter to try again`).
A window that does not open, or a run that fails before it records, is that video's alone, but two
in a row stop the videos (`Two videos in a row could not be recorded, so no more windows open:
something is wrong with Chrome. The errors above say what`). A video's recorder lost in the middle is
never continued: `The recorder of the YouTube video "..." ended unexpectedly at 19:47:12. Its audio
so far is kept; a video is never continued, so the same command records it again.`; its audio so far
is transcribed as a recording that may be cut short, and the next video is offered.

#### Downloading with yt-dlp

A download batch first finds yt-dlp, Deno, FFmpeg and FFprobe and asks their versions, as
`youtube check` does, then reads each playlist from its listing and each video, at most one read
every 6 seconds (200 videos take about 20 minutes, which it says first:
`Reading 200 videos from YouTube takes about 20 minutes; Q stops`). A video found done before is not
read again (see [The same command again](#the-same-command-again)). Each video read gets the first of
these verdicts that applies:

| What yt-dlp's read says | The plan's verdict |
| --- | --- |
| Its channel, by its UC id, @handle or channel address, is in `YouTube:CourseChannels` | `your course's channel: never downloaded` |
| It needs a sign-in: private, members only, Premium only, or restricted by age | `needs a sign-in: left out`: ClassScribe never signs in |
| A live stream, one still to come, or one still being processed | `not finished yet: left out` |
| No length | `length unknown: left out` |
| Longer than 9 hours 30 minutes, which ElevenLabs would refuse in one file | `longer than 9 h 30 min: left out` |
| `YouTube:CourseChannels` names channels, but the read names no channel to check against them | `channel unknown: needs your word` |
| Its channel is in `YouTube:MayDownloadChannels` | `ready (YouTube:MayDownloadChannels)` |
| Public | `ready` |
| Unlisted, or not said to be public | `needs your word` |

A video whose link came from an embedded player is never read: `copied from a player embedded in a
web page: never downloaded`. Unlisted videos need a word of their own because course pages embed
unlisted videos, while your own uploads and many public lectures are unlisted too. The guard has
limits you should know: ClassScribe cannot see where a link was copied from (unless the link itself
says so), a course may use videos on a channel that is not in `YouTube:CourseChannels`, and the word
you give is yours, kept in the video's `.source.json`, not something ClassScribe can check.

The plan, in the menu as on the command line:

```text
Read 12 videos from 2 links (1 playlist, "Risala, the creed"):
  #   Title              Channel       Length    Availability  Verdict
  01  The creed, part 1  Your Channel  01:02:33  public        ready
  02  The creed, part 2  Your Channel  00:58:10  unlisted      needs your word
  03  Live Q&A           Other         -         public        not finished yet: left out
  04  The creed, part 4  Your Channel  01:01:12  public        already transcribed on 12 Oct: Risala_2026-10-12_14-02-11Z.md
  ...
Ready: 9 videos, 9 h 41 min: about $2.61 to transcribe ($0.22 an hour plus $0.05 for the glossary of 75 terms).
Waiting for your word: 1 video, 58 min: about $0.26 to transcribe ($0.22 an hour plus $0.05 for the glossary of 75 terms).
ElevenLabs account: 43,362 of 100,000 credits used this period; 56,638 left until 1 November.
9 downloads: about 12 minutes, 10 to 20 seconds apart, depending on your connection; then the transcripts, a few minutes each.
Room: about 2.9 GB on C:\ for the audio (mono, at YouTube's own rate), with 41 GB to spare.
Google Drive: everything, the audio included (YouTube:DriveUpload), about 2.9 GB of audio; Drive has 11 GB free.
Channels: Your Channel @yourchannel (add one to YouTube:MayDownloadChannels if it is yours, or its owner allows downloading).
```

With nothing ready the line is `Ready: 0 videos.`, and with O (`--no-transcribe`) the cost ends
`. Nothing is sent to ElevenLabs.` The room is the ready videos' audio at each one's own rate, with
the largest download beside it, and room kept on the recordings folder's drive for a 3-hour class
after the batch (about 1.3 GB), so that a large batch never leaves the next class short; when that
does not fit, the plan says `There is not room enough for the audio and its download while keeping
room for a 3-hour class (about 1.3 GB): free some room, or download fewer videos at a time.` and
nothing is downloaded (exit code 1). The Drive line says `; Drive has only ... free, so what does not
fit waits in the queue until there is room.` when it would not all fit, gives no room when Drive is
not set up or cannot be asked, and with `YouTube:DriveUpload` set to `WithoutAudio` reads
`Google Drive: the transcripts, answers, logs and source files, without the audio (YouTube:DriveUpload).`

In the menu, each video that needs your word is asked about next:

```text
02  The creed, part 2 (Your Channel, 00:58:10) is unlisted. Did you upload it, or does its owner allow you
    to download it? It must not be one of your course's videos.
    Y downloads it   N leaves it out (it can be recorded as it plays instead)   Esc goes back
```

Y makes it `ready (your word)`, N `left out: you said no`. Once you have answered, the ready, cost,
time, room and Drive lines are shown again for what is now ready, and a batch that no longer fits
is refused. Then:

```text
Download the audio of 10 videos with yt-dlp?
Only videos you uploaded, or whose owner allows you to download them, and never your course's videos.
YouTube's terms allow downloading only where YouTube offers it (such as YouTube Studio, for your own uploads)
or with written permission from YouTube and the rights holder; beyond that the choice is yours.
ClassScribe never signs in to YouTube and never gives yt-dlp your browser's cookies.
Type YES and press Enter to go ahead; Enter alone goes back.
```

`YES` must be typed in full, in capitals, because this is where you take on the question of YouTube's
terms. When nothing needs downloading but recordings fetched before wait for their transcripts, it
asks `Nothing to download. Transcribe the recordings fetched before? Y starts; any other key goes
back.` instead.

On the command line nothing is asked: the same plan is printed, `--yes` stands for `YES` and takes
only the videos that need no word, and `--plan` stops after the plan (exit code 0, even when the room
does not fit). A video that needs your word is then left out, and the summary says how to give it:
`needs your word: in the menu (Y > D), or add --allow-unlisted, or its channel @yourchannel to
YouTube:MayDownloadChannels if it is yours; or record it as it plays: <command>`.

**`--allow-unlisted`** gives your word once, on the command line, for every video of the batch that
yt-dlp says is unlisted and whose channel its read names: you say each is a video you uploaded or
whose owner allows you to download it. Use it only when you know that of every unlisted video in the
batch. The plan shows such a video as `ready (--allow-unlisted)`, and its `.source.json` records
`"acceptedBy": "allow-unlisted"`. Because course pages embed unlisted videos, it is refused while
`YouTube:CourseChannels` is empty (exit code 2): `--allow-unlisted needs your course's channels: list
them in YouTube:CourseChannels first, so that a course video is never taken for one you may download`.
It never overrides another refusal (your course's channel, a sign-in, a live stream, a length that is
unknown or too long, a link from an embedded player, a channel that is not known), never takes a video
yt-dlp only fails to call public, and goes with `--download` alone. Without `YouTube:CourseChannels`
filled in, the summary's advice for such a video says to fill it in first.

**`YouTube:CourseChannels` and `YouTube:MayDownloadChannels`** are lists of channels in
`appsettings.json`, each entry a channel's UC id (`UC` and 22 more letters, digits, `_` or `-`) or its
@handle (`@` and 3 to 30 letters, digits, `_`, `-` or `.`):

```json
"YouTube": {
  "CourseChannels": [ "UCxxxxxxxxxxxxxxxxxxxxxx" ],
  "MayDownloadChannels": [ "@yourchannel" ]
}
```

Put your course's channels in `CourseChannels`: their videos are never downloaded, and while it names
channels, a video whose read names no channel needs your word. Prefer the UC id there, which never
changes, while a channel may change its @handle; a channel's page shows its @handle under its name,
and its UC id is in its address when it has no handle (`youtube.com/channel/UC...`) and under its
About, then Share channel, then Copy channel ID. Put your own channel, and those whose owners allow
you to download their videos, in `MayDownloadChannels`: their videos are ready under the batch's
`YES`, unlisted ones too. A channel in both lists is refused. The plan's `Channels:` line names the
channels of the videos that wait for your word, with their @handles.

The downloads then go one at a time, after any class that records, 10 to 20 seconds apart and at
most 60 begun in an hour, each from its read when that is less than 5 hours old (a download YouTube
refuses with HTTP 403 is read again and tried once more), with its progress every tenth
(`"The creed, part 1": 40% downloaded`). yt-dlp takes the best audio YouTube offers (normally AAC at
44.1 kHz) and never keeps the video; FFmpeg makes it a mono WAV at YouTube's own rate (usually 44.1
or 48 kHz), about 318 MB an hour at 44.1 kHz, and ClassScribe writes it into the recording as its own
`.wav`, split as a class's is at `Recording:MaxSegmentMegabytes`. The upload to ElevenLabs is the
usual 16 kHz copy. The audio must be within 2 seconds of the video's length, or nothing of it is kept
(`The audio of "..." is ... long, but the video is ...: the download is incomplete, so nothing of it
was kept. ...`). A video's recording is made only once its audio is in, so a date folder never holds a
half-downloaded video.

As the downloads begin the window says `Q stops after the video in hand; Q twice within 5 seconds, or
Ctrl+C, stops at once.` Q once stops after the video in hand, and the transcripts of what was fetched
still follow (a Q during the transcripts stops after the transcript in hand). Q twice, or Ctrl+C,
stops at once with exit code 130: yt-dlp and everything it started end, and its partial download
waits for the next try. Closing the window ends them too.

When yt-dlp fails, its words are matched to what to do:

| What yt-dlp met | What ClassScribe says | The batch |
| --- | --- | --- |
| YouTube's bot check | `YouTube asked to confirm you are not a bot. Wait a day or more before downloading again; ClassScribe never signs in or uses your browser's cookies. These videos can be recorded as they play instead.` | Stops downloading |
| A rate limit, a captcha, HTTP 429 | `YouTube is limiting this connection for up to an hour. Run the same command later; what is done is skipped.` | Stops downloading |
| Deno or FFmpeg missing | `yt-dlp is missing a helper (Deno or FFmpeg): ClassScribe youtube check says which.` | Stops downloading |
| ClassScribe's options refused | `yt-dlp did not accept ClassScribe's options; it may be too old or much newer: ClassScribe youtube check.` | Stops downloading |
| Windows would not start yt-dlp | `Windows did not start yt-dlp (...). Bitdefender may have blocked it (README, Set up once); ClassScribe youtube check says more.` | Stops downloading |
| No audio format | `yt-dlp could not get this video's audio, most often because it is out of date: winget upgrade --source winget --id yt-dlp.yt-dlp; if winget finds nothing newer, yt-dlp --update-to nightly.` | Next video |
| A sign-in needed | `It needs a sign-in, which ClassScribe never does. If you can watch it, record it as it plays.` | Next video |
| Not shown here, or gone | `It is not shown in your country.`, `It is gone from YouTube, or not available.` | Next video |
| YouTube out of reach | `YouTube could not be reached; run the same command again.` | Next video; three in a row stop downloading |
| Anything else | `yt-dlp said: ...`, without any advice about cookies | Next video |

What was downloaded before a stop is still transcribed. The summary lists what was not fetched, with
what to do: a video the download path may not take, or could not, but which you can watch (one that
needs your word or that you said no to, your course's, one from an embedded player, one that needs a
sign-in, or one the bot check stopped) is given the command that records it as it plays instead
(`...; record it as it plays instead: <command>`). It ends with `Run the same command again to fetch
what is left; what is done is skipped.`

**Keeping yt-dlp current.** YouTube changes often, and an old yt-dlp fails. Update it with
`winget upgrade --source winget --id yt-dlp.yt-dlp`. `youtube check` and every download batch warn
once it is more than 90 days old, the age at which yt-dlp itself warns:
`yt-dlp 2026.08.19 is 91 days old, and YouTube changes often, so a download may fail: update it with
winget upgrade --source winget --id yt-dlp.yt-dlp`. When a download fails for want of an audio format
and winget finds nothing newer, `yt-dlp --update-to nightly` takes yt-dlp's newest nightly build
(winget's record of the version then lags behind). ClassScribe never updates yt-dlp itself.
`youtube check` prints each tool's name, version and path, what is missing with the winget command
that installs it, and `yt-dlp and its helpers are in place.` when nothing is; it asks nothing of
YouTube. yt-dlp is found where `YouTube:YtDlpPath` says, else in winget's links folder
(`%LOCALAPPDATA%\Microsoft\WinGet\Links`), else on PATH as Windows holds it now, so a window opened
before the install finds it too; Deno, and FFmpeg with FFprobe, beside yt-dlp first, then the same
places.

#### Your Premium sign-in in the video window

ClassScribe's video window has a Chrome profile of its own, `%LOCALAPPDATA%\ClassScribe\video-browser`,
which Chrome makes the first time a video's window opens, apart from your everyday Chrome and its
sign-ins. Signed out, YouTube shows ads there, and they are recorded and transcribed with the video (a
minute of ads costs less than half a cent; you may skip one with YouTube's own button). With YouTube
Premium, sign in there once and the ads are gone:

1. Close every ClassScribe video window.
2. In PowerShell, open that profile in Chrome (if Chrome is elsewhere, use the path of its
   `chrome.exe`, as `YouTube:BrowserPath` would name it):
   ```powershell
   & "C:\Program Files\Google\Chrome\Application\chrome.exe" --user-data-dir="$env:LOCALAPPDATA\ClassScribe\video-browser" https://www.youtube.com
   ```
3. Sign in to YouTube there with the account that has Premium, then close the window.

You can also sign in with the Sign in button at the top of YouTube's page in a video's window. The
sign-in holds for every video window after it. To end it, sign out in a video window, or delete the
`video-browser` folder while no video window is open.

Once you are signed in, that folder holds your Google sign-in, so keep it private, as you would a
password. ClassScribe never reads, lists, copies, uploads or logs anything in it, and never puts it on
Google Drive; it only names the folder to Chrome when it opens a video's window, and keeps that
command line out of its logs. yt-dlp is never given it: downloads never sign in and never use cookies,
from this profile or any other. Keep it out of anything you copy or share, such as a zip of
`%LOCALAPPDATA%\ClassScribe`.

#### The same command again

Every video is looked for, by its ID, among the `.source.json` files under the recordings folder, so
the same command run again, after a crash, a closed window, YouTube's bot check or ElevenLabs'
refusal, picks up where it stopped, and nothing is fetched or billed twice unless you ask. A video is
**done** when a recording of it holds the whole video: a download, or a recording whose end was
confirmed. Then:

- Done, with a transcript: left out, as `already transcribed on 12 Oct: Risala_2026-10-12_14-02-11Z.md`.
- Done, without a transcript: not fetched again (`fetched before, not transcribed yet: ...`), and
  transcribed in this batch when transcripts are asked; one whose paid answer from ElevenLabs is kept
  gets the free `rebuild` command instead.
- Not done (no audio, or a recording that may be cut short, transcribed or not): fetched again as a
  new recording, the old one staying as it is, as
  `fetched before (Risala_2026-10-18_14-02-11Z), but ended after 2 minutes of silence, so it may be cut short: fetched again`.
- With `--again`: every video is fetched again, as new recordings; transcribing them is billed again.

Each recording is looked at again just before its transcript, since the plan may be hours old: one
transcribed meanwhile in another window is not sent again
(`"The creed, part 1" was transcribed meanwhile (...); it is not sent again`), nor is one whose paid
answer is kept, or one being recorded or transcribed elsewhere. Each transcript a batch makes is in
the run files while it is made, so `transcribe` and menu choice 3 in another window refuse to send it
too, the menu's header shows it with its upload's percent, and a window closed in the middle leaves it
to the next start's report as an interrupted transcript.

When ElevenLabs refuses the key (HTTP 401 or 403), the credits run out (402), or the account is rate
limited (429), every transcript left would fail the same way, so the transcripts stop:
`The transcripts stopped: ...`, then `Not transcribed: "..." (...), .... Once that is fixed, run the
same command again: what is done is skipped, and these are transcribed`. The audio is all kept. Any
other failure is that video's alone, and the next goes on.

Only one batch runs at a time, across ClassScribe's windows (`youtube.lock`). A second is refused,
with exit code 1: `Another ClassScribe window (process 1234) is working through YouTube videos; wait
for it to finish, or close it`. The lock goes with the window, so a closed window frees it.

#### A class comes first

- While a video records, a class started anywhere (menu choice 1 or 2, `record`, or C at the start)
  stops it and records: `Stopping the YouTube video "The creed, part 1" so that "Kalam" can record;
  what it recorded is kept.` The video's recording may then be cut short, and the same command
  records it again later. If the video has not stopped within 30 seconds, or cannot be asked to (a
  recorder of an older build), the class is refused as any second recording is, with what to do:
  `Only one recording at a time: the YouTube video "The creed, part 1" has been recording since 19:00
  (00:12:13). A shows it.`, then `Press Q twice in the window that shows the video to stop it, then
  start the class again.` (or, when it could not be asked, `It could not be asked to stop: press Q
  twice ...`).
- Before each video and each transcript, the batch waits while a class records, looking every 30
  seconds: `YouTube: waiting while "Kalam" records; the videos go on after it`, then `YouTube: going
  on, now that "Kalam" has stopped recording`. Nothing reaches YouTube or ElevenLabs from the batch
  meanwhile, and the next video's window opens only on your Enter once the class has ended. Uploads
  to Google Drive wait for a class as always.
- A video is never taken for a class: `record "Risala"` never shows a video's run, C (at the start,
  in the menu, or `--continue`) never continues a video, and a video's recorder that is lost is never
  continued.
- One date folder per batch: the batch's first video takes the day's free date folder as a class
  does, the others join it, and the day's class after it goes to `(2)`. A downloaded video's
  recording is made only once its audio is in, and a folder with a conversion in progress counts as
  taken, so a class that starts during the first download keeps its own folder.
- A download batch keeps room for a 3-hour class on the recordings folder's drive (see the plan,
  above).

#### Where a video's files go

```
%USERPROFILE%\ClassScribe\
    Risala\
        2026-10-17\                                  a class
        2026-10-18\                                  a batch: its videos, in the order given
            Risala_2026-10-18_14-02-11Z.wav          the first video's audio (downloaded: YouTube's rate, mono)
            Risala_2026-10-18_14-02-11Z.source.json  the video's title, link, channel and how its audio was obtained
            Risala_2026-10-18_14-02-11Z.log
            Risala_2026-10-18_14-02-11Z.md           its transcript, headed by the video's title and link
            Risala_2026-10-18_14-02-11Z.json
            Risala_2026-10-18_14-03-40Z.wav          the second video
            ...
        2026-10-18 (2)\                              the day's class, after the batch
```

Each video is an ordinary recording of the course: `list` and menu choice 3 show it with the video's
title and how its audio was obtained (`"The creed, part 1" (YouTube, downloaded)`, or `(YouTube,
recorded)`), and `transcribe`, `rebuild` and Google Drive treat it as any recording. `transcribe`
given the batch's date folder takes its newest recording only, so to transcribe one video in it, give
that video's `.wav`. The `.source.json` of a download looks like this:

```json
{
  "kind": "youtube",
  "url": "https://www.youtube.com/watch?v=abcdefghijk",
  "videoId": "abcdefghijk",
  "title": "The creed, part 1",
  "channel": "Your Channel",
  "channelHandle": "@yourchannel",
  "channelId": "UCxxxxxxxxxxxxxxxxxxxxxx",
  "uploadDate": "2025-03-03",
  "duration": "01:02:33",
  "availability": "public",
  "playlist": { "id": "PLxyz", "title": "Risala, the creed", "position": 1 },
  "obtained": "downloaded",
  "tool": "yt-dlp 2026.08.19",
  "obtainedUtc": "2026-10-18T14:02:11+00:00",
  "confirmed": "You typed YES for the batch (18 Oct 2026 14:00 UTC)",
  "acceptedBy": "yes",
  "ended": "Downloaded",
  "endConfirmed": true
}
```

`acceptedBy` is `yes` (a public video under `YES` or `--yes`), `word` (your word in the menu),
`allow-unlisted` or `may-download-channel`. A recording made as it played has `"obtained":
"recorded"`, Chrome's version as its `tool`, no upload date, length, availability or playlist, and in
`ended` and `endConfirmed` how its recording ended and whether that end is confirmed.

A downloaded video's transcript starts:

```markdown
# The creed, part 1

Downloaded 18 October 2026, 14:02 UTC.

From YouTube: [The creed, part 1](https://www.youtube.com/watch?v=abcdefghijk), by Your Channel, uploaded 3 March 2025, 01:02:33 long, number 1 in the playlist "Risala, the creed"; downloaded with yt-dlp 2026.08.19. Positions are the video's own, and each links to that moment on YouTube.

[[00:00:00]](https://www.youtube.com/watch?v=abcdefghijk&t=0s) ...
```

Each `[hh:mm:ss]`, of a paragraph or a mark, links to that moment on YouTube, while
`Transcript:IncludeTimestamps` is on. A recording made as it played says instead `Recorded ...` and
`From YouTube: [The creed, part 1](...), by Your Channel; recorded as it played in ClassScribe's window
(01:04:10). Positions are in the recording, so ads and pauses shift them from the video's own times.`,
and its positions are plain. A free rebuild keeps the heading.

#### What it costs, and Google Drive

- **Transcripts** cost what a class's do ($0.22 an hour, plus $0.05 with the glossary). A download's
  plan gives the cost from the videos' lengths; recording as they play, the lengths are not known
  before the videos play, so the plan gives the most it can cost, with each video stopped at
  `YouTube:LongestVideoHours`: `Each is recorded for as long as it plays, at most 4 hours
  (YouTube:LongestVideoHours), ads included, so at most 12 hours for these 3: they cost at most $3.24
  to transcribe, most likely much less ($0.27 for each hour recorded). The transcripts follow the
  recordings.` The moment before you press play, and any ads, are billed at the same rate.
- **The account.** When transcripts are asked, the batch checks for the key before it asks YouTube
  anything or opens a window (without it: `No ElevenLabs API key found in ELEVENLABS_API_KEY. Set it (README, Set up once), or add
  --no-transcribe to fetch only`, exit code 1) and the plan gives the account's credits, as
  `ElevenLabs:ReadAccountUsage` allows. After the first transcript one line projects the rest, in the
  account's own credits: `The first transcript used 5,200 credits for 01:02:33; the other 8 (8 h 39
  min) would use about 43,268 at that rate; 56,638 are left this period`. Each transcript also logs
  its own cost lines, as a class's does (see [Cost and privacy](#cost-and-privacy)).
- **Google Drive.** A video's recording goes up as a class's does, once Drive is set up: its `.wav`
  too while `YouTube:DriveUpload` is `Everything`, the default, or all but the audio with
  `WithoutAudio`, which keeps the `.wav` on the laptop alone. A downloaded video goes up right after
  its transcript; one whose transcript was not made waits in the queue, and with `--no-transcribe`
  all of them go up in one pass at the end. A video recorded as it played goes up from its recorder
  after its recording, and its transcript's files after the transcript. `drive free-space` leaves a video's audio that never went up where it is, and says so
  (see [Free room on the laptop](#free-room-on-the-laptop)).

#### YouTube's terms and the law

Downloading a YouTube video is allowed by YouTube's terms only where YouTube itself offers it, such as
YouTube Studio's Download for videos you uploaded, or with written permission from YouTube and the
rights holder ([YouTube's Terms of Service](https://www.youtube.com/t/terms), Permissions and
Restrictions; [Download your own YouTube videos](https://support.google.com/youtube/answer/56100)).
The owner's permission alone does not meet the letter of those terms, and yt-dlp has to solve
YouTube's player challenges to download at all, which a German court has treated as getting around a
technical protection measure; in the United States the question has not been settled in court. So
`--download` is for videos you uploaded or whose owner allows it, it asks you each time, and the
choice is yours. For your own uploads, YouTube Studio's Download followed by `transcribe <file>` stays
within the terms. Recording a video as it plays downloads nothing: you play it, as you would in your
browser. Your course's videos are never downloaded.

The law, in brief:

- Germany: the Hamburg courts held that youtube-dl, yt-dlp's predecessor, gets around an effective
  technical protection measure when it solves YouTube's signature challenges, in the music labels'
  case against its hosting provider (Landgericht Hamburg, 2023, upheld by the Oberlandesgericht
  Hamburg on 21 November 2024, 5 U 54/23;
  [TorrentFreak](https://torrentfreak.com/music-labels-win-legal-battle-against-youtube-dls-hosting-provider-230404/),
  [heise online](https://heise.de/-10179284)). That is German law, about music recordings.
- United States: unsettled. In October 2020 the RIAA had youtube-dl taken down from GitHub under the
  DMCA's anti-circumvention section; GitHub put it back on 16 November 2020 after the EFF argued that
  youtube-dl gets around nothing, since YouTube serves the streams to anyone who asks
  ([GitHub's account](https://github.blog/2020-11-16-standing-up-for-developers-youtube-dl-is-back/)).
  That was GitHub's decision on a notice, not a court's ruling.

Neither decides anything for your own uploads or for lectures whose owners allow downloading.

## Dry run before the first real class

Take two minutes to check the whole path on your machine. To try settings without touching your
own, first run `$env:CLASSSCRIBE_SETTINGS_FOLDER = "$env:TEMP\ClassScribe-dry-run"` in the
PowerShell window you start ClassScribe from: it then reads and writes its settings in that folder
alone, and the menu's header names it. Close that window afterwards, or run
`Remove-Item Env:CLASSSCRIBE_SETTINGS_FOLDER`.

1. Start ClassScribe, press 2 and type a name, such as `Dry run`.
2. Play any video with speech for 30 seconds.
3. Press Q twice and read the transcript when it opens.
4. If you will use your microphone: press 7, W, Enter, then type `chrome` and Enter to follow the
   class page (Enter alone follows any app that holds the microphone), and any key but S, so that
   the dry run's pick is not kept as your default; press 2 and a name; press V when the start lines say the microphone starts switched off; say a few words
   while the video plays; press Q twice and check that the transcript holds both. For a class in the
   room, 7 then I, then 1: speak, stop, and play the `.wav` file back. Run the microphone check once
   during your first real class, as above.
5. Once Google Drive is set up, the dry run goes up there too after its transcript: with the published
   copy, start ClassScribe again after a minute and the header says `Google Drive: signed in; nothing
   waits to go up.`, and the `.log` beside the recording holds the line that names its folder,
   `My Drive > ClassScribe > Dry run > <today's date>`. Delete it in Drive afterwards if you like;
   ClassScribe never deletes anything there.
6. Before the first batch of YouTube videos, record one short video as it plays, ideally one of
   your own a few minutes long: press Y, paste its link, type `Dry run`, press R, then T, and Y at the
   plan; press play in the window that opens, and let YouTube's Autoplay move on at its end, or close
   the window. Check that the transcript is headed by the video's title and link. Before the first
   download, run `ClassScribe youtube check`, then a download of one short video you uploaded, with
   `--plan` first. Each reaches YouTube, and each transcript is billed, a cent or so for a few
   minutes.

A new version is tried the same way before it reaches a class, through the dry-run kit: double-click
`Start-DryRun.cmd` in `tools\dry-run` of that version's copy of the code. It runs a copy of that
version beside your installed one, with a stand-in for ElevenLabs on this laptop (nothing is sent or
billed), Google Drive off and your settings untouched, tells you what to do in the window, and checks
the result. `tools\dry-run\README.md` says what it does and what it checks.

## How the recording is protected

- Each packet of audio goes to Windows as it arrives, and the file's header is brought up to date
  every 5 seconds. If the app is killed, or Windows shuts down or signs you out during the class,
  at most a fraction of a second is lost; after a power cut, at most those 5 seconds. Transcribing
  reads such a file to its very end, whatever its header says.
- Pausing with P saves the file first, so a crash during a break loses nothing recorded before it.
- The transcript step starts only after the audio file is closed. If the upload fails, the audio is
  untouched and the class can be transcribed again.
- When the sound moves to another output device (headphones plugged in, Bluetooth dropping),
  ClassScribe follows it within a few seconds and continues in a new segment file. The segments are
  joined before upload.
- If the device fails, it is reopened. While there is nothing to record (no output device, a chosen
  device not connected, a chosen app not playing) or the device cannot be opened yet, ClassScribe
  looks again every second and says so at once, then every 30 seconds. That holds from the very
  start: only a device whose sound format ClassScribe cannot read keeps a recording from starting.
- The once-a-second look at the output devices and the apps lets go of every Windows object it uses
  straight away, so ClassScribe's handles and memory stay flat in Task Manager through a long class.
  Before, each look left a handle behind until .NET tidied up, about 1,000 every 17 minutes.
- If the disk cannot be written, ClassScribe says so and continues in a new file. If writing fails
  again straight away (a full disk, say), it waits 30 seconds before each new try, and the audio of
  that time is lost. A file left with no audio in it is removed. Only audio that reached the disk is
  counted, so the marks stay in step with what is transcribed.
- If Windows holds the capture up for longer than it keeps audio waiting (one second), ClassScribe
  warns that the recording probably has a gap there.
- A capture that stays open but stops handing over audio, which Windows reports in no other way, is
  found within seconds and reopened (see [While recording](#while-recording)).
- The laptop is kept awake while ClassScribe records and uploads, and a time it slept anyway, or
  Windows paused ClassScribe, is logged (see [Keeping the laptop awake](#keeping-the-laptop-awake)).
- If a sound driver hangs as you stop, ClassScribe waits for it at most 20 seconds, then stops
  without it. Everything recorded so far is kept.
- It warns when it has heard nothing for 60 seconds, except while paused.
- A recording of pure digital silence (nothing but exact zeros, as Windows hands over while muted)
  is not uploaded, so it costs nothing.
- With your microphone, the class and the microphone are recorded by separate sessions with their
  own files: a microphone that fails never stops the class, nor the other way round. A track whose
  device sends sound in a form ClassScribe cannot read is left out at the start (`The microphone
  cannot be recorded: ...; the class is still being recorded`, or the reverse), and only when neither
  can be recorded does the start fail. Both tracks follow one clock and are padded with silence where
  nothing arrives, so a reopened capture, a device move or a disk pause stays as silence at its true
  place, the tracks end together, and a microphone whose own clock runs fast or slow is corrected in
  small steps, counted in the summary as `corrections for drift`.
- An existing recording is never overwritten.
- Once Google Drive is set up, each class has a second copy there, every file checked against
  Drive's MD5, and an upload that breaks off goes on where it stopped. The laptop's audio is deleted
  only when you ask (9 > L, `drive free-space`), and only after each file is checked against its copy
  on Drive once more (see [Google Drive](#google-drive)).

## The glossary

A glossary is a text file of terms ElevenLabs should spell as written, one per line, sent with the
audio as its keyterms. ClassScribe ships none, and sends none until your settings name one: a new
install transcribes without a glossary. To make one:

- one term per line; lines starting with `#` are comments, and blank lines are passed over
- up to 1000 terms, each under 50 characters and at most 5 words, without `< > { } [ ] \`
- parts joined by a hyphen, a dash, a slash or another mark count as separate words: `Ahl al-Sunna`
  is three, and `Ahl al-Sunna wa-l-Jamāʿa` is six, one too many. Apostrophes, ʿayn and hamza stay
  inside their word.
- a term that breaks a rule is skipped, with a warning that says why; the others are still sent
- a term repeated, in any letter case, is sent once
- if a term keeps coming out wrong, try a plainer spelling of it

Keep it in your settings folder, `%APPDATA%\ClassScribe`, where an update never touches it, and name
it once:

```powershell
ClassScribe settings set ElevenLabs:KeytermsFile "%APPDATA%\ClassScribe\keyterms-islamic-sciences.txt"
```

Environment variables such as `%APPDATA%` work, and quotes around the path are ignored. A relative
path is taken from the settings folder and from nowhere else, never from beside the app. An edit to
the file counts from the next recording's start: no build and no update.

A saved class can name a glossary of its own, or none: `ClassScribe class set "Mantiq" --keyterms
"%APPDATA%\ClassScribe\mantiq.txt"`, `--keyterms none`, or the last question of menu C, E (Enter
keeps it, N for none, `-` for `ElevenLabs:KeytermsFile`'s). `class set "Mantiq" --clear keyterms`
takes it away again. A class's glossary that is not there is named in the menu's header and at the
run's start; it never keeps the class from recording.

**Which glossary a recording takes** is decided once, as it starts, and kept beside it in
`<base>.glossary.json`, so that a transcript made later, or a new try, sends what the first one
sent, even after the settings changed. The order:

1. a glossary given to the command: `transcribe <path> --keyterms "<file>"`, or `--keyterms none`
   (this transcript only; its entry in `.glossary.json` says so);
2. the recording's own `.glossary.json`, when it has one;
3. the saved class the run was started as (`record --class`, menu C), when it names one;
4. a saved class whose course is the recording's name, when it names one, for a start by name too
   (`record "Mantiq" ...`), and for a recording transcribed later; when classes of that course name
   different glossaries, the next step decides, with a warning that names them;
5. `ElevenLabs:KeytermsFile`;
6. none.

A recording made before this version has no `.glossary.json`: steps 4 to 6 decide at its first
transcription, and write it then. `rebuild` sends nothing and uses no glossary.

One line says which glossary, as a run starts and again before each upload:

```text
Glossary: keyterms-islamic-sciences.txt (ElevenLabs:KeytermsFile), 94 terms
Glossary: mantiq.txt (the saved class "Mantiq"), 40 terms
No glossary: ElevenLabs:KeytermsFile is empty
Glossary: keyterms-islamic-sciences.txt, as decided when this recording started (94 terms then, 94 now)
```

When the file changed since the recording started, it is sent as it is now, and the line says
`changed since: 94 terms then, 97 now`. When it is gone, the glossary is decided again by steps 3 to
6, with a warning that names the missing file: at a continued recording's start, the new decision
is kept in its `.glossary.json` from then on; at the upload right after a class, the glossary that
class's start decided is sent, as its start line named it. A class's transcript sends that glossary
too when its `.glossary.json` is damaged or could not be written. A change to a saved class or to
`ElevenLabs:KeytermsFile` made during a class counts from the next recording; for the class under
way, put the file where its start line says, or send another later with `transcribe <path> --again
--keyterms "<file>"`. A glossary that is not there, or cannot be read, never stops a recording or
costs a transcript: the transcript is made without its terms, and the warning says so. The upload
line still counts the terms sent: `Uploading ... with 94 keyterms`.

The `.glossary.json` beside a recording looks like this, one `sent` entry per transcript, and goes
to Google Drive with the recording's other files:

```json
{
  "version": 1,
  "decided": {
    "setting": "%APPDATA%\\ClassScribe\\keyterms-islamic-sciences.txt",
    "path": "C:\\Users\\you\\AppData\\Roaming\\ClassScribe\\keyterms-islamic-sciences.txt",
    "from": "ElevenLabs:KeytermsFile",
    "terms": 94,
    "sha256": "58659e75...",
    "utc": "2026-11-01T14:04:42+00:00"
  },
  "sent": [
    { "copy": 1, "path": "C:\\Users\\you\\AppData\\Roaming\\ClassScribe\\keyterms-islamic-sciences.txt", "terms": 94, "sha256": "58659e75...", "utc": "2026-11-01T16:31:07+00:00", "from": "as decided when this recording started" }
  ]
}
```

The repository keeps an example, `examples\glossary\keyterms-islamic-sciences.txt`: a list for the
Islamic sciences in IJMES transliteration. Technical terms, school names among them, are fully
transliterated. Names of people and titles of books follow IJMES in leaving out macrons and dots
while keeping ʿayn and hamza, as in `al-Ghazali` and `al-Iqtisad fi al-Iʿtiqad`
([IJMES guide](https://www.cambridge.org/core/journals/international-journal-of-middle-east-studies/information/author-resources/ijmes-translation-and-transliteration-guide)).
It is never built or published; copy it into your settings folder to use it.

After changing the glossary you can transcribe an old recording with it: `transcribe <path> --again
--keyterms "<file>"`, or menu choice 3, then Y, which sends the glossary decided when the recording
started. That is billed as a new transcription.

## Settings

Your settings are kept in `%APPDATA%\ClassScribe\appsettings.json`, apart from the program, and the
menu names the file it reads at the top (`Settings: ...`). That file is yours: ClassScribe makes it
at your first change (in the menu, with `settings set` or `class add`, or with `settings open`), as
a full copy of every setting, each at its default unless you changed it, under a short header that
says what the file is, and says once `Your settings are now kept in ...; an update of ClassScribe
never changes that file.` From then on an update of ClassScribe never changes it: a later build that
adds a setting uses its default until you add it. Your next change from the menu (S or C), `settings set`
or `class` adds every setting your file lacks, at its default, and names each with the build's
version (`ClassScribe 0.7.0 added 1 setting to your file: Recording:CameraResolution = 720p.`), and
`ClassScribe settings fill` adds them without changing anything else. When a later build changes a default, your file
keeps the value it holds, and `settings list` marks it (`newer default: auto`), as the menu's header
does (`1 setting holds an older default; settings list shows it`): `settings reset <setting>` takes
the new default. Before each change ClassScribe keeps the file as it was in
`appsettings.previous.json` beside it.

Until you change something, ClassScribe reads the defaults, from the `appsettings.json` shipped
beside the program, and the menu says so. Once your own file is there, every ClassScribe you start
reads it, `dotnet run` included. Before that, `dotnet run` reads the copy in
`src\ClassScribe\bin\Debug\net10.0-windows10.0.19041.0`, which a build makes from
`src\ClassScribe\appsettings.json` and replaces only when that file is the newer of the two. So edit
`src\ClassScribe\appsettings.json` and build again (`dotnet run` builds each time it starts), or edit
the copy itself, which a build then leaves alone until the project's file changes again. While the
project's file is newer than the copy and differs from it, the menu and each command say so in a
`WARNING` line, and the change does not count until the build.

There are three ways to change a setting, and each checks the value before it is saved:

- the menu: S for the recordings folder and the camera's picture size, C for saved classes, and S
  when asked after a pick in choices 6 and 7;
- PowerShell: `ClassScribe settings set Recording:OutputFolder "D:\Classes"` (see
  [Settings from PowerShell](#settings-from-powershell));
- Notepad: `ClassScribe settings open` opens your file (made first, as a full copy, if you have none
  yet). A comment you write there is kept as long as you edit by hand, but ClassScribe will not
  change a file with comments of your own (any comment but its own header, one between the header and
  the first `{` included): it says so and points to Notepad.

If your settings file is deleted or moved, ClassScribe does not quietly make a new one. It uses the
defaults, says `Your settings file ... is missing. If it was moved or deleted by mistake, put it back
(appsettings.previous.json beside it is the version before your last change). To start again from
the defaults: ClassScribe settings reset --all.` in the menu's header and at each command, and
refuses every change until the file is back or you reset.

ClassScribe reads the file again before each menu choice and each command, and checks it before
anything uses it, so an edit counts from the next choice, without a restart. A recording that is
running keeps the settings it started with. A value ClassScribe cannot use stops a choice or command
other than a recording before it uploads anything, with a message that names the setting and says
what it must be, then `Nothing was started. Fix the settings above in ..., then choose again` (for a
command, `then run the command again`, with exit code 1). A recording is never stopped by the file:
menu choices 1, 2 and C, the report's C, and `record` and `record-only` run with every setting that
passes, and leave out each one the check refuses, with a warning that names it and the value used in
its place (`Recording:FlushSeconds is "abc" in ..., which the check refuses; this recording uses the default,
"5", in its place.`). A file that is not valid JSON, or cannot be read, gives the settings ClassScribe read
last, or the defaults, and what the command line says, with the line `Nothing of ... could be taken,
so this recording uses the settings ClassScribe read last, or the defaults where it read none, and
what the command line says.` The menu shows the problems at the top as well. Such a file does not
keep ClassScribe from starting, and fixing the file is all it takes.

An environment variable that sets a value ClassScribe cannot use, such as `Recording__FlushSeconds`
set to `abc`, is different: it cannot be fixed while ClassScribe runs, so ClassScribe does not start,
and says which variable it is (`An environment variable sets a value ClassScribe cannot use ...`).
For a dry run that must not touch your settings, set `CLASSSCRIBE_SETTINGS_FOLDER` to another folder
in that PowerShell window first: ClassScribe then reads and writes `appsettings.json` there instead,
and the menu says so (`Settings folder: ..., from CLASSSCRIBE_SETTINGS_FOLDER, as in a dry run`).

Some things only bring a warning, given once, at the first choice or command, and none of them holds
up a class: an API key written into the settings (see below); a `YouTube:LongestVideoHours` above
`Recording:StopAfterHours`, which the safety stop then cuts short; and a recordings folder, in
`Recording:OutputFolder` or a saved class's `OutputFolder`, that is:

- inside OneDrive, Google Drive for desktop (a drive labelled `Google Drive`, or a path through
  `My Drive`) or Dropbox (a folder that Dropbox's own `info.json` names, under `%APPDATA%\Dropbox`
  or `%LOCALAPPDATA%\Dropbox`, as [Dropbox describes](https://help.dropbox.com/installs/locate-dropbox-folder)):
  these upload a recording again and again while it grows, and with ClassScribe's own copy on Google
  Drive each class would go up to the cloud twice;
- a drive's root, such as `D:\`, your user folder itself, or a folder that holds `AppData` or
  `Windows`: each course's folder would sit among folders that are not the classes'.

The file is JSON, which has two traps:

- In a path, write each backslash twice, as in `"D:\\Classes"`, or use forward slashes, as in
  `"D:/Classes"`. A single backslash starts a special character, such as `\t` for a tab.
- Write numbers with a dot (`1.5`, not `1,5`), and on or off as `true` or `false`.

The ones you are most likely to change:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Recording:OutputFolder` | `%USERPROFILE%\ClassScribe` | Where recordings and transcripts go: a folder per name, and in it a folder per class, named after the local date it started on (see [Where things go](#where-things-go)). Environment variables work, and quotes around the path are ignored. A relative path is taken from your user folder, so `Classes` means `%USERPROFILE%\Classes`. Menu S changes it (choice 1). A folder inside OneDrive, Google Drive or Dropbox, a drive's root, your user folder itself or a folder holding `AppData` or `Windows` brings a warning. A saved class may name its own folder. |
| `Recording:Device` | empty | Part of the name of the output device to record, such as `Pixel Buds`. Empty means the device the class plays on. |
| `Recording:App` | empty | The app whose sound to record, by its process name, such as `chrome`. Overrides `Recording:Device`. |
| `Recording:Mode` | `Class` | What a recording holds: `Class` (the sound the computer plays), `ClassAndMicrophone` (that and your microphone, mixed into one transcript) or `Microphone` (your microphone alone, in person). The names are taken in any letter case; a number is refused. Menu choice 7 overrides it until ClassScribe is closed. See [Your microphone](#your-microphone). |
| `Recording:Microphone` | empty | Part of the name of the microphone to record, such as `OBSBOT`. Empty means Windows' default microphone, never a Bluetooth one or an input that records what the computer plays (Stereo Mix): then the laptop's own microphone array, then another microphone. A Bluetooth microphone is used only when named here. |
| `Recording:MicrophoneProcessing` | `Raw` | `Raw` asks Windows for the microphone's audio without its sound processing, and takes `Default` when that is not available; `Default` takes the processed audio, as any app gets it. |
| `Recording:MicrophoneFollows` | `auto` | With the class and your microphone, when the class is recorded from a device: the app whose microphone use your microphone follows, by part of its process name. Your voice is recorded only while that app has the microphone open and taking sound. `auto` follows any app that holds the microphone; `none` records whenever V is on. In app mode the recorded app is followed and this is not read; `--mic-follows` and menu choice 7 can name another for a run. Empty, or naming ClassScribe itself, is refused. |
| `Recording:ClassAppMute` | `Unknown` | Whether the class app lets go of the microphone when you mute in it: `Visible` (it does; ClassScribe follows your mute by itself), `Invisible` (it keeps the microphone open; your voice is recorded only after you press V, and only while the app holds the microphone) or `Unknown` (as `Invisible`, and ClassScribe reports when it sees the app let go of the microphone). Measure your class page with the microphone check before setting `Visible`. |
| `Recording:CameraResolution` | `720p` | The size of the camera's picture for a lesson recorded with the camera: `480p` (848x480, about 1.2 GB an hour with the sound), `720p` (1280x720, about 2.3 GB an hour) or `1080p` (1920x1080, about 3.7 GB an hour), in any letter case, or a width and a height such as `1024x576`: the width from 320 to 1920 and the height from 240 to 1080, both even, in a shape from 4:3 to 16:9 (a typed size was not measured; the camera records the closest it offers). `--resolution` and menu choice 7 choose another for a run. Menu S (P) changes it, as does S after a pick in choice 7. The picture is encoded at a constant bitrate by the size: about 2.5 Mbit/s at 480p, 5 at 720p, 8 at 1080p. `camera-check` shows the size in force. |
| `Recording:CameraOffsets` | `{}` | Moves a camera's picture against the sound in a lesson, by part of the camera's name, in milliseconds: `{ "OBSBOT": -60, "Integrated": -30 }` (negative is earlier). When several parts are in a camera's name, the longest applies. Each name part 1 to 200 characters, without control characters or a colon; each figure a whole number from -1000 to 1000. Empty: no camera is moved. A lesson keeps the offset it was recorded with. To measure a camera's, see [Recording with the camera](#recording-with-the-camera). Each offset is set on its own: `ClassScribe settings set Recording:CameraOffsets:OBSBOT -60` (for a name part with a space, the whole setting's name in quotes), and `settings reset Recording:CameraOffsets:OBSBOT` takes it out; a recording leaves out only an offset the check refuses. |
| `Recording:WakeCameraMicrophone` | false | When on, and the microphone being recorded belongs to a webcam (by the container it shares with a camera that is neither built in nor virtual), ClassScribe keeps that webcam's picture open, shared and never exclusively, from before the microphone opens until the recording ends, keeping none of it, so that a webcam that sleeps by itself, such as the OBSBOT Meet Flip with Device Auto Sleep on, cannot fall asleep and silence its microphone. The camera's light stays on while recording. Run `camera-check` and type Y once before turning it on, so that a security program such as Bitdefender asks about ClassScribe while you watch. Turn it on with `ClassScribe settings set Recording:WakeCameraMicrophone true`, or for the ClassScribe you start alone with `Recording__WakeCameraMicrophone=true` in its environment. See [A webcam's microphone](#a-webcams-microphone-the-obsbot). |
| `Recording:SilenceWarningSeconds` | 60 | Warn after this much silence. At least 1. |
| `Recording:KeepUploadFile` | false | Keep the 16 kHz copy that was uploaded, after a failed or cancelled try as well. When off, the copy is removed after every try, whether it worked or not. |
| `Recording:OpenFolderWhenDone` | true | Open the folder in Explorer when the recording or transcript is ready. |
| `Recording:OpenTranscriptWhenDone` | true | Open the transcript when it is ready. |
| `Recording:KeepScreenOn` | `OnBattery` | When the screen is kept on while ClassScribe records or uploads: `OnBattery` (only on battery, where it is what keeps the laptop out of standby), `Always` or `Never`. The names are taken in any letter case; a number is refused. See [Keeping the laptop awake](#keeping-the-laptop-awake). |
| `Recording:SeparateProcess` | true | Record in a background ClassScribe process, so that closing the window does not end the class. `false` records in the window, as before. Under `dotnet run` the recording runs in the window either way. |
| `Recording:ReopenWindow` | `OnCloseAndProblems` | When ClassScribe brings a minimized window back by itself, without taking the focus, while no window shows the recording: `OnCloseAndProblems` (3 seconds after the last window was closed while it records, at once when something needs attention or the run fails, and once something worth a look has lasted 2 minutes), `OnProblems` (only for those problems, never for a close) or `Never`. The names are taken in any letter case; a number is refused. That window ignores every key until Enter. See [The background recorder](#the-background-recorder). |
| `Recording:BlockShutdown` | true | While a recording or its upload runs, a restart, shutdown or sign-out you start asks first and names ClassScribe; a forced shutdown is not held. |
| `Recording:ContinueWithinMinutes` | 60 | Offer to continue a recording of the same name that stopped this recently without a transcript. 0 never offers; at most 1440. |
| `Recording:StopAfterHours` | 9 | The safety stop, with a warning 15 minutes before, because ElevenLabs takes at most 10 hours in one file. 0 turns it off; at most 9.5. |
| `ElevenLabs:LanguageCode` | empty | Empty lets ElevenLabs detect the language. |
| `ElevenLabs:NoVerbatim` | false | Drop filler words and false starts. |
| `ElevenLabs:KeytermsFile` | `""` | The glossary of every recording no saved class names one for (see [The glossary](#the-glossary)): none by default, since ClassScribe ships none. Name a glossary of your own, such as `%APPDATA%\ClassScribe\keyterms-islamic-sciences.txt`, most easily with `settings set`, which writes it into the file as JSON needs it. Environment variables work, and quotes are ignored; a relative path is taken from your settings folder, never from beside the app. Empty, or `none`, sends no glossary. The default changed: a file that still holds `keyterms.txt`, the glossary earlier versions shipped, is pointed out by `settings list` (`newer default: ""`), and that name now finds no file. |
| `ElevenLabs:TimeoutMinutes` | 60 | The least time one try is given: the upload, the transcription and the whole answer. A longer recording gets more; see [How long an upload may take](#how-long-an-upload-may-take). From 1 to 1440. |
| `ElevenLabs:SlowestUploadMbps` | 1 | The slowest upload speed, in megabits per second, that the time limit allows for. If your line uploads more slowly, set it a little below that speed. From 0.1 to 1000. |
| `ElevenLabs:PricePerHourUsd` | 0.22 | The API price of an hour of audio, in US dollars, for the estimate logged after each transcript: Scribe v2 on [ElevenLabs' API price list](https://elevenlabs.io/pricing/api). 0 logs the audio's length without a price. From 0 to 100. See [Cost and privacy](#cost-and-privacy). |
| `ElevenLabs:KeytermsPricePerHourUsd` | 0.05 | What keyterm prompting adds an hour, in US dollars, counted in the estimate when a glossary is sent. From 0 to 100. |
| `ElevenLabs:ReadAccountUsage` | true | Read the account's credits before and after each transcript and log what it cost in credits. It needs the key's User permission; a key restricted to Speech to Text is told so once, and the transcript is not affected. `false` asks nothing. |
| `Transcript:IncludeTimestamps` | true | Start paragraphs with `[hh:mm:ss]`. |
| `Transcript:ParagraphPauseSeconds` | 2.0 | A pause between words this long, in seconds, starts a new paragraph. 0 or more; 0 turns these breaks off. |
| `Transcript:MaxParagraphCharacters` | 900 | A longer paragraph is split at the next sentence end. 0 or more; 0 turns this off. |
| `Transcript:ShowGapsLongerThanSeconds` | 60 | In a recording of the class alone, a heading in the transcript where at least this much time with nothing playing was left out (`About 3 minutes with nothing playing were left out here`). 0 writes no such heading; below 0 is refused. Recordings with your microphone leave nothing out. |
| `Drive:Enabled` | true | Once Google Drive is set up, put each class in the queue as its run starts and upload it after its transcript (with record only, once its files are closed), with what else waits; and upload a transcript made later. `false` puts nothing in the queue and sends nothing by itself: then only menu choice 9 and `drive upload` upload, and the menu's header says so. See [Google Drive](#google-drive). |
| `Drive:FolderName` | `ClassScribe` | The name of the folder ClassScribe makes in My Drive at the first Google sign-in, which holds the classes as the recordings folder does: a folder per course, and in it one per class's date folder. ClassScribe finds that folder again by a mark of its own, so you may rename or move it in Drive, and a change here later changes nothing. At most 100 characters, without tabs or line breaks. |
| `Drive:Upload` | `Everything` | What of a recording goes up: `Everything` (both tracks' audio, the transcripts, ElevenLabs' answers, the marks and the log) or `WithoutAudio` (all but the `.wav` files, which then stay on the laptop alone, and a lesson's video with them). The temporary `.upload.wav` never goes up. The names are taken in any letter case; a number is refused. |
| `Drive:LessonVideos` | `false` | `true` sends a lesson's finished video (`<base>.mp4`) to Google Drive too, last, while `Drive:Upload` sends audio; its camera pieces then stay on the laptop until free-space finds the video safe on Drive. `false` keeps lesson videos on the laptop, and their pieces are deleted once the video passes its check. An hour of 720p is about 2.3 GB, so a free 15 GB Drive holds about six hours. `true` or `false`. |
| `Drive:ChunkMegabytes` | 8 | The size of each piece sent to Google Drive, in MiB (1,048,576 bytes), from 1 to 256: a piece lost to a dropped connection costs at most this much, sent again. |
| `YouTube:CourseChannels` | `[]` | Your course's YouTube channels, each as its UC id (best, since it never changes) or its @handle, such as `["UCxxxxxxxxxxxxxxxxxxxxxx"]`: their videos are never downloaded, and while it names any, a video whose channel is not known needs your word. `--allow-unlisted` is refused while it is empty. An entry of another shape, or a single value where the list belongs, is refused. See [Downloading with yt-dlp](#downloading-with-yt-dlp). |
| `YouTube:MayDownloadChannels` | `[]` | The channels whose videos you may download: your own, and those whose owners allow it, each as its UC id or @handle, such as `["@yourchannel"]`. Their videos are ready under the batch's `YES`, unlisted ones too. Refused as above, and when a channel is also in `YouTube:CourseChannels`. |
| `YouTube:DriveUpload` | `Everything` | What of a YouTube video's recording goes to Google Drive: `Everything`, as for a class, or `WithoutAudio`, which keeps its `.wav` files on the laptop alone (the video can be fetched again). The names are taken in any letter case; a number is refused. |
| `YouTube:YtDlpPath` | empty | yt-dlp's `yt-dlp.exe`, as a full path. Empty finds it in winget's links folder, then on PATH; Deno and FFmpeg are looked for beside it first. Environment variables work, and quotes are ignored. A relative path, one that is not an `.exe`, or one that is not there is refused. |
| `YouTube:BrowserPath` | empty | Chrome's `chrome.exe`, as a full path, for the video window. Empty finds it through Windows' App Paths, then in Program Files, Program Files (x86) and `%LOCALAPPDATA%`. Refused as `YouTube:YtDlpPath` is. |
| `YouTube:LongestVideoHours` | 4 | The longest a video is recorded for as it plays, ads included: its recording stops there, and the plan's worst case is worked out from it. From 0.25 to 9. Above `Recording:StopAfterHours` it only brings a warning, since the safety stop then ends a video first. |
| `YouTube:OEmbedBaseUrl` | `https://www.youtube.com/` | Where the record path reads the videos' titles: YouTube's public oEmbed address, one request per video with nothing but its link. An `https` address, or `http` to `127.0.0.1` or `localhost` only, for a local stub in a dry run, as `YouTube__OEmbedBaseUrl`. |
| `Logging:Console:FormatterName` | `plain` | One line per message. Set to `simple` to see full stack traces. |

The file has a few more that rarely need changing, checked the same way: `Recording:FlushSeconds`
(5, at least 1), `Recording:StatusIntervalSeconds` (60, at least 1), `Recording:MaxSegmentMegabytes`
(1500, from 10 to 4000), `ElevenLabs:ModelId` (`scribe_v2`, not empty), `ElevenLabs:TagAudioEvents`
(false; true marks laughter and other sounds), `ElevenLabs:MaxAttempts` (3, from 1 to 10),
`ElevenLabs:RetryDelaySeconds` (5, from 0 to 3600), and `Drive:ApiBaseUrl` and `Drive:OAuthBaseUrl`
(Google's own addresses, `https://www.googleapis.com/` and `https://oauth2.googleapis.com/`; an
`https` address, or `http` to `127.0.0.1` or `localhost` only, for a local stub in a dry run, as
`Drive__ApiBaseUrl`). The Google client and sign-in are never in this file (see
[Where things go](#where-things-go)). The `Logging` section is read again and checked
like the rest: each log level, under `Logging:LogLevel` (where `Default` is `Information`) or under a
log's own section, such as `Logging:Console:LogLevel`, must be `Trace`, `Debug`, `Information`,
`Warning`, `Error`, `Critical` or `None`.

The API key is deliberately not in this file. It is read from `ELEVENLABS_API_KEY` alone: a key
written into `appsettings.json` is ignored, and ClassScribe warns you to remove it, so that it
cannot be shared by mistake.

### Settings from PowerShell

```powershell
ClassScribe settings path                                           # the file read now, alone on the line
ClassScribe settings list                                           # every setting: its value, its default when it differs, where it comes from
ClassScribe settings list --json | ConvertFrom-Json                 # the same, for a script
ClassScribe settings get Recording:OutputFolder                     # one value, alone; a list's items a line each
ClassScribe settings set Recording:OutputFolder "D:\Classes"        # change one setting
ClassScribe settings set YouTube:CourseChannels "@Channel1" "@Channel2"   # a list: all its items
ClassScribe settings set Logging:LogLevel:ClassScribe.Drive Debug   # one entry of the log levels
ClassScribe settings set Recording:CameraOffsets:OBSBOT -60          # one camera's offset, in milliseconds
ClassScribe settings reset Recording:Device                         # back at its default, written into your file
ClassScribe settings reset --all                                    # every setting; your saved classes are kept
ClassScribe settings open                                           # your file in Notepad
ClassScribe settings fill                                           # add the settings your file lacks, at their defaults
```

`settings set` checks the value as the menu does before anything is written, and says what changed
(`Recording:OutputFolder is now "D:\Classes" (it was "%USERPROFILE%\ClassScribe") in ...`); a value it
refuses changes nothing, and a name it does not know is answered with the nearest one
(`Recording:OutputFolders is not a setting; did you mean Recording:OutputFolder?`). Setting a value
it already has writes nothing. When an environment variable sets that setting, the change is made
and ClassScribe says it has no effect while the variable is set. The ElevenLabs key is never a
setting: `settings set ElevenLabs:ApiKey` is refused, with how to set `ELEVENLABS_API_KEY` instead.
`settings list` marks a value that comes from an environment variable, and one that holds an older
default (`newer default: auto`). `settings reset --all` keeps the file as it was in
`appsettings.previous.json`. Two ClassScribe windows never write the file at once: a change waits up
to 2 seconds for another to finish, and a file that another program saves while ClassScribe changes
it is read again before the change is made.

Names such as `Ṣughrā` come out right in the window. To read them through a pipe, such as
`| ConvertFrom-Json` or `> list.txt`, first run, in that PowerShell window:

```powershell
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
```

The commands' exit codes: 0 done; 1 a settings file that is not valid JSON, has comments of your
own, is missing, cannot be written, or keeps changing while ClassScribe writes it; 2 an unknown
setting, the key, a value the check refuses, or another mistake on the command line. They run
whatever the file holds, so a file the check refuses can be fixed with them.

### Saved classes

A saved class keeps everything a class needs under a name: its course (the recording's name, which
names its folder and files), what it records, your microphone and the app it follows, its own
recordings folder, whether it is transcribed, its own glossary, and for a lesson in the room the
camera and the picture's size. Start one from menu C, by typing its name or course in menu choices 1 and 2, or
from PowerShell:

```powershell
ClassScribe class add "Mantiq" --app chrome --mic "Microphone Array" --mic-follows chrome
ClassScribe class add "Kalam (in person)" --course "Kalam" --in-person "Microphone Array" --record-only
ClassScribe class add "Lesson" --in-person --camera "OBSBOT" --resolution 720p
ClassScribe class list                                     # each class, with what it records or why it cannot be used
ClassScribe class show "Mantiq"                            # its fields, what it records and its command; --json too
ClassScribe class set "Mantiq" --output "D:\Classes"       # change fields; --clear output empties one
ClassScribe class set "Mantiq" --keyterms "%APPDATA%\ClassScribe\mantiq.txt"   # its own glossary; none for none, --clear keyterms for the setting's
ClassScribe class rename "Mantiq" "Mantiq (Chrome)"        # its course, folders, files and Drive stay as they are
ClassScribe class remove "Mantiq"                          # no recording is touched
ClassScribe record --class "Mantiq"                        # record it as saved
```

Classes are saved in your settings file under `Classes`, by name, each with the fields `Course`,
`Mode` (`Class`, `ClassAndMicrophone` or `Microphone`, in person), `App` or `Device` (neither: the
device the class plays on), `Microphone`, `MicrophoneFollows`, `OutputFolder`, `Transcribe` (true
unless `false`), `Camera` (part of a camera's name, or `auto` for a USB camera, else the built-in one),
`CameraResolution` and `KeytermsFile` (the class's own glossary, or `none`; `ElevenLabs:KeytermsFile`
when empty; a file that is not there is named in the menu's header and at the run's start, and never
makes the class unusable); a field a later build adds is kept as it is. `class add` without `--course`
takes the class's name as its course. Names are matched exactly, in any letter case, and must be
usable as a folder's name, at most 80 characters. A class that cannot be used, such as one with both
`App` and `Device`, is named in the menu's header with why, `class show` ends with exit code 2 for
it (and while your settings file is missing; 1 only for a file that is not valid JSON or cannot be
read), and starting it is refused with the same words; the others are not held up. `class add` and
`class set` refuse a change that would leave a class unusable, and change nothing. In menu C, N
offers the Lesson video preset (L): in person, with any camera (a USB camera, else the built-in
one), your microphone, the settings' folder, transcribed; see
[Recording with the camera](#recording-with-the-camera).

### How long an upload may take

Each try of an upload has a time limit, for the upload, the transcription and the whole answer. It
is at least `ElevenLabs:TimeoutMinutes`, and grows with the recording: 10 minutes, plus the upload at
`ElevenLabs:SlowestUploadMbps`, plus a quarter of the audio's length, rounded to the minute. At the
defaults an hour of class keeps its 60 minutes, while two hours (an upload of 230.4 MB) get 71, a
class of 2 hours 11 minutes (252.3 MB) 76, four hours 131 and six hours 192. The `Uploading ...` line
gives the size, in MB of a million bytes, and the limit, such as
`Uploading Kalam_2026-10-14_19-30-00Z.upload.wav (230.4 MB) with ... keyterms; time limit 71 minutes`.
It is a limit, not a prediction: how long ClassScribe would wait before giving up on that try. The
76, for instance, is 10 minutes, plus 33.6 for 252.3 MB at 1 Mbps, plus 32.9 for a quarter of
2 hours 11 minutes, rounded. The exchange itself usually takes a small fraction of it (a two-hour
class: well under two minutes to upload on a normal line, and a minute or so for ElevenLabs to
answer).

A recording longer than 9 and a half hours is not sent at all, because ElevenLabs takes at most 10
hours per file: nothing is prepared or uploaded, and the message says how to send it in pieces.

## A program to double-click

Publish ClassScribe as a folder outside OneDrive, as [Set up once](#set-up-once) says:

```powershell
dotnet publish src/ClassScribe -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Programs\ClassScribe"
```

Keep `ClassScribe.exe` together with the files beside it, and make a shortcut to it: right-click
`ClassScribe.exe`, then Show more options, Send to, Desktop (create shortcut). It needs .NET 10,
which the SDK from [Set up once](#set-up-once) includes, and Windows 10 version 2004 or later. The
folder is about 33 MB, 25 MB of it Windows' own camera API (`Microsoft.Windows.SDK.NET.dll`), which
ClassScribe uses to list the cameras. Start classes from this copy: its
recordings run in the background, and a rebuild of the repo during a class never touches it.

Double-click the shortcut for the menu. Dropping an audio or video file on the shortcut, or on
`ClassScribe.exe`, transcribes that file, as the `transcribe` command does: a dropped recording that
already has a transcript is not sent again, and the window gives the commands to make it again for
free or to transcribe it again with `--again`; a recording being recorded now is not sent either. A
window opened this way stays open at the end while there is something to read: after a dropped file,
and after any failure, such as settings ClassScribe cannot use. Press Enter, Esc or Ctrl+C to close
it. Keys pressed before `Press Enter to close` appears are ignored, so an Enter pressed during the
upload cannot close it over the result.

The published app keeps your settings in `%APPDATA%\ClassScribe\appsettings.json`, made at your
first change, and never in its own folder, so publishing again never changes them (see
[Settings](#settings)). The `appsettings.json` beside `ClassScribe.exe` holds the shipped defaults,
which a publish replaces. No glossary is shipped, and none beside `ClassScribe.exe` is ever read,
not even a `keyterms.txt` an older version left there: keep your glossary in your settings folder
and name it in your settings (see [The glossary](#the-glossary)).

Do not publish as a single `.exe` (`-p:PublishSingleFile=true`): Bitdefender has quarantined
single-file builds of ClassScribe as `Gen:Variant.Yogi.67941`, a false positive, while the folder
publish has never been flagged. Do not turn on trimming or Native AOT either, because NAudio reaches
Windows audio through COM interop.

## Cost and privacy

- ElevenLabs lists Scribe v2 at $0.22 per audio hour through the API, plus $0.05 an hour (about 20%)
  when keyterms are sent. A two-hour class with the glossary is about 54 cents, and one of 2 hours
  11 minutes about 59 cents. After every transcript the log says what it cost, right after
  `Transcript received`, from the audio ElevenLabs counted at that price
  (`ElevenLabs:PricePerHourUsd` and `ElevenLabs:KeytermsPricePerHourUsd`):
  `ElevenLabs transcribed 131 minutes of audio (02:11:24): about $0.59 at the API price, $0.22 an hour plus $0.05 an hour for the 61 keyterms`.
  ElevenLabs bills per audio minute, so the sum is approximate; when ElevenLabs' count and the file's
  length differ by more than a minute, the line says whose number it used. The estimate never turns
  dollars into credits, because the in-app credit rate and the API's dollar rate cannot be reconciled
  from ElevenLabs' pages.
- When the key may read the account, the run's last line, after the transcript is saved and opened
  (only the upload to Google Drive comes after it), gives the credits themselves: `ElevenLabs
  charged 43,362 credits for this transcript; 56,638 of 100,000 credits are left until 1 November`.
  The credits are read before the upload and again after the transcript, so the difference counts anything else the account did in between; when the first
  read did not succeed, the line gives the account's standing instead (`ElevenLabs account: ...
  credits used this period`), and when the credits did not change, it says so, since API usage on a
  plan may be billed in dollars and then shows on the usage page alone. A key restricted to Speech to
  Text is all the app needs; to see the credits here, also allow User (read) on the key under
  Developers > API Keys on elevenlabs.io. Until then the estimate is the only cost line, and the run
  says once `ElevenLabs would not show the account's credits to this key (HTTP 401,
  missing_permissions): most likely it is restricted to Speech to Text, which is all the transcript
  needs. To see the credits here, allow User (read) on the key under Developers > API Keys on
  elevenlabs.io, or set ElevenLabs:ReadAccountUsage to false to stop asking` (ElevenLabs may answer
  `HTTP 403` instead; both are read the same way); `ElevenLabs:ReadAccountUsage` set to `false`
  stops it asking. Nothing about this read can delay or fail a transcript; it has 10 seconds and is
  never tried again. Where the line lands depends on where the class was recorded: in this window,
  and after `transcribe`, it is the last line on the screen before Google Drive's; in a background
  recording, the usual way, the recorder reads the account only once the window has been shown the
  end and has opened the transcript, so the line is in the recording's `.log` beside the recording
  (and in `recorder.log`), not in the window. The estimate line above
  comes before the end, so the window shows it either way.
- Every transcript ElevenLabs sends back is charged, even one ClassScribe could not save; that one
  is kept (see [Where things go](#where-things-go)).
- Making a transcript again from ElevenLabs' saved answer (R in the menu, or `rebuild`) is free:
  nothing is sent, and the log says `Nothing was sent to ElevenLabs, so nothing was charged` (see
  [A transcript again, for free](#a-transcript-again-for-free)).
- The audio is uploaded to ElevenLabs. Keep transcripts for your own study, in line with what your teacher allows.
- YouTube videos cost what a class costs to transcribe, and a batch's plan says what it will cost
  before anything is spent: the sum from the videos' lengths for a download, the most it can cost for
  videos recorded as they play, the account's credits, and after the first transcript the rest
  projected in credits (see [What it costs, and Google Drive](#what-it-costs-and-google-drive)). A
  video's audio goes to ElevenLabs as a class's does, and to Google Drive as `YouTube:DriveUpload`
  says (everything, by default).
- Recording a YouTube video as it plays, ClassScribe sends YouTube one oEmbed request per video for
  its title (`YouTube:OEmbedBaseUrl`), carrying the video's link and nothing else, with no key and no
  cookie. The video window is Chrome with a profile of its own; YouTube sees ordinary views from your
  connection there, signed in with Premium if you signed in there (see
  [Your Premium sign-in in the video window](#your-premium-sign-in-in-the-video-window)), and with ads,
  which are recorded and transcribed, if not. ClassScribe never reads, clicks or changes anything in
  the page or the profile, and Chrome is started with ClassScribe's environment less
  `ELEVENLABS_API_KEY` and every `ElevenLabs__` setting.
- Downloading, yt-dlp talks to YouTube from your connection, signed out: ClassScribe never gives it
  cookies, a browser's data or a sign-in, from the video window's profile or any other, and starts
  every call with `--ignore-config --no-cookies --no-cookies-from-browser --no-mark-watched`, so that no
  configuration file of yt-dlp's can add any. It runs without a window, in a job of its own that ends
  everything it started once the run is over or ClassScribe's window closes, with the same
  environment less the ElevenLabs key. YouTube's bot check or rate limit may last a day
  or more, and may well touch signed-out YouTube in your browser on the same connection too.
- Your microphone, when recorded, is uploaded with the class, mixed into one track at the same
  price. Only what the class app was listening to is recorded from it, unless
  `Recording:MicrophoneFollows` is `none`, or you press V where the app's mute cannot be seen; muting
  yourself in the class page may not keep your voice out, the Windows mute does (see
  [Your microphone](#your-microphone)). In person everyone in the room is recorded and uploaded: ask
  before recording others, and check what your institution allows.
- Google Drive costs nothing extra: the sign-in uses your own Google Cloud project, without billing,
  and asks only for `drive.file`, which lets ClassScribe see and change only the files and folders it
  makes itself, nothing else in your Drive. It never shares anything (it never calls Drive's sharing
  API), and it only adds to Drive: it never deletes, trashes or overwrites anything there, with one
  exception, a new revision of its own copy of a file in three cases only: a text file of the recording
  (its log, its marks, a transcript or an answer) that has grown since it went up, a lesson's video made
  again (with `Drive:LessonVideos` on), and its own copy whose MD5 check failed, sent once more. A
  class's audio goes to Google's servers, as it does to
  ElevenLabs'; with your microphone, or in person, other voices go too. Your own files beside a
  recording, such as the class's PDFs, never go up. The Google client and the sign-in stay on this
  laptop, encrypted for your Windows account; they are never written to a log (the `.log` that goes up
  to Drive included), `appsettings.json` or a run file. The record of what went up
  (`%LOCALAPPDATA%\ClassScribe\drive`) holds file names, sizes, MD5s and Drive's ids, and, while an
  upload is unfinished, the address that lets it go on, which Google accepts for a week at most and
  which is never logged. Deleting files from the laptop, with the free-space option (9 > L,
  `drive free-space`) or by hand, never touches Drive: the free-space option deletes only a
  recording's audio (`.wav`, `_partN.wav`, `.mic.wav`, `_partN.mic.wav`), after checking each file
  against its copy on Drive again, and never its `.md`, `.json`, `.marks.txt` or `.log`. Signing out asks Google to end ClassScribe's access; you can also
  end it yourself at https://myaccount.google.com/connections. Nothing on Drive is touched by a
  sign-out.

## If something goes wrong

Each message starts with the time; warnings and errors also say `WARNING` or `ERROR`. Look for the
words you see.

### The settings and the menu

| What you see | What to do |
| --- | --- |
| `The mistake is on line ...` | `appsettings.json` is not valid JSON. Fix that line; most often a path needs each backslash twice (`"D:\\Classes"`) or forward slashes (`"D:/Classes"`). Then choose again, or run the command again: there is no need to restart. |
| `Nothing was started. Fix the settings above in ..., then choose again` (or `then run the command again`) | The lines above name each setting with a value ClassScribe cannot use, and what it must be. Fix them (`ClassScribe settings set` works whatever the file holds), then choose again or run the command again. Until then the menu shows the same lines at the top, under `Settings:`, and only recordings run, without those settings. |
| `appsettings.json is empty` | A save was most likely cut short. Write the settings back, or at least `{ }`; the settings it leaves out then keep their defaults. |
| `... is no longer there, so the settings stay as they were when it was last read` | The settings file was deleted or renamed while ClassScribe ran. Put it back (`appsettings.previous.json` beside your own file is the version before your last change); under `dotnet run`, a build puts the copy beside the program back. To go back to the defaults instead: `ClassScribe settings reset --all` for your own file, or start ClassScribe again without the file beside the program. |
| `... could not be read: ...` | Another program, such as OneDrive or an editor, held the settings file for longer than a moment, or Windows would not let ClassScribe read it. Close that program, then choose again, or run the command again. |
| `appsettings.json cannot be read: ...` | The file is valid JSON, but not in the shape settings take: the same setting written twice in one section, say, or something other than `{ ... }` around them all. Fix what the message names, then choose again. |
| `WARNING  src\ClassScribe\appsettings.json has changed since the last build` | Under `dotnet run`, ClassScribe reads the copy in `bin\Debug\net10.0-windows10.0.19041.0`. Build again, or edit that copy, for the change to count. |
| `ClassScribe could not start.` and `An environment variable sets a value ClassScribe cannot use` | Change or remove the variable the message names (the setting's name, with `__` for each colon), then start ClassScribe again. |
| `ClassScribe shows its menu only when someone can type into its window` | ClassScribe was started without a command from a script, or with its input redirected. Give it a command, such as `record` or `list`; the help follows the message. |
| `Could not be read now: ...` under a list of `devices`, below an app or a microphone, `Which apps hold each microphone could not be read now: ...`, and `Not everything could be read from Windows just now` at the end | Windows did not give that list, or one app's session in it, at that moment; the rest is complete. The line names the app when Windows did (`Zoom's audio session`, `(Zoom, pid 9090)`) and the Windows call that failed (`GetState: ...`). Run `devices` again. If it keeps saying so, quit the other apps that use sound (Zoom, Teams, Discord, WhatsApp, tray icons included) and run it again; if it still does, restart Windows. A recording goes on beside one app's session that cannot be read; when that app is the one your microphone follows, see `Cannot read zoom's microphone session` below. |
| `Recording:StopAfterHours is 10; it must be from 0 to 9.5 (hours)`, `Recording:ContinueWithinMinutes is -5; it must be from 0 to 1440 (a day)` | The new settings are checked like the others: the message gives the range and an example. Fix the value, then choose again. |
| `Recording:MicrophoneFollows is ""; it must be part of the class app's name, such as chrome, or auto (any app that holds the microphone) or none (V alone).`, `Recording:MicrophoneFollows is "scribe", which names ClassScribe itself; ...` | Name the class app (`chrome`), or write `auto` or `none`. ClassScribe's own recording holds the microphone for the whole class, so following it would record everything. |
| `Recording:Mode is "Klass", which is not Class, ClassAndMicrophone or Microphone.` (likewise `Recording:MicrophoneProcessing`, `Raw` or `Default`, and `Recording:ClassAppMute`, `Visible`, `Invisible` or `Unknown`) | Write one of the names, in any letter case; a number is refused. |
| `Transcript:ShowGapsLongerThanSeconds is -1; it must be 0 or more, such as 60. 0 turns the headings for silence left out off.` | Fix the value. |
| `"..." is not part of an app's name. Keeping "chrome".`, `"..." names ClassScribe itself, whose own recording holds the microphone for the whole class; following it would record everything. Keeping "chrome".` | Menu choice 7 kept the app in force. Type the class app's process name, such as `chrome`, or press Enter. |
| `record needs a source: --device "<part of its name>", --device auto, --app <process> or --in-person`, `record takes one source; you gave --device and --in-person` | Give exactly one source. |
| `--mic adds the microphone to --device or --app; with --in-person the microphone is already the source` | Drop `--mic`, or `--in-person`. |
| `--mic-follows goes with --mic: it names the app whose microphone use your microphone follows`, `--mic-follows cannot go with --in-person: in person there is no class app`, `--mic-follows needs part of an app's name, such as chrome, or auto or none` | Give `--mic-follows` only with `--mic`, and a value that names an app (not ClassScribe), or `auto` or `none`. |
| `record needs a name, right after the word record: ... (--in-person took "Kalam" as the microphone's name)` | Put the name right after `record`, before `--in-person` or `--mic`, which may take a name themselves. |
| `--seconds takes a whole number of seconds, such as 600`, `--seconds takes from 1 to 86400 seconds (a day)`, `--log-folder needs a folder after it, such as "D:\Classes"` | `mic-check`'s refusals; fix the value. |
| `drive needs one of signin, signout, status, upload or free-space, such as ClassScribe drive status` | Give the word after `drive` (exit code 2). |
| `drive free-space needs a path: a recording's file or a folder of recordings, ...`, `drive free-space deletes audio from this laptop once Google Drive has a checked copy of it; add --yes to go ahead, ...` | Give the recording's file or folder, and `--yes`, since the command asks nothing before it deletes. Nothing was deleted (exit code 2). |
| `youtube needs --name "<course>", the course the videos belong to, such as --name "Risala"`, `youtube needs at least one YouTube link, or --list "<file>" of links`, `youtube needs --yes to go ahead after the plan, or --plan to print the plan alone`, `--yes and --plan cannot go together` | Give what the line names. Nothing was started (exit code 2). |
| `--list needs a text file of links after it, such as --list "risala-links.txt"`, `Nothing found at ...`, `--list cannot be read: ...`, `risala-links.txt holds no link` | Give the list file's path; it must be there, readable, and hold at least one link. |
| `Line 4 of risala-links.txt: "..." is not a YouTube link.`, `The command line: "..." is a YouTube link that names no video or playlist.`, `... is a channel's link; a channel's uploads can be hundreds of hours, so give a playlist's link, or the videos' own.`, `... This is the link of a player embedded in a web page, most likely your course page. Use the YouTube button on the player for the video's own link.` | Fix or remove that link (see [Links and a list file](#links-and-a-list-file)). In the menu such a line is left out (`Left out: ...`) and the rest go on. |
| `... is a playlist's link, which only --download takes; give each video's own link to record it as it plays`, `The live link of ... is taken only with --download, whose read tells a live stream from a video; ...`, `youtube records at most 10 videos at a time as they play, each for as long as it plays; these links name 14. Give fewer, or add --download` | Recording as they play takes each video's own link, at most 10 at a time. Give the videos' own links, fewer at a time, or download them with `--download` if you may. In the menu, R leaves such links out (`Left out: ... which only D takes; ...`) and refuses more than 10 (`... these are 14. Paste fewer, or download them with D.`). |
| `youtube takes at most 500 videos at a time; these links name 612`, `This batch holds more than 500 videos; give fewer links, or a shorter playlist, at a time` | Split the batch. The second comes once the playlists have been read; nothing was downloaded (exit code 2). |
| `--allow-unlisted is for --download: recording videos as they play needs no word about them, and downloads nothing`, `--allow-unlisted needs your course's channels: list them in YouTube:CourseChannels first, so that a course video is never taken for one you may download` | Leave the flag out when recording as they play. For a download, fill in `YouTube:CourseChannels` first (see [Downloading with yt-dlp](#downloading-with-yt-dlp)). Exit code 2. |
| `YouTube:YtDlpPath is "..."; it must be the full path of yt-dlp.exe, ...`, `... it must name yt-dlp's .exe file, or be empty to find it.`, `YouTube:YtDlpPath is "...", which is not there; install yt-dlp (winget install --source winget --id yt-dlp.yt-dlp), or leave it empty to find it.` (likewise `YouTube:BrowserPath`, with Chrome's `chrome.exe`) | Give the full path of the `.exe`, such as `"%LOCALAPPDATA%\\Microsoft\\WinGet\\Links\\yt-dlp.exe"`, or leave the setting empty for ClassScribe to find it. |
| `YouTube:CourseChannels:0 is "..."; each channel is its @handle, such as @yourchannel, or its UC id, UC and 22 more letters and digits, as the channel's page shows them.` (likewise `YouTube:MayDownloadChannels`), `YouTube:CourseChannels is "...", which is not a list in square brackets, such as ["@yourchannel"].` | Write the channels as a list, each its `@handle` or its `UC...` id: `["UCxxxxxxxxxxxxxxxxxxxxxx", "@yourchannel"]`. |
| `YouTube:MayDownloadChannels names @..., which is also in YouTube:CourseChannels; a course's channel is never downloaded, so take it out of one of them.` | A channel is your course's or one you may download from, never both. |
| `YouTube:LongestVideoHours is 12; it must be from 0.25 to 9 (hours), such as 4.`, `YouTube:OEmbedBaseUrl is "..."; it must be an https address, such as https://www.youtube.com/, or http://127.0.0.1:<port>/ for a local stub.`, `YouTube:DriveUpload is "...", which is not Everything or WithoutAudio.` | Fix the value. |
| `YouTube:LongestVideoHours is 6, above Recording:StopAfterHours (5), so a YouTube video's recording stops at 5 hours, at the safety stop. Make YouTube:LongestVideoHours at most 5 if that is not what you want` | A warning, given once; nothing is refused. The plan counts the shorter of the two and names it. |
| `ElevenLabs:ApiKey is set in the settings` | Remove the key from `appsettings.json` (or an `ElevenLabs__ApiKey` variable). It is not used: ClassScribe reads the key only from `ELEVENLABS_API_KEY`. This warning comes once, at the first choice or command. |
| `Recording:OutputFolder is inside OneDrive` | OneDrive uploads a recording again and again while it grows, and can hold files ClassScribe needs to delete. Choose a folder outside OneDrive, such as `%USERPROFILE%\ClassScribe`. This warning, too, comes once, at the first choice or command, and refuses nothing. |
| `Recording:OutputFolder is inside Google Drive (...)`, `... is inside Dropbox (...)` (or `Classes:<name>:OutputFolder ...` for a saved class's own folder) | Google Drive for desktop and Dropbox upload a recording again and again while it grows, and ClassScribe's own copy on Google Drive would put it there a second time. Choose a folder outside them, such as `%USERPROFILE%\ClassScribe` (menu S, then 1). A warning only, given once. |
| `Recording:OutputFolder is the root of a drive (D:\)`, `... is your user folder itself (...)`, `... holds a folder named AppData, as a user folder does`, `... holds a folder named Windows, as Windows' own drive does` | Each course's folder would sit among folders that are not the classes'. Give the recordings a folder of their own, such as `D:\ClassScribe`. A warning only, given once. |
| `Your settings file ... is missing. If it was moved or deleted by mistake, put it back ...` (an `ERROR` line in the menu's header, a warning at each command) | Your own settings file is gone, so ClassScribe uses the defaults and refuses every change. Put the file back (`appsettings.previous.json` beside it is the version before your last change), or start again from the defaults with `ClassScribe settings reset --all`. |
| `... in ..., which the check refuses; this recording uses ... in its place.`, then `Fix the settings in ...; until then this recording goes on without them, and other choices and commands wait` | The recording runs without the settings named. Fix them after the class (`ClassScribe settings set`, or `settings open`); until then the other choices wait. |
| `Nothing of ... could be taken, so this recording uses the settings ClassScribe read last, or the defaults where it read none, and what the command line says.` | The file is not valid JSON, or could not be read. The recording runs all the same; fix the file after the class. |
| `Your settings are now kept in ...; an update of ClassScribe never changes that file.` | Your first change made your own settings file, a full copy of every setting. Nothing to do. |
| `Your settings file has comments of your own, which a change from ClassScribe would lose. ...` | ClassScribe changes a file only when it can keep everything in it. Make the change in Notepad (`ClassScribe settings open`), or remove the comments. Any comment but ClassScribe's own header counts, a note between the header and the first `{` or a header line reworded included. |
| `ClassScribe 0.7.0 added 1 setting to your file: ... = ...`, or `ClassScribe 0.7.0 added 3 settings to your file:` with a line each | A later ClassScribe knows settings your file did not have yet, and your change (or `settings fill`) added them at their defaults. Nothing to do; change any of them as usual. |
| `The saved class "..." would not be usable: ...`, then `Nothing was changed.` | The change would have left that class unusable, for the reason given, so nothing was changed (exit code 2). Make the change the class can take; `ClassScribe class show "<name>"` lists its fields. A problem the class had before does not block a change. |
| `The change is saved, but the file still has problems that keep it from being used: ...` | Your change was saved, but other settings in the file were wrong before it and still are; the line names them. Fix them (`ClassScribe settings set`, or `settings open`); until then only recordings run, without those settings. |
| `No settings file yet; nothing to fill.` | `ClassScribe settings fill` found no settings file of your own and made none: ClassScribe makes it at your first change. Nothing to do (exit code 0). While your own file is missing, fill is refused with the missing-file line instead (exit code 1). |
| `Your settings file held an ElevenLabs key, which ClassScribe took out of it and out of appsettings.previous.json: ...` | A key written into the settings by hand is never used, and ClassScribe never writes one to any file. Make sure `ELEVENLABS_API_KEY` holds your key (see [Set up once](#set-up-once)). |
| `The file as it was is not kept in appsettings.previous.json, since it holds an ElevenLabs key, ...` | `settings reset --all` replaced a file that was not valid JSON and held a key, so no copy of it was kept. Nothing to do; the key is read from `ELEVENLABS_API_KEY`. |
| `WARNING  ElevenLabs key: not set (ELEVENLABS_API_KEY); recording works, transcribing will not.` (the menu's header) | Set the key for your Windows user in PowerShell, `[Environment]::SetEnvironmentVariable('ELEVENLABS_API_KEY', '<your key>', 'User')`, then close every ClassScribe window and open a new one. Classes record meanwhile, and can be transcribed later. |
| `Another ClassScribe is changing your settings just now; try again in a moment.`, `Your settings file ... keeps changing while ClassScribe changes it, as if another program saved it again and again. ...` | Try again in a moment; for the second, close the program that keeps saving the file (an editor, or a sync program). Nothing was changed. |
| `... is not a setting; did you mean ...? ClassScribe settings list shows them all.` | Check the setting's name; `settings list` shows them all (exit code 2). |
| `The environment variable ... sets ... for ClassScribe, so this change has no effect while it is set.` | The change was saved, but the variable wins. Remove the variable (in PowerShell, `[Environment]::SetEnvironmentVariable('<name>', $null, 'User')`), then open a new window. |
| `1 setting holds an older default; settings list shows it` | A later ClassScribe changed that setting's default; your file keeps the value it holds. `settings list` marks it (`newer default: ...`); `ClassScribe settings reset <setting>` takes the new default. |
| `Settings folder: ..., from CLASSSCRIBE_SETTINGS_FOLDER, as in a dry run; your own settings are not read in this window.` | That variable is set in this window, as for a dry run. Close the window, or run `Remove-Item Env:CLASSSCRIBE_SETTINGS_FOLDER`, to read your own settings again. |
| `WARNING  The saved class "..." cannot be used: ...` | Fix what the line names with `ClassScribe class set` (`--clear` empties a field) or in menu C (E), or remove the class. The other classes and choices are not held up. |
| `Mantiq is a saved class: ClassScribe record --class "Mantiq"` | A saved class's name was given without a source. Run the command the line gives, or give a source to record under that name with it (exit code 2). |
| `There is no saved class "...". The saved classes are: ...` | Check the name (`ClassScribe class list`); names are matched exactly, in any letter case. |
| A `WARNING` line under `Recordings and transcripts` in the menu | `The drive X:\ is not available`: plug the drive in, or change `Recording:OutputFolder`. `... is a file, so recordings cannot go there`: change `Recording:OutputFolder`. `Recording:OutputFolder uses %NAME%, which is not set`: set that variable, or write the folder out in full. |

### While recording

| What you see | What to do |
| --- | --- |
| `No sound for 60 seconds` | Check that the class is playing through the device named in the message and that Windows is not muted. With Bluetooth earbuds that play in one ear only, see the earbuds row below. |
| `Could not start capturing system audio yet` | Windows is still setting up the device, one just plugged in, say. ClassScribe tries again every second by itself. |
| `The folder ... cannot be looked into (...), so this recording goes to the next date folder` | The date folder for today, such as `Mantiq\2026-10-17`, is there but ClassScribe may not read it, as with one copied from another account. The class records into `2026-10-17 (2)` instead; nothing is lost. Give yourself access to the folder in its Properties > Security if you want to use it. |
| `uses an audio format this app cannot read`, `uses the unsupported encoding` | The device sends sound in a form ClassScribe cannot record, so the recording did not start. Choose another device with menu choice 6. |
| `Cannot capture an output device right now` | Windows has no usable output device at that moment, or its audio service is restarting. ClassScribe tries again every second and says so every 30 seconds; plug in or switch on a device. |
| `Nothing to record yet; waiting for ...`, `Still nothing to record` | The chosen device is not connected, the chosen app is not playing, or there is no output device. ClassScribe keeps looking; connect the device or start the sound, or press Q twice and choose something else with 6. |
| `"zoom" has closed; recording silence until it plays again` | The recorded app has no sound open any more. Its capture stays open and records silence until the app plays again; start the app, or rejoin the meeting. |
| `"zoom" is not among the apps with sound at this look, but ClassScribe could not read an audio session on "Speakers (...)" (GetSession: ...), which may be its; its capture goes on` | Windows would not give one audio session, so the app may still be playing there; it is not taken for closed, and its capture goes on. Said once per run at this level. Nothing to do; if it is followed by `has closed`, the app did close. |
| `Could not write the recording`, `Still cannot write the recording` | The disk is full or went away. Free some space; the recording continues by itself in a new file. After two failures in a row it tries every 30 seconds, and the audio in between is lost. |
| `The capture was held up for ...` | The recording probably has a gap there. If most of that time went on storing the audio, the disk was too slow: record to an internal drive. Otherwise the computer was very busy (close heavy programs) or went to sleep (keep it plugged in with the lid open, and set a longer sleep time in Windows' power settings). |
| `The laptop slept from ... to ...; nothing was recorded then` | The lid was closed, the power button pressed or Start > Sleep chosen, or Windows put the laptop to sleep by itself. The recording went on after the gap. During a class keep the lid open and the laptop plugged in. |
| `ClassScribe was held up from ... to ... (Windows paused it)` | Windows paused ClassScribe while the laptop stayed awake, as it may with programs to save power on battery. Nothing was recorded then. Plug the laptop in, and on battery keep `Recording:KeepScreenOn` at `OnBattery` or `Always`. |
| `No audio has arrived from "..." for ...` | The capture stopped handing over audio although it should, and ClassScribe reopened it in a new segment file. If it keeps coming back, restart the class app, or choose another device or app with menu choice 6. |
| `chrome is taking sound from the Bluetooth microphone "Headset (...)": that puts the headset or earbuds into call mode, and the class's sound may be going to them in a way Windows cannot record, so this recording may be silent. In chrome, set the microphone to the laptop's built-in microphone ("Microphone Array (...)"), or to another that is not Bluetooth` (`chrome on a Bluetooth microphone` in the title) | The class page uses a Bluetooth headset's or earbuds' microphone, which puts them into call mode: Bluetooth then carries the class to them past Windows' loopback, and ClassScribe records silence. In Chrome, click the microphone icon at the right end of the address bar and choose the laptop's `Microphone Array`, or any microphone that is not Bluetooth (also under Settings > Privacy and security > Site settings > Microphone, or in the class page's own microphone setting); `chrome has let go of the Bluetooth microphone ...` follows within 10 seconds. The look is made every 10 seconds in every recording of the class: with a device recorded and no app followed, any app counts; with an app recorded or followed, that app alone. |
| Only one earbud plays, or the sound has dropped to call quality, and the recording is silent (`No sound for 60 seconds`, `no sound 2 min` in the title) | The earbuds are in call mode because an app is using their microphone, most likely the class page, and Bluetooth carries the class's sound to them past Windows' loopback, so nothing reaches the recording. Switch the class page's microphone to the laptop's `Microphone Array`, as in the row above; the earbuds leave call mode and the recording picks the class up again. The silent stretch stays silent in the recording. ClassScribe gives the warning above when it sees the class app on a Bluetooth microphone; if no warning came, the app holding the earbuds' microphone is not the one it watches (with an app recorded or followed, that app alone counts): the microphone check (menu choice 8) shows which app has it. |
| `Windows did not accept the request to keep the laptop awake`, `... to keep the screen on` | ClassScribe records or uploads all the same, but the laptop may go to sleep if it is left alone. Keep it plugged in, with sleep set to Never in Settings > System > Power & battery. |
| `Windows has not finished changing the audio device for 20 seconds` | A sound driver stopped answering as you stopped. ClassScribe stopped without it, and everything recorded so far is kept. If it happens again, restart Windows. |
| `No audio was captured`, `Nothing audible was captured from "..."` | The recording holds no sound, or only digital silence. Nothing was sent to ElevenLabs or charged, and transcribing it again would find the same silence; `record-only` ends with exit code 1 too, and the silent file is kept. Before the next class, check what is recorded (menu choice 6, `Recording:Device` or `Recording:App`) and that Windows is not muted. |
| `Nothing audible was captured in this part; the earlier parts of ... are kept` | A continued recording's new part holds no sound, but the recording as a whole does, so it goes on to be transcribed. Check what is recorded before the next class. |
| `Still paused, for 5 minutes now; P records again` | A reminder, every 5 minutes while paused; from 10 minutes the title says `PAUSED 10 MIN` in red. Press P when the break ends. |
| `The class seems to be playing while paused; P records again` | Sound has been arriving for 30 seconds while the recording is paused. Press P, or nothing of it is recorded. |
| `Only about ... of recording fit on D:\ (... free). Free some space there before a long class` | At the start, the drive has room for less than 3 hours of recording. Free some space, or record to another folder. |
| `Only about ... of recording still fit on D:\ ...`, `Less than a minute of recording still fits on ...` | Every 5 minutes while less than 30 minutes fit, with `DISK NEARLY FULL` in the title. Free some space on that drive now, or the recording stops being saved. |
| `This recording stops by itself in 15 minutes, after 9 hours (Recording:StopAfterHours)`, `This recording stops now, after 9 hours ...` | The safety stop: ElevenLabs takes at most 10 hours in one file. Start a new recording to record more. |
| `The time given with --minutes 90 is up; stopping the recording` | What you asked for. With `record`, the transcript follows. |
| The window was closed mid-class | With the published copy the recording goes on in the background, and a minimized ClassScribe window comes back 3 seconds later; start ClassScribe, or press Enter in that window, to see it. Under `dotnet run`, or with `Recording:SeparateProcess` off, the recording ended, and the audio up to that moment is in the recording's folder: the next start offers C to continue it and T to transcribe it. |
| `Recording in this window ...`, `ClassScribe was started in a way that ends every program it starts when it ends` | The recording runs in this window, so closing it ends the recording. For a recording that outlives the window, start the published `ClassScribe.exe` (see [Set up once](#set-up-once)) with `Recording:SeparateProcess` on. |
| `Only one recording at a time: ...` | Another recording is running. Start ClassScribe (or press A in the menu) to see it, or stop it first. For one in another ClassScribe window, that window shows it; closing that window ends it. |
| `The recording "..." ended unexpectedly at about ...` | The report at the start: the background process is gone. C continues the recording, T transcribes what was recorded, F forgets it. If you did not stop it, look at Bitdefender's Notifications, or at the cause the report names. |
| `Press Enter to control the recording from this window.`, `ClassScribe opened this window by itself. Keys do nothing here until you press Enter.` | This window was opened by ClassScribe itself, so keys do nothing in it until you press Enter. |

### The microphone, while recording

| What you see | What to do |
| --- | --- |
| `Microphone: waiting for the microphone "OBSBOT"`, then `Waiting for the microphone "OBSBOT"; the class is being recorded` every 30 seconds, with `(no microphone)` in the title | The named microphone is not connected, or Windows lists no microphone that may be used by itself. Plug it in, or name another (menu choice 7, `Recording:Microphone`); the class is recorded meanwhile. In person the title says `NOT RECORDING` instead. |
| `waiting for a microphone that is not Bluetooth ("..." is used only when named in Recording:Microphone)` | Only a Bluetooth microphone is connected. Name it to use it, knowing that it switches the earbuds to call quality, or plug in another. |
| `waiting for a microphone other than "Stereo Mix ...", which records what the computer plays` | The name you gave matches an input that records the computer's own sound, which is never recorded. Name a real microphone. |
| `The microphone "..." has been gone for 10 s; recording "..." instead until it returns`, `The microphone "..." is back; recording it again` | The chosen microphone was unplugged; another stands in, and the recording moves back when it returns, each time in a new file. |
| `Recording the microphone "OBSBOT ...", the microphone the class app uses, instead of "Microphone Array ..."` | The class app listens on another microphone than the one ClassScribe picked, so ClassScribe moved to it, which is what the Windows mute needs. Nothing to do. |
| `The class app uses "Stereo Mix ...", which records what the computer plays; recording "..." still` | The class app listens on an input that would record the class twice, so ClassScribe keeps its own microphone. Pick a real microphone in the class page. |
| `Could not start capturing the microphone yet; trying again every 10 seconds`, `Cannot capture the microphone right now; trying again every 10 seconds` | Windows is still setting up the microphone, or another program holds it exclusively. ClassScribe tries again by itself. |
| `The microphone "..." could not be opened without Windows' sound processing (0x...); the next attempt opens it with Windows' sound processing` | RAW was asked for, but the stream would not open with it; within 10 seconds ClassScribe opens the microphone with Windows' sound processing. Nothing to do; `Recording:MicrophoneProcessing` set to `Default` skips the try. |
| `The microphone cannot be recorded: ... uses an audio format this app cannot read (...); the class is still being recorded`, or `The class cannot be recorded: ...; the microphone is still being recorded` | That track's device sends sound in a form ClassScribe cannot read, so it is left out of this run; the other track is recorded. Choose another device or microphone before the next class. When neither can be recorded the run fails with the second line, without `; ... is still being recorded`. |
| `Your microphone was not recorded in this run: no microphone file was written. The class was recorded and goes on to the transcript` (or `... and is kept`) | The microphone's track was left out, or never opened. The class is transcribed on its own, with exit code 0. |
| `No audio was captured from the class. Was anything playing? Your microphone's audio is kept in ... To transcribe it on its own, run: ...` | The class track holds nothing, but the microphone's does. The command on the line transcribes the microphone's files alone. |
| `Lenovo Vantage's microphone noise cancelling is on (mode 1, probably "Multiple Voices")`, `... has switched on ...`, `... has changed to mode ...` | Set Vantage > Device Settings > Sound > Input to Off: it can cut out a lecturer across the room. `Lenovo Vantage's microphone noise cancelling is now off.` follows within 30 seconds when you do. |
| `Windows lowers other sounds by 80% while a microphone is open ...` (or `by 50%`, or `Windows mutes other sounds ...`) | Set Sound > More sound settings > Communications to Do nothing, or the class gets quieter whenever a microphone is open. |
| `Lenovo Vantage's microphone mode could not be read from the registry, so it is not checked: ...`, `Windows' ducking setting could not be read ...` | The registry refused. That setting is not checked this run, and nothing else changes. |
| `The microphone "..." is Bluetooth: using it switches the earbuds to call quality, for the class too. The laptop's microphone array avoids that.` | You named a Bluetooth microphone, or the class app uses one. Name the laptop's own array, or accept the call quality. |
| `The class plays on "Speakers (...)", so the microphone records it a second time (an echo). Use headphones.` (`echo: use headphones` in the title) | Plug in headphones, or the transcript gets the class twice. |
| `The microphone starts switched off (V switches it on)` | `Recording:ClassAppMute` is `Unknown` or `Invisible`: your voice is recorded after you press V, and only while the class app holds the microphone. |
| `chrome has not opened the microphone for 5 minutes; your voice is not recorded until it does. Join the class, or check that the page may use the microphone` (`MIC WAITING`) | Join the class, or allow the microphone in the page. The class is recorded meanwhile. |
| `chrome uses "OBSBOT ..."; ClassScribe records "Microphone Array ..."` in the status line | The class app and ClassScribe use different microphones for a moment; ClassScribe moves to the app's at its next look. |
| `Cannot read chrome's microphone session: ...; your voice is not recorded until the sessions can be read again` (`CANNOT READ THE MICROPHONE SESSIONS` in red after 10 seconds) | Windows' audio service is restarting, or refused the look; the words after the colon say what could not be read, whose session it was when Windows said (`session 2 of 3 (Zoom, pid 9090) not readable: GetState: ...`) and which call failed. ClassScribe keeps looking four times a second, and the class is recorded all the while. Another app's session that cannot be read does not cause this; the followed app's own (with `auto`, any app's) does when Windows' usage record says nothing of the app either. |
| `Windows would not give the state of zoom's session on "Microphone Array ..." (...); until it does, Windows' microphone usage record says whether zoom is listening` | The followed app's own session on your microphone (with `auto`, any app's, named by its program, such as `Zoom`) cannot be read beyond its process, as with the Zoom app in a meeting; Windows' record of which apps use the microphone stands in for it, so your voice is recorded while the record says the app uses the microphone. Nothing to do. |
| `The microphone watch stopped: ...; your voice is not recorded for the rest of this run` | The watch failed in a way it did not expect. The class is still recorded; start the next run as usual. |
| `Could not look at the class app's microphone sessions now; the looks so far decide` | A last look failed, at V or at the stop; the watch's own line says why. |
| `chrome let go of the microphone at 19:12:03 and took it again at 19:12:40. If that was you muting and unmuting in the class page ...` | If those were your clicks, your mute is visible to Windows: set `Recording:ClassAppMute` to `Visible`. If it was a rejoin or a device change, leave it. |
| `Only silence from the microphone "..." for 2 minutes although chrome is listening and Windows is not muting it. Check Settings > Privacy & security > Microphone (Let desktop apps access your microphone), and the microphone itself` | The app listens and nothing is muted, yet the microphone hands over exact zeros: Windows' privacy switch, or the microphone itself. |
| `Only silence from the microphone "..." for 2 minutes: it is muted in Windows (the microphone key, or Sound settings > Input)`, or `... Check that it is not muted in Windows ... and that Windows lets desktop apps use it (Settings > Privacy & security > Microphone)` | Unmute it with the microphone key, or check the privacy switch. In person this is `ONLY SILENCE FROM THE MICROPHONE` in red, since the whole recording is then silence. |
| `The microphone of the camera "..." has sent only exact zeros since it opened, 1 minute ago. If the camera's light is off, the camera is asleep and its microphone off: unfold its lens or wake it, and turn off Device Auto Sleep in OBSBOT Center or turn on Recording:WakeCameraMicrophone. If the light is on, the room is just quiet: this camera sends exact zeros in silence`, with `(only zeros from the webcam's microphone)` in the title | A webcam's microphone has sent nothing but exact zeros since it opened. Look at the camera's light: off, wake the camera (unfold its lens) and turn off Device Auto Sleep for the next class, or turn on `Recording:WakeCameraMicrophone`; on, nothing is wrong. In person it is an Alert in red. |
| `The microphone of the camera "..." has sent only exact zeros since it opened, 1 minute ago, although its camera is open and awake. Check that its microphone is on in OBSBOT Center, and that Windows lets desktop apps use the microphone` | ClassScribe holds the camera's picture and its frames arrive, so the camera is awake, yet its microphone sends nothing: its own microphone switch (OBSBOT Center), or Windows' privacy switch. |
| `The microphone of the camera "..." has sent only exact zeros for 2 minutes. If its light is off, the camera has fallen asleep (an open microphone does not keep it awake) and your voice is not being recorded: turn off Device Auto Sleep in OBSBOT Center, or turn on Recording:WakeCameraMicrophone. If the light is on, the room is quiet and nothing is lost` | Said at most once a recording, and never while ClassScribe holds the camera's picture with its frames arriving. Light off: wake the camera, and the voice from then on is recorded. Light on: a quiet class. |
| `The microphone being recorded belongs to the camera "...", so ClassScribe keeps the camera's picture open, shared, until the recording ends: the camera cannot fall asleep and silence its microphone. Nothing of the picture is kept; the camera's light stays on` | `Recording:WakeCameraMicrophone` is on and the microphone is a webcam's. Nothing to do; `Let go of the picture of the camera "..."` follows at the stop, before the upload. |
| `The camera "..." has sent no picture yet; its microphone records meanwhile` | The held camera sent no frame within 5 seconds, so the microphone was opened without waiting longer. Look at its light; the camera is opened again by itself. |
| `The camera "..." stopped sending its picture. Is its lens folded? Folded, its microphone is off. ClassScribe opens it again every 5 seconds`, with `(camera's picture stopped)` in the title, then `The camera "..." is open again` | The held picture stopped for 3 seconds: a folded lens, the camera unplugged, or another app holding it alone. Unfold or plug it in; ClassScribe opens it again by itself. In person it is an Alert in red. |
| `The camera "..." could not be opened: ... Without its picture open the camera may fall asleep, and its microphone then sends only silence. Turn off Device Auto Sleep in OBSBOT Center, or record another microphone. ClassScribe tries again every 5 seconds`, with `(camera not opened)` in the title | The words after `opened:` say why: Windows' camera privacy settings or a security program (allow ClassScribe in Bitdefender's Webcam Protection, then `camera-check` with Y), another app holding it alone, the camera not connected, or the call that failed with its code. The microphone is recorded all the same. |
| `The camera process holding "..." failed: A call on the camera's route did not return within 30 seconds` | A call to open the camera got stuck inside Windows, often while a security program's prompt (Bitdefender's Webcam Protection) waits for an answer. ClassScribe starts another camera process by itself, and the microphone records meanwhile. If it repeats, run `camera-check`, type Y and answer the prompt. |
| `Camera process: Letting the camera go did not return within 30 seconds` | At the end of the hold, letting the camera go got stuck inside Windows. The camera process ends right after, and Windows lets the camera go with it. Nothing to do unless it repeats; then unplug the camera and plug it in again after the class. |
| `Could not tell whether the microphone "..." belongs to a webcam, so no camera's picture is held for it yet; the microphone is recorded all the same, and each look asks again: ...` | With `Recording:WakeCameraMicrophone` on, Windows did not list the cameras (within 5 seconds, or with the error after the colon). The microphone is recorded; ClassScribe asks again at the microphone's next look (every 10 seconds) and holds the picture once the list comes. Meanwhile look at the camera's light: off, wake the camera. |
| `The camera's hold could not look at its camera process; it tries again in 5 seconds, and the microphone is recorded all the same` | Not expected: something failed while starting or watching the camera process. The microphone is recorded, and the hold tries again by itself. |
| `The camera "..." opened, shared, and sent its picture: ... frames in 5 seconds.` (`camera-check`) | The camera opens as a lesson and the hold will open it. |
| `The camera "..." could not be opened: ...`, `The camera "..." opened but sent no picture in 5 seconds; is its lens folded, or is it asleep?`, `Windows did not start ClassScribe's camera process: ...`, `The camera process did not finish within 75 seconds, so it was ended` (`camera-check`, exit 1) | The numbered calls above the line show which one failed, with its code and Windows' words. Allow ClassScribe if a security program asked, unfold the lens or wake the camera, then run the check again. |
| `No sound from the microphone "..." for 60 seconds. Check that it is not muted in Windows (the microphone key) and that Windows lets desktop apps use it ...` | In person, a minute without anything audible. Check the microphone, its mute and the privacy switch. |
| `No audio has arrived from "Microphone Array ..." for 5 seconds although it should; ... Reopening it` (`(no audio from the microphone)` in the title) | The microphone's capture stopped handing over audio and was reopened in a new file. If it keeps coming back, try another microphone. |
| `A packet arrived ... s ahead of the track, further than expected, and no time ClassScribe did not run was found; it is written as it comes` | Not expected; the next second's padding catches the track up. |
| `Could not keep the microphone on the clock this second; trying again at the next` | The computer's clocks could not be read for a moment; the next second pads the track. |
| `Writing ... s of silence before a packet that arrived that far ahead of the track; no time ClassScribe did not run was found` | After the 30-second pause of a disk failure, the gap is written as silence at its place. Nothing to do. |
| `There is no microphone in this recording` | V was pressed in a recording of the class alone. |
| My voice is in the recording although I was muted in the class page | The app does not let go of the microphone when you mute, and `Recording:ClassAppMute` says `Visible`: set it to `Invisible`, and mute with the microphone key, or press V. |

### The microphone check

| What you see | What to do |
| --- | --- |
| `The microphone check cannot create its log in ...` (exit code 1) | The folder cannot be made or written. Give another with `--log-folder`, or free some space. |
| `ERROR    Windows' list of microphones could not be read: ...`, `ERROR    Windows' microphone usage record could not be read: ...` | Windows' audio service is restarting, or the registry refused. The check keeps trying, and a `NOTE` says when it reads again. |
| `NOTE     no check for ... s: the laptop slept or the check was held up` | A change that came and went in that time is not in the log. |
| `SELF     ClassScribe's own process has a session on this microphone.` | Should never appear; it would mean ClassScribe itself opened the microphone during the check. |

### The camera

| What you see | What to do |
| --- | --- |
| `--camera records only in person in this build: give it with --in-person` (exit code 2) | The camera goes with your microphone alone, in person: use `--in-person` as the source. |
| `Camera: "..." (USB), 1280x720 at 30 frames a second, 5 Mbit/s, hardware encoder (...). Its light is on while it records; P lets it go during a pause` | The camera opened and its picture records. Nothing to do. The rate is the camera's own as it runs, which another app sharing it may have set lower, up to 30; `at up to 30 frames a second` when Windows does not say it. |
| `The camera "..." sends its picture, but it cannot be written: its frames come at ... ClassScribe opens it again every 2 seconds. The sound records on`, with `CAM WAITING` in the title (an Alert) | Frames arrive from the camera, but in a size or a format ClassScribe cannot write, so none of the picture is kept. Close other apps using the camera, unplug it and plug it back in; if it goes on, record without the camera and report the line. The sound was never interrupted. |
| `The camera "..." stopped sending its picture; the sound records on. Plug it back in`, with `CAM WAITING` in the title (an Alert) | The camera was unplugged, folded, or went to sleep. Plug it in or unfold it; ClassScribe opens it again every 2 seconds into the same piece, and says `The camera "..." is recording its picture again`. The sound was never interrupted. |
| `The camera "..." gave no picture. Windows allows it, so a security program may be blocking ClassScribe (Bitdefender: Privacy > Video & Audio Protection > Webcam Protection). The sound records on` | The camera opened but sent nothing for 5 seconds. Allow ClassScribe in Bitdefender's Webcam Protection (run `camera-check` and type Y to be asked while you watch). |
| `The camera "..." was refused: ... E_ACCESSDENIED ...` | Windows' camera privacy switch, or a security program, refused it. Turn on Let desktop apps access your camera in Settings > Privacy & security > Camera, and allow ClassScribe in the security program. ClassScribe tries again every 2 seconds. |
| `The camera "..." is held by another app (...). Close it there; ClassScribe takes the camera as soon as it is free` | Another app holds the camera alone (ClassScribe only ever shares it). Close the camera in that app. |
| `The camera "OBSBOT" is not connected, so its picture is not recording; ClassScribe looks for it every 5 seconds` | No camera of that name is plugged in. Plug it in; ClassScribe never records another camera in its place. |
| `The camera's picture has been black for 10 seconds: is its lens covered or folded?`, with `CAM DARK` in the title (a Notice) | The picture is black, often a lens cap or a folded lens. The picture records on; it clears when the picture brightens. |
| `ClassScribe's camera process for "..." stopped working; ClassScribe starts it again in 2 seconds. The sound records on` | The camera's own process ended, as after a driver fault; a new one writes a new piece from that moment. |
| `The camera stopped working three times; the rest of this lesson is recorded without its picture. The sound records on` | The camera's process ended three times within 10 minutes. The lesson's sound records on to its end; record the next lesson after plugging the camera in again, or restarting the laptop. |
| `The hardware encoder could not start (...); the camera's picture is encoded by Windows' software encoder instead` | The graphics' encoder refused; Windows' own encoder writes the picture instead, using a little more of the processor. Nothing to do. |
| `The camera's picture could not be written (writing its picture failed (...)). The sound records on` | The disk or the encoder failed while writing the picture. ClassScribe starts a new piece, with Windows' software encoder when the encoder failed. Check the disk's free space. |
| `Little room is left on D:\ (... free), so the camera is let go: the rest of this lesson is recorded without its picture. The sound records on` | The disk has room for less than 30 minutes of sound and picture together. Free some space; the sound records on. |
| `The camera process did not finish its piece within 15 seconds of the stop, so it was ended; its piece is readable to its last second or so` | At the stop, the camera's process hung. The sound was saved first; the piece keeps all but its last moments. |
| `The camera process did not take a line within 5 seconds, so it was ended` | The camera's process stopped answering, stuck inside Windows. While recording, ClassScribe starts it again in 2 seconds into a new piece; at the stop, the piece keeps all but its last moments. The sound was never interrupted. |
| `The camera process could not be let go within 5 seconds; it ends with ClassScribe at the latest` | The camera's process could not be ended at once, often a camera driver stuck inside Windows. Nothing to do: the stop goes on, and the process ends when ClassScribe does. If the camera's light stays on afterwards, unplug the camera. |
| `The camera's picture did not stop within 25 seconds of the sound's stop; the recording goes on without it, and its piece is kept as far as it was written` | The camera hung at the stop. The sound was saved first, and the transcript and the next class go on; the piece keeps all but its last moments. |
| `The camera's track did not let go within 25 seconds; it is left to finish by itself` | The same, when the recording's files were closed. Nothing to do. |
| `The picture is placed 60 ms earlier for "..." (Recording:CameraOffsets)` | The camera's offset from your settings applies. See [Recording with the camera](#recording-with-the-camera) to measure it. |
| `--resolution needs --camera` or `--resolution takes 480p, 720p or 1080p, ...` (exit code 2) | Give `--resolution` only with `--camera`, and one of the three sizes or a width and a height such as `1024x576` within the limits the message gives. |
| `PROBLEM  Windows does not let desktop apps use the camera: ...` (camera check, exit code 1) | Turn on Let desktop apps access your camera in Settings > Privacy & security > Camera (the line names the page), then run the camera check again. |
| `PROBLEM  No H.264 video encoder is installed, ...` | A Windows N edition lacks it: add the Media Feature Pack in Settings > System > Optional features. |
| `NOTE     Bitdefender Antivirus watches the camera: ...` | When Bitdefender asks about ClassScribe and the camera, allow it (Bitdefender: Privacy > Video & Audio Protection > Webcam Protection). |
| `The cameras could not be listed: The camera needs Windows 10 version 2004 (build 19041) or later; ...` | The camera needs a newer Windows; everything else in ClassScribe works as before. |
| `The lesson's video: ....mp4 (1:02:13, 2.4 GB), with its sound. Watch it before you publish it.` | The lesson's video is made and checked. Watch it before you share it: everything the microphone heard while not paused is in it. |
| `Deleted the camera's 2 pieces of ...: the lesson's video passed its check, and lesson videos do not go to Google Drive (Drive:LessonVideos), so the video is the one copy of the picture kept` | The pieces went once the video passed its check. Nothing to do; keep the `.mp4`. |
| `The camera's 2 pieces of ... stay on this laptop until free-space finds the video safe on Google Drive (Drive:LessonVideos)` | Lesson videos go to Drive, so the pieces are kept until free-space (9 > L) has checked the video's copy there. |
| `... was encoded differently from ... (another encoder or another picture size), so the whole picture is encoded again, which takes longer than joining the pieces as they are` | The pieces came from two encoders (the software one took over), or a continued lesson changed its size. Wait: the video is made all the same, more slowly. |
| `The lesson's video was not made: The lesson's video did not pass its check (...). The camera's pieces are kept. To make it again, free: ... finish-video "..."` | Something in the video was wrong (a picture that does not decode at a join, its length). The pieces are kept; run the command, or menu choice 3's V. If it fails again, report the line. |
| `The lesson's video was not made: ClassScribe's camera process did not say how far it had got for 2 minutes, so it was ended. ...` | The camera process hung while it made the video. The pieces are kept; run `finish-video` with the command given. |
| `The lesson's video was not made: ....mp4, the older video, is open in another program, such as a video player, so the new one, which passed its check, could not take its place (...). It stays as ....mp4.partial until the next making. Close it, then make the video again. ...` | A player (or another program) had the older video open while it was made again; ClassScribe tried three times, a second apart. Close the player and run the `finish-video` command given, or menu choice 3's V. The older video and the pieces are as they were. |
| `... cannot be read (...), so it is left out of the lesson's video`, then `... could not be read, so its picture is in no video; it stays on this laptop. Delete it by hand once you no longer need it` | That piece holds no picture Windows can read, often one whose camera process ended before it wrote anything. The video is made from the others; the piece is kept, small as such a piece usually is. Delete it by hand when you are sure you do not need it. |
| `The lesson's video was not made: ... could not be opened now (...), so the video was not made without its picture. ...` | Another program (a scanner, a player) held a piece, or the older video, while the video was made. Every piece is kept; run the `finish-video` command given once it has let go. |
| `The lesson's video was not made: ....mp4, the older video, cannot be read (...), and its picture before the first camera piece is in no piece, so a new video would lose it. ...` | The older video of a continued lesson is damaged, and its first part's pieces are gone. Move the older video out of the lesson's folder (keep it), then run `finish-video` to make a video from the pieces alone. |
| `The lesson's video was cancelled; the camera's pieces are kept. To make it later, free: ...` | Ctrl+C after the stop cancelled the video with the transcript. Run the command when you want the video. |
| `The camera wrote no picture in this lesson, so it has no video to make; the sound is kept` | The camera never gave a picture (not connected, refused). The lesson's sound is kept and transcribed as usual. |
| `The sound of this lesson is on Google Drive only, so its video cannot be made again here.` (finish-video, exit code 1) | free-space deleted the `.wav` files. Download them from Drive into the lesson's folder, then run `finish-video` again. |
| `... has its video, ....mp4, and no camera pieces left to make it again from.` or `... has no camera pieces, so it has no picture to make a video of.` (exit code 1) | Its pieces were deleted once the video passed its check, or it was recorded without the camera. Nothing to make. |
| `The lesson video of "..." was not finished; its sound and its camera pieces are kept.` (the report at the start) | ClassScribe stopped while it made the video. Press V to make it now, free, or run `finish-video`. |

### The background recorder and its windows

Lines that come from the background process show in every window that shows the recording, and in
the recording's `.log`; the ones about the link show in the window alone.

| What you see | What to do |
| --- | --- |
| `Recording in the background: closing this window does not stop it` | Nothing: that is the usual start. Esc leaves the view; start ClassScribe again to see it. |
| `"Kalam" is being recorded already, since ...; showing it.` | `record` named a recording that is running, so the window shows it instead of starting another. |
| `Starting the recorder once more.`, then `Recording in this window instead. ...` | The background process would not start twice. The reason is on the line: Windows' words, such as `Windows did not start the recorder: Access is denied (error 5)` or `... file contains a virus (error 225)`, which is Bitdefender's block; `The recorder (process N) did not answer within 20 seconds`; or `The recorder ended before it answered, with exit code N`, with the last lines of `recorder.log`. The class is recorded in this window, so do not close it. Before the next class, add the exception from [Set up once](#set-up-once) and look at Bitdefender's Notifications. |
| `Windows did not say whether ClassScribe runs in a job (...)`, `Windows did not let the recorder start outside the job ClassScribe runs in (...)` | ClassScribe could not make sure that closing the window would leave a recorder running, so the recording runs in this window to be safe. Start the published `ClassScribe.exe` from its shortcut. |
| `The recorder could not take its request: ...`, `The recorder could not make the pipe ClassScribe-<run id> for ClassScribe's windows (...), so it did not record`, `The recorder could not write its run file (...), so it did not record`, `The recorder could not be put together: ...` | The background process gave up before recording, and the window records the class itself after one more try. Most often `%LOCALAPPDATA%\ClassScribe` cannot be written (a full disk), or something else holds a pipe of that name. |
| `Could not take the lock that allows one recording at a time (...)`, `Could not write this recording's run file (...)` | `%LOCALAPPDATA%\ClassScribe` cannot be written. The recording goes on, but without the lock, so start no other recording until it stops, and without the run file, so an unexpected end cannot be reported. Free some space on that drive. |
| `Could not bring the run file ... up to date (...)` | The recording itself is not affected; the file is written again with the next update, and until then a start of ClassScribe may report the run as it last was. |
| `Could not look at the runs in ...` | ClassScribe cannot report unexpected ends until `%LOCALAPPDATA%\ClassScribe\runs` can be read. |
| `... lines of this recording's log were not saved to its file. ...` | The recording is complete; some lines of its `.log` are missing, for the reason given, such as a disk that did not keep up or a file another program held. |
| `Could not show a line of this log in a ClassScribe window; the line is kept in this log` | A fault in the link to the window; the recording and its `.log` are not affected. |
| `The window was closed; the recording goes on. A ClassScribe window comes back, minimized ...` | The first line of a window that came back after the last one was closed. Press Enter in it to control the recording, or leave it minimized. |
| `Opening a ClassScribe window, minimized, because ...` | The first line of a window that came back for a problem, which the rest of the line names, or because the run failed. Press Enter, then read the lines above. |
| `Could not open a ClassScribe window for this recording: Windows did not start the alert window: ... (error N). The recording goes on` | The recording is fine, but no window came back to show it. Start ClassScribe yourself to see it; if the error names a virus, check Bitdefender's Notifications. |
| `The ClassScribe window that came back was closed within 2 minutes, so a closed window brings none back again in this run` | Just a note: closing the window twice means you do not want it back for a mere close. A problem still brings one. |
| `The recorder ended unexpectedly at ...; continuing the same recording` | The background process ended without being asked, most likely stopped by Bitdefender's Advanced Threat Defense; ClassScribe continued the recording as its next part. Add the exception described in [Set up once](#set-up-once). |
| `The recorder ended unexpectedly again at ..., the second time within 10 minutes, which most likely means Bitdefender is ending it` | The recording continues in this window instead, so do not close it. Check Bitdefender's Notifications (Protection > Advanced Threat Defense) and add the exception. |
| `ClassScribe opened this window by itself, so the recording is not continued in it.` | The background process was lost, or would not start, while only the window that came back by itself showed the recording. Its audio is kept: start ClassScribe, and the report offers C to continue it and T to transcribe it. |
| `The recorder ended unexpectedly at ..., after the time --minutes gave it had run out, so the recording is not continued` | The recording was due to stop anyway. The command on the line transcribes it. |
| `The recorder ended unexpectedly while it stopped the recording ...`, `The transcript of "..." was not finished; its audio is complete` | The background process was lost after the recording had stopped, or during the upload. The audio is complete: the command on the line transcribes it. If the upload had finished, ElevenLabs may have billed it already, so look at its usage page first. |
| `Lost the connection to the recording; it carries on` | The link to the background process broke and could not be made again within 10 seconds, but the process still runs. Start ClassScribe again to see it. |
| `The recorder is not answering (process N, started HH:mm)` | The background process runs but has sent nothing for 10 seconds. W keeps waiting; S stops it through its stop event, which does not need the link; its audio so far is on disk. `The recorder is answering again.` follows when it does. |
| `<key> did not reach the recording: the connection to it is being made again. Press it again in a moment.` | The key was pressed while the link was being made again. Press it again. |
| `Reconnected to the recording.`, `Some lines of the log are no longer held here; the whole log is in ...` | The link was made again. The background process keeps its last 20,000 lines for windows; older ones are in the recording's `.log`. |
| `This recording was started by another build of ClassScribe (...)` | A window of one build cannot show a recording made by another. Start the `ClassScribe.exe` named, or press S to stop the recording; the audio so far is kept. |
| `Start ClassScribe the same way as the recording (without Run as administrator) to see it.` | A window run as administrator cannot show a recording started without, and the other way round. |
| `Asked the recording to stop; its audio so far is kept.`, `Another window asked for this recording to stop` | S was pressed in a window that could not reach the recorder; the recorder stops as after Q twice. `Could not ask the recording to stop: no recorder listens for it, so it may have ended already.` means there was nothing left to stop. |
| `Something other than ClassScribe made an event named Local\ClassScribe.Stop....` | S from another window will not work for this recording; Q pressed twice in a window that shows it still stops it. |
| `Nothing is being recorded or transcribed in the background now.`, `There is no ClassScribe run ...` | The hidden `attach` command, or A, found nothing to show. |
| `ClassScribe is being closed; the recording so far is saved` | The window of a recording running in the window was closed. The audio up to that moment is kept, and the next start offers C and T. |
| `Windows is asking to end the session (a restart or a shutdown); the recording so far is saved`, `Asked Windows to wait, and to show why: ClassScribe is recording a class` | You, or an update, started a restart, a shutdown or a sign-out. Windows' screen names ClassScribe: Cancel keeps recording; `Windows is not ending the session after all; the recording goes on` follows. Restart anyway finishes the files (`Windows is ending the session now ...`), and the next start offers to continue. |
| `Windows did not let ClassScribe watch for a restart, a shutdown or a sign-out (...)`, `Windows did not accept the request to ask first before ending the session (...)` | Recording goes on, but a restart or a shutdown will not wait for ClassScribe. Stop the recording yourself before you restart. |
| `Could not show the recording's state in the window's title`, `... on Windows Terminal's tab and taskbar button`, `... on the window's taskbar button`, `Could not flash the window's taskbar button` | Harmless, once per run: the log still says everything. |
| `No ClassScribe window was showing this recording at its end, so the recorder opens the results itself` | Nothing to do: the transcript appearing is the sign that the work is done. |

### Transcribing

| What you see | What to do |
| --- | --- |
| `No ElevenLabs API key found` | Set the key (see [Set up once](#set-up-once)), then close and reopen the terminal, or quit every VS Code window and start VS Code again. The recording is kept: transcribe it with menu choice 3. |
| `ElevenLabs rejected the API key` | Check the key in `ELEVENLABS_API_KEY`, and that it may use Speech to Text. After changing it with `setx`, close and reopen the terminal; in VS Code, quit every VS Code window and start VS Code again, because a new terminal inside a running VS Code still has the old key. |
| `does not have enough credits` (HTTP 402) | The ElevenLabs account has run out of credits. Top up or change the plan on elevenlabs.io, then transcribe with menu choice 3. |
| `is rate limiting this account` (HTTP 429) | Too many requests in a short time, or at once. ClassScribe has already tried again, with growing pauses; wait a few minutes, then use menu choice 3. |
| `ElevenLabs did not answer in time` | The time limit for the upload, the transcription and the whole answer ran out: at least `ElevenLabs:TimeoutMinutes` (60 by default), and more for a longer upload, as the `Uploading ...; time limit ... minutes` line said. The audio is untouched: transcribe again with menu choice 3. On a slow line, first lower `ElevenLabs:SlowestUploadMbps`, which gives the next try more time. |
| `... already has a transcript (...). Transcribing it again is billed again, so nothing was sent` | `transcribe` on the command line, or a file dropped on the exe, would bill that recording a second time. Run the `rebuild` command the next line gives, if there is one, to make its transcript again for free, or the `transcribe ... --again` command, to transcribe it again, billed again. In the menu, choices 3 and 4 ask R or Y instead. |
| `ElevenLabs' answer to an earlier try at ..., which has been paid for, is kept in ...` | The same for a recording whose paid answer is kept without a transcript: the `rebuild` command given lays that answer out for free, and `--again` transcribes the recording again, billed again. |
| `... is being recorded now; transcribe it once it has stopped` | That recording is still being recorded, in this window or another. Stop it first, then transcribe it. |
| `Also in this folder, without a transcript: ...` | The folder holds other recordings without a transcript, such as the first half of a class split by a crash. The command after each transcribes it, or lays out its kept answer for free. |
| `There is no ClassScribe recording in ...` | The folder holds no `.wav` file named as ClassScribe names its recordings, neither in it nor in its date folders (such as `2026-10-17`). Give the course's folder, the class's date folder, or the file itself; the recordings folder itself holds courses, not recordings. |
| `... holds ... of audio, and ElevenLabs takes at most 10 hours per file` | Nothing was prepared or sent. Copy some of its `.wav` files into a folder of their own and transcribe that folder, then the rest the same way. With your microphone, copy the matching `.mic.wav` files beside them under the same names, or only the class is sent; the length counted is the class track's alone, so a five-hour class with its microphone track is sent as it is. |
| `Preparing the recording for upload (2 segment(s) of the class and 1 of your microphone)`, then `Prepared 01:58:10 of audio for upload: the class and your microphone mixed into one track` | Normal for a recording with your microphone: the two are mixed into one track. |
| `Your microphone stayed silent; only the class is sent.` | The microphone's track never rose above a very low level: you never unmuted, the class app never listened, or the microphone heard nothing. The summary line at the end of the recording says which. The upload is then the class's alone. |
| `Only your microphone was recorded (N segment(s)); it is sent on its own` | The recording has no class track (the class could not be recorded, or its files were removed), so the microphone's files are sent alone. |
| `Skipping ...: it is not a readable WAV file` | A damaged part of either track is left out and the rest is sent; with the microphone heard, a damaged microphone part is reported twice. |
| `If the connection breaks like this on every try` | ElevenLabs may be turning the upload down before it has read all of it. Check the API key and the account's credits before uploading again. |
| The upload fails in another way | The audio is safe. Transcribe the recording again with menu choice 3, or, after `record` or menu choice 2, paste the command at the end of the `The audio is safe in ...` line into PowerShell. |
| `ElevenLabs' answer is kept in ...`, `The transcript could not be made, but ElevenLabs' answer is saved in ...` | ElevenLabs answered, and charged, but no transcript was made from it. Open that file: its `"text"` is the transcript's text. Transcribing again is charged again. |
| `ElevenLabs' answer could not be kept anywhere` | It was paid for, but no file could take it. Free some disk space. ElevenLabs keeps the transcript under the transcription ID in the message (see [Where things go](#where-things-go)). Transcribing again is charged again. |
| `ElevenLabs heard no speech in ...` | The transcript was saved, but it is empty, and it was charged. Check what was recorded before the next class. |
| `ElevenLabs transcribed ... of audio (...): about $... at the API price, ...` | The cost estimate, from the audio ElevenLabs counted at `ElevenLabs:PricePerHourUsd`, plus `ElevenLabs:KeytermsPricePerHourUsd` when keyterms were sent (see [Cost and privacy](#cost-and-privacy)). Approximate: ElevenLabs bills per audio minute. `(..., counted by ElevenLabs; the file holds ...)` means ElevenLabs counted more or less than the file holds, by over a minute. `no price is estimated` means the price is set to 0. |
| `ElevenLabs did not say how much audio it counted, and the file's length is not known here, so the cost is not estimated` | A file uploaded as it was (not a WAV ClassScribe wrote) and an answer without a count: the usage page on elevenlabs.io has the cost. |
| `ElevenLabs charged ... credits for this transcript; ... are left until ...`, `ElevenLabs account: ... credits used this period`, `ElevenLabs shows no change in the account's credits for this transcript` | The account's credits read after the transcript: the difference from before the upload; the standing alone when the first read did not succeed; or no change, which on usage-based API billing means the charge is on the usage page instead. `usage-based billing so far this period: $...` follows when the account has an overage. After a background recording the line is in the recording's `.log`, written after the window was shown the end (see [Cost and privacy](#cost-and-privacy)). |
| `ElevenLabs would not show the account's credits to this key (HTTP 401, missing_permissions)` (or `HTTP 403`) | Harmless, once per transcript: the key is restricted to Speech to Text, which is all the transcript needs. To see the account's credits in the log, allow User (read) on the key under Developers > API Keys on elevenlabs.io; to stop asking, set `ElevenLabs:ReadAccountUsage` to `false`. After a background recording the line is in the recording's `.log`. |
| `The account's credits could not be read (...); the transcript is not affected` | ElevenLabs did not answer the account read within 10 seconds, the connection failed, or it answered with an error or in another shape; the transcript is saved all the same. Nothing to do. |
| `The transcript is saved, but the upload copy ... could not be removed` | Another program holds the copy open. The transcript is fine; delete the copy later. |
| `The upload copy ... could not be removed` | The same after a failed or cancelled try: another program holds the copy open. Delete it later; a new try replaces it once nothing holds it any more. |
| `Copy ... of the transcript of ... was taken at that moment; saving the next one instead` | Another ClassScribe window saved a transcript of the same recording at the same moment. Each is kept, under its own number; nothing to do. |
| `The header of ... says it holds ..., but the file goes on to ...` | The file comes from a recorder that stopped without finishing it. ClassScribe reads all of it; nothing to do. |
| `Skipping keyterm on line ...` | That term breaks a rule of [the glossary](#the-glossary), so it was not sent; the message says which rule. |
| `No glossary at ... (ElevenLabs:KeytermsFile), so the transcript is made without its terms` | The glossary your settings name is not there. Put the file there: this recording's next transcript sends it, the one after the class included when this line came at its start. To send another, run `transcribe <path> --again --keyterms "<file>"` once the class is over. The `settings set` command the line gives names another from the next recording, never for a class under way. A relative name is taken from your settings folder, never from beside the app, so a `keyterms.txt` an older version left there is not read. |
| `No glossary at ... (the saved class "..."), so the transcript is made without its terms` | The class's own glossary is not there. Put the file there: this recording's next transcript sends it, the one after the class included when this line came at its start. To send another, run `transcribe <path> --again --keyterms "<file>"` once the class is over. The `class set ... --keyterms` command the line gives names another from the class's next recording, never for the one under way; `--clear keyterms` gives the class `ElevenLabs:KeytermsFile`'s instead. The class records all the same. |
| `The glossary ... (...) could not be read (...), so the transcript is made without its terms`, `The glossary ..., as decided when this recording started, could not be read (...), so the transcript is made without its terms` | The glossary is there, but another program held it, such as an editor or OneDrive, or Windows would not let ClassScribe read it. The transcript goes without its terms. Close that program, then run `transcribe <path> --again`, which is billed again, to send the glossary. |
| `No glossary: ElevenLabs:KeytermsFile is empty` | No glossary is named, so none is sent: ClassScribe ships none. To send yours, name it (see [The glossary](#the-glossary)). `... says none` means the setting, the class or `--keyterms` was set to `none` on purpose. |
| `No glossary, as decided when this recording started (... is empty)`, `... says none)` | No glossary was named when the recording started, so none is sent now either, whatever the settings say today. To send one with this recording, run `transcribe <path> --again --keyterms "<file>"`, which is billed again. |
| `Glossary: ..., as decided when this recording started (... then, ... now)` | A transcript made later sends the glossary decided as the recording started, kept in its `.glossary.json`, whatever the settings say now. To send another for one transcript, give `transcribe <path> --keyterms "<file>"`. |
| `Glossary: ..., as decided when this recording started; changed since: ... then, ... now` | The glossary was edited after the recording started, and is sent as it is now; the numbers are its terms then and now. |
| `The glossary decided when this recording started, ..., is not there now, so the glossary is decided again` | The file was moved or deleted since the recording started. The next line says which glossary is sent instead: right after a class, the one its start decided; later, a saved class of the course, `ElevenLabs:KeytermsFile`, or none. Put the file back for the next try to send it. At a continued recording's start, the glossary decided again is kept for its transcripts from then on. |
| `The glossary this run's request carries is incomplete, so the glossary is decided here instead` | The run file or request of this class was written by another build, or damaged. The class records all the same; the next line says which glossary it takes, here `ElevenLabs:KeytermsFile`'s when the class runs in the background. |
| `Could not decide the recording's glossary at its start (...); the class records on, and its transcript decides the glossary when it is made` | Something nobody expected went wrong with the glossary as the class started. The class records all the same; its transcript says which glossary it sends. If it keeps happening, the recording's `.log` says more. |
| `The saved classes "..." and "..." record ... with different glossaries (...), so ElevenLabs:KeytermsFile decides` | Two classes of one course name different glossaries, and the recording was started by its name, not by `--class`, so ClassScribe cannot tell which is meant. Start it with `record --class "<name>"` or menu C, or give the classes one glossary. |
| `WARNING  The saved class "..." names the glossary ..., which is not there; its transcripts are made without its terms until it is.` | The menu's header names a class whose own glossary is missing. The class can still be used. Put the file there, or change the class's glossary (menu C, E, or `class set ... --keyterms`). |
| `The glossary record ... could not be read (...)`, `Could not write the glossary record ... (...)` | The recording's `.glossary.json` is damaged, or its folder would not take it. The glossary is decided again, a damaged record is left as it is, and nothing else is affected. |

### Making a transcript again

| What you see | What to do |
| --- | --- |
| `Rebuilt from ... into ...; nothing was uploaded`, then `Nothing was sent to ElevenLabs, so nothing was charged` | Done, for free. |
| `... has no saved answer from ElevenLabs yet; transcribe it first` | There is no answer to lay out yet: transcribe the recording first. |
| `... has a transcript, but its saved answer from ElevenLabs is no longer there` | The `.json` beside the transcript was deleted, so only a new transcription, billed again, can make another. |
| `... has no saved answer from ElevenLabs that became a transcript, but the answer to an earlier try ... is kept in ...` | Run the `rebuild` command the next line gives: it lays that kept answer out. |
| `... is neither a file of a ClassScribe recording nor an answer from ElevenLabs saved as .json` | Give `rebuild` a course's folder, a class's date folder, one of a recording's files, or an answer saved as `.json`. |
| `... is rebuilt`, `... is rebuilt instead` | `rebuild` was given a folder: it rebuilds the transcript of the newest recording there that has a saved answer, and names each newer one it passed over, with the reason. For a paid answer kept apart, run the command the next line gives; a recording without a saved answer has to be transcribed first. |
| `Could not rebuild the transcript of ...` | The saved answer could not be read, or the new transcript could not be saved; the lines after it say why, such as `ElevenLabs answered in a form this app cannot read`. Nothing was sent, and nothing is charged. |

### The list of recordings

| What you see | What to do |
| --- | --- |
| `Leaving ... out of the list of recordings: one of its files cannot be read` | Another program holds a file of that recording open, or it may not be read. Close that program and choose 3 again. The other recordings are still listed. |
| `Leaving the folder ... out of the list of recordings: it cannot be read` | A folder in the recordings folder cannot be read, such as a link to a drive that is not plugged in. |
| `No recordings found.` and a reason | The recordings folder cannot be used; see the `WARNING` row under [The settings and the menu](#the-settings-and-the-menu). |

### Google Drive

| What you see | What to do |
| --- | --- |
| `ClassScribe has no Google client yet, so it cannot sign in`, `Google Drive: not set up` | Make the client once, as [Google Drive (set up once)](#google-drive-set-up-once) says, then sign in with the file: 9, then S, or `drive signin --client "<the file>"`. |
| `... is the file of a Web application client, and ClassScribe signs in as a Desktop app` | In Google Auth platform > Clients, click Create client, choose Desktop app, download its JSON, and give that file. |
| `... is not a client file from Google`, `... is not a Desktop app client's file from Google`, `... holds no client_id` | Give the `client_secret_....json` file Download JSON gave you, as it is. |
| `... holds no client_secret` | Google shows a secret only once. On the client's page, click Add secret, download the JSON again, and give that file. |
| `The browser did not open by itself. Copy this address into your browser:` | Copy the address under it into your browser and sign in there; ClassScribe waits up to 5 minutes. |
| `You did not allow ClassScribe to use Google Drive; no sign-in was stored.` | Sign in again and click Allow on Google's page. The client is kept, so 9 then S, then Enter, is enough. |
| `Google did not give ClassScribe access to Google Drive: the box for it was left unticked` | Sign in again and tick the box `See, edit, create, and delete only the specific Google Drive files you use with this app`. |
| `Google's answer did not reach ClassScribe within 5 minutes` | Sign in again and finish in the browser within 5 minutes. If the browser said it could not reach `127.0.0.1`, a security program may be blocking it: in Bitdefender, allow `http://127.0.0.1` under Online Threat Prevention, then try again. |
| `Google ended the sign-in with an error (...)`, `Google refused the sign-in (HTTP 400, invalid_grant ...)` | Sign in again; a sign-in's code is used once and lasts only minutes. If it happens again, check the client in Google Auth platform > Clients. |
| `ClassScribe is not signed in to Google Drive` | Sign in with 9, then S, or `drive signin`. |
| `Google ended ClassScribe's sign-in to Google Drive` | Sign in again with 9, then S. If it happens every 7 days, the app is still in Testing: publish it (step 5 of the set-up), then sign in again. |
| `Google no longer accepts ClassScribe's Google client (invalid_client ...)`, `Google does not accept the client in that file` | The client was deleted, or its secret replaced. Download the client's JSON again (Add secret, if the secret is gone) and sign in with that file. |
| `Google Drive answered that the Drive API is not enabled in your Google Cloud project` | Open https://console.cloud.google.com/apis/library/drive.googleapis.com in your project, click Enable, wait a minute, and try again. |
| `Google could not be reached ...`, `Google Drive could not be reached ...` | Check the internet connection, then try again. The sign-in is kept. |
| `Google Drive could not be reached (...). ClassScribe asks again in 2 seconds, and goes on asking for about 4 minutes` (or `Google Drive refused to ... (HTTP 503 ...)` with the same end) | Nothing to do: Google was out of reach or busy for a moment, and ClassScribe asks again by itself, waiting a little longer each time. If it is still out of reach after about 4 minutes, the upload stops and the recordings wait (below). |
| `Google Drive did not accept ClassScribe's sign-in (HTTP 401 ...). Sign in again with menu choice 9 > S, or ClassScribe drive signin.`, `Google refused to renew ClassScribe's sign-in (HTTP ..., ...). Try again later.` | Sign in again with 9, then S; if Google refused the renewal for another reason, try again later. What waits stays in the queue. |
| `Google Drive holds 2 folders marked as ClassScribe's own; using ...` | More than one folder in My Drive carries ClassScribe's mark, as when a folder put in the trash was restored after a sign-in had made a new one. ClassScribe uses the one named; move what you want from the other into it. |
| `Could not remember the ids of ClassScribe's folders on Drive in ...` | Harmless: the folders are found by their names next time. Check that `%LOCALAPPDATA%\ClassScribe\drive` can be written. |
| `Could not read ...\drive\recordings\....json (...); the recording's files on Google Drive are looked for by name instead` | Harmless: the record of what went up could not be read, so the files already on Drive are found by name and MD5, and nothing goes up twice. |
| `... google-drive.dat cannot be opened by this Windows account`, `... google-drive.dat is damaged` | The sign-in was made by another Windows user or on another computer, or Windows' keys changed. Sign in again with the client file (Add secret on the client's page, if you deleted the file). |
| `Signed out here, but Google could not be reached to end ClassScribe's access` | The sign-in is forgotten here. To end the access at Google too, open https://myaccount.google.com/connections, choose ClassScribe and remove its access. |
| `The ClassScribe folder is not in My Drive now: it is in the trash, or was deleted.` | ClassScribe never takes anything out of the trash. Restore the folder in Drive yourself, or sign in again to make a new one. |
| `Drive:FolderName is ...`, `Drive:ApiBaseUrl is "..."; it must be an https address, ...`, `Drive:ChunkMegabytes is 0; it must be from 1 to 256.`, `Drive:Upload is "...", which is not Everything or WithoutAudio.` | Fix the setting: a plain name of at most 100 characters; Google's own address, or `http://127.0.0.1:<port>/` for a local stub; a piece size from 1 to 256 (MiB); `Everything` or `WithoutAudio`. |
| `Google Drive: uploading ... to ClassScribe/...`, `...: 30% (...)`, `... is on Google Drive: My Drive > ...` | Normal: a class going up, each file checked against Drive's MD5. `...: already on Drive` is a file that was there already, `... is already on Google Drive: ...` a recording that needed nothing more, and `Nothing waits to go up to Google Drive.` an upload of the queue that found it empty. |
| `Google Drive: paused while "..." records`, then `Google Drive: going on, now that "..." has stopped recording` | Normal: an upload of an earlier class waits between pieces while a class records, so that the class is not held up, and goes on by itself. |
| `Google Drive: paused while "..." is transcribed`, then `Google Drive: going on, now that "..." is done` | Normal: an upload waits while a class is transcribed, or a lesson's video made, so that the transcript's upload to ElevenLabs has the connection to itself. |
| `Sending the lesson's video (2.4 GB) to Google Drive; at this connection's speed so far, about 30 minutes.` | Normal, with `Drive:LessonVideos` on: the lesson's video goes up last, after everything else of the recording. |
| `... is having its lesson video made now; it goes up once it has stopped` | `finish-video` is making the lesson's video; the recording goes up with the next upload. |
| `The lesson's video stays on this laptop; to send lesson videos to Google Drive too, run: ...` (a lesson's start) | Lesson videos stay off Drive by default. Run the command to send them too (they are large: about 2.3 GB an hour at 720p). |
| `The camera's pieces of ... stay: ....mp4 differs from its copy on Google Drive ...` (free-space) | The video on the laptop is not the one on Drive (made again since, say). The pieces are kept; upload the lesson (9 > U, or `drive upload`), which sends the new video as a new revision of its copy, and free-space deletes them once that copy matches. |
| `Freed the camera's 2 pieces of ... (...): its lesson video is on Google Drive, checked, in ...; the video stays here too` | Normal: the pieces kept for Drive went once its copy of the video was checked. |
| `The lesson video of ... is still to be made, so none of its audio or camera pieces were deleted: ...` (free-space) | A camera piece is newer than the lesson's video, or there is no video, so free-space keeps the lesson whole. Run the `finish-video` command the line gives, then free-space again. If you do not want the video, delete the `.cam*.mp4` pieces by hand first. |
| `... is newer than the lesson video of ..., which does not hold it, so it stays on this laptop` (free-space) | That piece's picture is in no video, and the sound to make one is gone from the laptop. Download the `.wav` files from Drive into the lesson's folder and run `finish-video`, or delete the piece by hand if you do not need it. |
| `Another ClassScribe is uploading to Google Drive; ... waits and goes up after it` | Nothing to do: one upload runs at a time, and the one running takes this recording before it stops. `... is on Drive already, unchanged since it went up` instead means nothing of it needed to go up. |
| `<base> waits to go up to Google Drive: ...`, `<problem> 2 recordings wait to go up to Google Drive after the next class, or with menu choice 9 > U, or ClassScribe drive upload.` | It goes up later; the reason is in the line. To send it now, press U in the report at the start, or 9 > U, or run `drive upload`. With `Drive:Enabled` set to `false` the line ends `... to go up to Google Drive with menu choice 9 > U, or ClassScribe drive upload.`, since nothing goes up after a class then. |
| `<file> did not go up to Google Drive: ...`, `<file> could not be read to go up to Google Drive: ...` | That file waits, with Drive's words or Windows' reason, and the recording's other files went up. The next upload tries it again. |
| `The upload to Google Drive was stopped. 1 recording waits, and goes on from where it stopped after the next class, or with menu choice 9 > U, or ClassScribe drive upload` | Ctrl+C stopped the upload. It goes on from where it stopped with the next upload. |
| `The upload to Google Drive stopped because of an error (<reason>); <base> waits to go up after the next class, or with menu choice 9 > U, or ClassScribe drive upload` | The class and its transcript are fine, and the exit code is as it would have been; the recording waits and goes up with the next upload. If it happens again, look at the lines before it in the recording's `.log`. |
| `Google Drive is full: the account's storage takes nothing more (HTTP 403, storageQuotaExceeded: ...). Make room in Google Drive, or add storage to the account, then upload again.` | Make room in Google Drive or add storage, then upload with 9 > U. After a class, ClassScribe tries that recording again no sooner than a day later. |
| `Google Drive takes no more uploads today (...). Upload again tomorrow.` | Google lets each account upload 750 GB in 24 hours ([Google's limits](https://knowledge.workspace.google.com/admin/drive/storage-and-upload-limits-for-google-workspace)). Upload tomorrow with 9 > U; after a class, ClassScribe tries again no sooner than a day later. |
| `<file> does not match Drive's MD5 after it went up (here ..., ... bytes; on Drive ..., ... bytes); sending it again as a new revision` | The copy that arrived differs from the file, so it is sent once more, as a new revision of ClassScribe's own copy. `<file> still does not match Drive's MD5 after it was sent again; it goes up again with the next upload.` leaves the recording waiting; the next upload tries again. |
| `<file> on this laptop differs from its copy on Google Drive, which ClassScribe never replaces: only a text file that has grown, or a lesson's video made again, goes up again, as a new revision. The copy on Drive stays as it is`, `A different <file> is already in My Drive > ... on Google Drive; ClassScribe never replaces it, so this one did not go up` | The file changed on the laptop after it went up, or Drive holds another file of that name. The copy on Drive stays as it is; only a text file that has grown, or a lesson's video made again, goes up again. To keep the laptop's version on Drive as well, upload it yourself under another name. |
| `<base>.mp4 was made again since its copy went up to Google Drive, so it goes up whole, as a new revision of that copy` | Normal, with `Drive:LessonVideos` on: a continued lesson's video, or one made with `finish-video`, replaces its copy on Drive as a new revision of the same file. Once it is checked there, free-space can delete the camera's pieces. |
| `<file>'s copy on Google Drive is no longer there, or is in the trash, so it goes up again as a new file; ClassScribe takes nothing out of the trash` | Nothing to do: the grown file goes up again, and the copy in the trash stays there. |
| `<base> is being recorded now; it goes up once it has stopped`, `<base> is being transcribed now; it goes up once it has stopped`, `<file> is open in another program, as while it is being recorded; <base> goes up once it is closed` | Nothing to do: it waits and goes up after it has stopped. For another program holding a file, close that program. |
| `Google Drive answered with an upload address for <file> that is not its own, so nothing was sent there.`, `The upload of <file> begun earlier names an address that is not Google Drive's own, so it is not used; the upload starts again` | ClassScribe sends a file only to Google Drive's own address. Nothing was sent elsewhere; the file goes up with the next upload, or starts again from the beginning. If it happens again, check `Drive:ApiBaseUrl`. |
| `Google Drive took none of the last pieces of <file>; it goes up again with the next upload.`, `Google Drive lost the upload of <file> twice; it starts again with the next upload.` | Google kept losing what was sent. The rest of the recording went up; the file goes up again with the next upload. |
| `<file> is no longer in <folder> and never went up to Google Drive, so it is left out` | A file was deleted or moved from the folder before it went up. Nothing to do, unless you want it on Drive: put it back, then upload again. |
| `Google Drive refused to ... (HTTP ...)`, `Google Drive did not say where to send the file when asked to ...`, `Google Drive's answer when asked to ... could not be read.`, `Google Drive made the folder ... but did not say its id.` | Google answered in a way ClassScribe did not expect. The recording waits; upload it again later with 9 > U. If it keeps happening, the lines before it in the recording's `.log` say more. |
| `<folder> holds no recording; it is passed over`, `<file> is not a file of a ClassScribe recording, so there is nothing to upload from it` (or `to free from it`), `There is no ClassScribe recording in <folder>` | `drive upload` and `drive free-space` take a recording's file, a class's date folder, a course's folder or the recordings folder. Your own folders and files there are passed over, never uploaded. |
| `... was put in the queue again while it went up, so it is looked at once more` | Nothing to do: a transcript made meanwhile, say, goes up in the same upload. |
| `The folder of ..., ..., is not there now: its drive, ..., is not there either; it waits for an upload that finds it` | Plug the drive back in, then upload with 9 > U. |
| `The folder of ..., ..., is not there now: it was moved or deleted, so it is taken out of the queue for Google Drive` | If you moved it, upload it from where it is now: 9 > F, or `drive upload "<its folder>"`. Nothing on Drive changed. |
| `The folder of ..., ..., is not there now, but every file of it went up to Google Drive before and was checked` | Nothing to do: it is safe on Drive. |
| `... lies in ... itself, not in a recording's folder, so it does not go up` | A paid answer kept in the recordings folder, when its own folder would not take it: run the `rebuild` command the line gives, which lays it out beside its recording, then upload that recording. |
| `ClassScribe could not keep track of the upload of ... in ... (...), so the upload stopped` | `%LOCALAPPDATA%\ClassScribe\drive` could not be written: check the free space on the laptop and that no program holds the file, then upload with 9 > U. |
| `Could not put ... in the queue for Google Drive (...); it goes in when the class ends` | Harmless: the class records as usual, and goes into the queue at its end. |
| `Freed <size> on this laptop: deleted 1 audio file of <base> (...). Their copies stay on Google Drive, in My Drive > ...; the transcripts, ElevenLabs' answers, the marks and the log stay here` | Done: the audio is on Drive alone, checked, and the rest stays on the laptop. Nothing on Drive changed. |
| `None of the audio of <base> was deleted: <file> has not gone up to Google Drive yet.` (or `... is open in another program, as while it is being recorded.`, `...'s copy on Google Drive is no longer there, or is in the trash.`, `... differs from its copy on Google Drive (here ... bytes, MD5 ...; there ..., MD5 ...).`) | One audio file failed the check, so all of that recording's audio stayed. Upload the recording again (9 > R, or `drive upload`), then free it again. |
| `<base> has not gone up to Google Drive yet, so none of its audio was deleted. Upload it first: <command>`, `<base> is not all on Google Drive yet, so its audio stays here. Upload it first: R, then its number.` | Run the command given, or 9 > R, then free it again. |
| `<base> is being recorded now, so none of its audio was deleted.`, `<base> is being transcribed now, so none of its audio was deleted.` | Free it once it has stopped. |
| `<base> has no audio on this laptop to delete; its audio is on Google Drive, in My Drive > ...` | Nothing to do: it was freed before. |
| `<file> could not be deleted (...); it stays on this laptop` | Another program holds that file open. Close it and free the recording again; the files already freed stay freed. |
| `Google Drive could not be reached (...). Nothing more was deleted.` | Free space asks Drive about every file before it deletes anything, so nothing of a recording it could not check was deleted. Try again when the laptop is online. |
| `The audio of ... is on Google Drive, not on this laptop: ...`, `Part of the audio of ...` | Its audio was freed, so it cannot be transcribed again as it is: download its `.wav` files from the folder named into the recording's folder first. To make its transcript again for free, run the `rebuild` command the next line gives. |
| `... is a YouTube video's recording; its audio is not on Drive (YouTube:DriveUpload), so it stays. Delete it by hand if you need the room; the video can be fetched again.` | Its audio never went up, as `YouTube:DriveUpload` `WithoutAudio` says, so free space leaves it. This is no failure. |
| `Google Drive's room could not be read (...); what does not fit waits in the queue` | Harmless: a YouTube batch's plan only leaves Drive's room out. |

### YouTube videos

See [YouTube videos](#youtube-videos) for how a batch goes. The lines of a video's own recording are
in its `.log` too.

| What you see | What to do |
| --- | --- |
| `Another ClassScribe window (process 1234) is working through YouTube videos; wait for it to finish, or close it` | One batch runs at a time. Wait for it, or close that window; a video it records as it plays carries on in its recorder to its end. Exit code 1. |
| `No ElevenLabs API key found in ELEVENLABS_API_KEY. Set it (README, Set up once), or add --no-transcribe to fetch only` | Set the key (see [Set up once](#set-up-once)), or choose O in the menu, or add `--no-transcribe`. Nothing was asked of YouTube. Exit code 1. |
| `No link was given; nothing to do.`, `No video to record; nothing to do.`, `A course's name is needed: its folder is named after it.` | Menu choice Y found nothing it could take, or the course's name was empty; paste the links again, or type the course's name. |
| `YouTube: waiting while "Kalam" records; the videos go on after it`, then `YouTube: going on, now that "Kalam" has stopped recording` | Normal: a class comes first. Nothing reaches YouTube or ElevenLabs from the batch meanwhile. |
| `Stopping the YouTube video "..." so that "Kalam" can record; what it recorded is kept.`, `A class is starting, so this video's recording gives way to it` (in the video's log) | Normal: a class stopped the video. Its recording may be cut short, so the same command records it again once the class is over. |
| `Only one recording at a time: the YouTube video "..." has been recording since ...`, then `Press Q twice in the window that shows the video to stop it, then start the class again.` (or `It could not be asked to stop: press Q twice ...`) | The video did not stop within 30 seconds, or its recorder cannot be asked (one of an older build). Press Q twice in the window that shows it (A in the menu shows it), then start the class. |
| `Only one recording at a time: "Kalam" has been recording since ...; the YouTube videos wait for it.` | A class started in the moment between the batch's look and the video's start. The batch waits for it, then asks Enter again. |
| `Something other than ClassScribe made an event named Local\ClassScribe.Yield.<run id>, so a class cannot stop this video's recording by itself this time; Q pressed twice still stops it` | Not expected. Before a class, stop the video yourself with Q twice. |
| `Chrome was not found (YouTube:BrowserPath, App Paths, Program Files, LOCALAPPDATA). Install it, or set YouTube:BrowserPath to its chrome.exe` | Install Chrome, or set `YouTube:BrowserPath`. Nothing was opened; exit code 1. |
| `ClassScribe's video window from an earlier run is still open, so Chrome sent the video there. Close every ClassScribe video window, then press Enter to try again`, then `Enter tries again; ...` | A window of ClassScribe's own Chrome (not your everyday Chrome) was left open, and took the video. Close it, then press Enter. |
| `The video window did not open: Windows did not start Chrome: ... (error N)` | Windows' own words say why: `Access is denied (error 5)` or `... virus ... (error 225)` is most often Bitdefender; look at its Notifications. That video is not recorded; the next is offered. |
| `Two videos in a row could not be recorded, so no more windows open: something is wrong with Chrome. The errors above say what` | Read the lines above it. What was recorded is still transcribed; run the same command again once Chrome works. |
| `The recording failed: The video window was closed before the recording began. Nothing was recorded.` | The window closed in the seconds before its recording began. The same command records the video again. |
| `Nothing plays in ClassScribe's video window yet: press play there. If it plays, check that YouTube's player and Chrome's volume in Windows' mixer are up` (`press play in the video window` in the title) | Press play in the video's window. If it plays, raise YouTube's player volume and Chrome's in Windows' volume mixer. |
| `Nothing has played in ClassScribe's video window for 5 minutes; stopping`, `Nothing played in ClassScribe's video window, so nothing of "..." was kept to transcribe` | Nothing was played in 5 minutes, so nothing was kept or billed. The next video is offered; the same command tries this one again. |
| `Nothing has played for 60 seconds; the recording stops at 2 minutes unless it plays again. Press P here to pause the count` (`nothing plays` in the title) | The video ended, or is paused, perhaps by YouTube's "Continue watching?". Play it on, or press P in ClassScribe's window to hold the count while you pause it. |
| `Nothing has played for 2 minutes; the video has most likely ended, or YouTube paused it` | The recording stopped, and may be cut short. If the video had not ended, the same command records it again. |
| `The video window was closed; stopping the recording`, `The window moved on to another video ("..."); the recording of "..." stops here` | Normal: the video's end, confirmed. |
| `This video has played for 4 hours (YouTube:LongestVideoHours); stopping` | The summary gives `For the rest: <command>`, which records it from where it stopped. For longer videos, raise `YouTube:LongestVideoHours` (at most 9). |
| `"...": ...; it may be cut short, so the same command records it again` | The video's end is not confirmed (a class, the silence, the length limit, or a lost recorder). The recording is kept and transcribed; run the same command again to record it whole, or keep what you have. |
| `Could not write the video's source file ... (...); the recording goes on` | The recording is kept, but is not known as the video's, so the same command records the video again. Check the free space and that the folder can be written. |
| `ClassScribe's video window of "..." did not close; close it before the next one opens` | Close it yourself; otherwise the next video goes to it. |
| `"..." records on in the background to its end; no more videos open, and the same command transcribes it later` | Esc left the video's view. Run the same command once it has ended. |
| `The recorder of the YouTube video "..." ended unexpectedly at .... Its audio so far is kept; a video is never continued, so the same command records it again.` | Most likely Bitdefender ended the recorder (see [Set up once](#set-up-once)). Its audio so far is transcribed, as a recording that may be cut short. |
| `The YouTube batch was stopped with Ctrl+C; what was recorded is kept, and the same command transcribes it later` | Ctrl+C was pressed twice. Exit code 130. |
| `Stopping after the one in hand. Press Q again within 5 seconds to stop at once`, `Stopping at once` | Q, during the downloads or the transcripts. |
| `YouTube gave no title for it (private, removed, or not embeddable); it is tried all the same` (in the plan) | The video keeps its ID as its title. A private video will not play in a window that is not signed in to an account that may see it. |
| `yt-dlp was not found (YouTube:YtDlpPath, winget's links folder, PATH). Install it: winget install --source winget --id yt-dlp.yt-dlp`, `Deno was not found ... Install it: winget install --source winget --id DenoLand.Deno`, `FFmpeg and FFprobe were not found in one folder ... Install them: winget install --source winget --id yt-dlp.FFmpeg` | Run the winget command given, then `ClassScribe youtube check`. Exit code 1. |
| `yt-dlp is at ..., but it did not say its version; Bitdefender may have blocked it (the README says how to allow it), or it is damaged: reinstall it with winget install --source winget --id yt-dlp.yt-dlp` (or Deno, FFmpeg, FFprobe) | Add a Bitdefender exception for that `.exe` as in step 5 of [Set up once](#set-up-once), or reinstall it. |
| `yt-dlp 2026.08.19 is 91 days old, and YouTube changes often, so a download may fail: update it with winget upgrade --source winget --id yt-dlp.yt-dlp` | Run that command; the batch goes on meanwhile. See [Downloading with yt-dlp](#downloading-with-yt-dlp). |
| `Windows did not start yt-dlp (...). Bitdefender may have blocked it (README, Set up once); ClassScribe youtube check says more.` | Look at Bitdefender's Notifications, add an exception for `yt-dlp.exe`, then run `youtube check`. The downloads stopped. |
| `"..." was not downloaded: ...`, `... could not be read: ...`, `The downloads stopped: ...` | What yt-dlp met, in ClassScribe's words (see the table in [Downloading with yt-dlp](#downloading-with-yt-dlp)). What was downloaded is still transcribed; run the same command again later for the rest. |
| `The playlist ... could not be read: ...`, `The playlist ... was not read: ...`, `Playlist ...: ... unavailable videos are hidden`, `yt-dlp's listing of the playlist ... cannot be read: ...`, `yt-dlp's information in ... cannot be read: ...` | That playlist or video is left out, with the reason; the other links go on. YouTube hides a playlist's unavailable videos from its listing. |
| `Reading 200 videos from YouTube takes about 20 minutes; Q stops`, `YouTube refused "..."'s download with HTTP 403; reading it again and trying once more`, `YouTube could not be reached 3 times in a row` | The first two are normal. The third stopped the downloads: check the connection, then run the same command again. |
| `There is not room enough for the audio and its download while keeping room for a 3-hour class (about 1.3 GB): free some room, or download fewer videos at a time.` | Free some room on the recordings folder's drive (or `%LOCALAPPDATA%`'s), or give fewer videos. Nothing was downloaded; exit code 1, or 0 with `--plan`. |
| `Google Drive: everything, the audio included (YouTube:DriveUpload), ...; Drive has only ... free, so what does not fit waits in the queue until there is room.` | Make room in Google Drive, or set `YouTube:DriveUpload` to `WithoutAudio`. Nothing is refused for it. |
| `Deleted the unfinished conversion of "..." left by an earlier batch: .... It is fetched again by the same command` | An earlier batch was cut short while it turned a download into a recording; its unfinished files were deleted, and the video is fetched again. |
| `The unfinished conversion of "..." left by an earlier batch could not be deleted (...); the next batch tries again`, `The note of an earlier conversion, ..., cannot be read (...); it is removed` | Another program holds one of those files; close it. The video is fetched again either way. |
| `The audio of "..." is ... long, but the video is ...: the download is incomplete, so nothing of it was kept. ...`, `yt-dlp finished "...", but did not say where its WAV is, or it is not in ...; run the same command again` | Run the same command again. If it happens again, run `youtube check` and update yt-dlp. |
| `The transcripts stopped: ...`, then `Not transcribed: .... Once that is fixed, run the same command again: what is done is skipped, and these are transcribed` | ElevenLabs refused the key, the credits ran out, or the account is rate limited (see [Transcribing](#transcribing)). The audio is all kept; fix it and run the same command again. |
| `The transcript of "..." failed: ...` | That video's alone; the next went on. The same command transcribes it later. |
| `"..." was transcribed meanwhile (...); it is not sent again`, `"..." (...): ElevenLabs' answer, paid for, was kept for it meanwhile in ...; it is not sent again. To lay it out for free: ...`, `"..." (...): it is being recorded or transcribed elsewhere now, so it is not sent from here` | Nothing is billed twice: another window or command got there first. |
| `Could not write the run file of the transcript of ... (...); until it is done, do not transcribe it in another window` | `%LOCALAPPDATA%\ClassScribe` cannot be written. The transcript goes on; free some space. |
| `The video's source file of ... could not be read (...); its transcript is headed by the recording's name instead`, `The video's source file ... cannot be read: ...; ... is listed without its video's title` | Its `.source.json` was damaged or edited. The recording is transcribed and listed all the same; the same command may fetch the video again. |
| `The first transcript used ... credits for ...; the other ... would use about ... at that rate; ... are left this period` | Normal: the rest of the batch's transcripts, projected in the account's own credits. |
| `Run the same command again to fetch what is left; what is done is skipped.` (or `... to record what is left; ...`) | The summary's last line, when anything is left: the lines above it say what, and what to do first. Exit code 1. |
| `not recorded: you stopped the videos`, `not recorded: you left the view of the video before it`, `not recorded: the videos stopped after two that could not be recorded`, `not recorded: its recording did not start`, `not recorded: its recorder ended before it recorded`, `records on in the background; run the same command again once it has ended to transcribe it` (in the summary) | Why a video was not recorded, or not yet transcribed. The same command records or transcribes it later; what is done is skipped. |
| `needs your word: in the menu (Y > D), ...`, `channel unknown: needs your word`, `your course's channel: never downloaded`, `copied from a player embedded in a web page: never downloaded` (in the summary `..., most likely your course page: never downloaded; record it as it plays instead: <command>`), `needs a sign-in: left out`, `left out: you said no` (in the plan or the summary) | See the guard in [Downloading with yt-dlp](#downloading-with-yt-dlp). Each that you can watch is given the command that records it as it plays instead. |
| `The recording of the YouTube video "..." (...) ended unexpectedly at about ...`, `The transcript of the YouTube video "..." (...) was not finished; ...` (the report at the start) | T transcribes what was recorded; C is never offered for a video. The same `youtube` command records the video again, or transcribes it. |

## Layout

```
src/ClassScribe/
    Program.cs, AppHost.cs, App.cs    start-up: the settings read and checked, then a command or the menu
    Settings/         the settings, where they live (your own folder, the defaults beside the program),
                      the one writer of your file, the saved classes, the API key from the environment,
                      the warnings about a recordings folder, and the file read again and checked
                      before each choice and command
    Audio/            capture, the recording as one or two tracks on one clock, WAV writing, the
                      upload copy (the two tracks mixed)
    Audio/Microphone/ choosing the microphone, the advice about it, the watch on the class app's
                      microphone use and the gate that follows it, the watch for the class app on a
                      Bluetooth microphone, the microphone check's lines
    Audio/Timeline/   the recording clock and where each packet goes on a track
    Audio/Windows/    WASAPI loopback through NAudio, the once-a-second device check, the
                      microphone listing and capture, and the reader of the apps' microphone sessions
    Desktop/          keeping the laptop awake, the computer's clocks, the window's title, taskbar and
                      flash, starting processes (the recorder, yt-dlp in a job of its own, and
                      Chrome for a video), the job check, Windows' end of the session, the registry,
                      process names, the windows of ClassScribe's own Chrome, and Windows'
                      microphone usage record (Windows' own calls in Desktop/Windows/)
    Recorder/         the background recorder: its host, run files, lock, logs, the watch for times
                      when ClassScribe did not run, and the window it brings back by itself
    Link/             the named pipe between the recorder and the windows that show its run
    Transcription/    ElevenLabs client (Refit), the glossary (its term rules, its files, the one resolver
                      that decides a recording's glossary, and the recording's .glossary.json), transcript
                      layout, the upload's time limit, the cost estimate and the account's credits after a transcript
    Drive/            Google Drive: the sign-in (PKCE, the listener on 127.0.0.1, the browser), the
                      client file, the encrypted store, the access tokens, Google's OAuth and Drive
                      clients (Refit), ClassScribe's folders in My Drive, the resumable uploads, the
                      record of what went up and the queue, the upload after each class, and freeing
                      room on the laptop
    Storage/          recording folders (course and date folders, the old flat ones read too), file
                      names, what of a recording's audio is on Google Drive alone, numbered
                      transcript copies, marks, the microphone check's log, and the roaming folder
                      that holds the Google sign-in
    YouTube/          YouTube videos: the links and list files, the batch both ways share (the lock,
                      the wait for a class, the transcripts, the source files), the download path
                      (finding and running yt-dlp, its fixed arguments, the reads, the guard, the
                      plan, the conversion to ClassScribe's own .wav) and the record path (finding
                      Chrome, the titles from YouTube's oEmbed address, the video's window, how a
                      video's recording ends)
    Commands/         the menu, the microphone menu, Google Drive's menu, menu choice Y, the command
                      line (record, transcribe, rebuild, list, devices, mic-check, drive, youtube,
                      settings, class), the saved classes' and the settings' menus (C and S), the
                      report at the start, the view of a recording, the microphone check, and the
                      settings check before each
tests/ClassScribe.Tests/    NUnit and Moq
tools/dry-run/      the dry-run kit: Start-DryRun.cmd, the stand-in for ElevenLabs, the test player
examples/glossary/  an example glossary of the Islamic sciences, never built or published
```

In the tests, the transcription service, the upload preparation, the command runner and the desktop
are Moq mocks. The audio device, the microphones, the apps' microphone sessions, the keyboard, the
clock, the power requests and the HTTP transport are small hand-written stand-ins (`TestDoubles.cs`
and the `TestDoubles.*.cs` beside it), because the tests drive those step by step: deliver this
audio, press P, fail now, switch device, sleep, let chrome go of the microphone. A few tests (the
`WindowsAudio`, `WindowsPower` and `WindowsRegistry` categories) read Windows' real device list,
microphone list, level meters, the apps' microphone sessions, the microphone usage record, three
registry values, the power source and the clocks, and make one power request that is let go at
once; nothing is played or recorded, no microphone is opened, and the computer's settings are never
changed. The `WindowsProcess` tests reach Windows for the background recorder: they start at most a
hidden `cmd.exe` that ends at once or, for the dry-run kit, a hidden Windows PowerShell 5.1, open
named pipes and stop events named after fresh run ids, make hidden windows of the test's own, and
read the test process's own job; the end-to-end tests
run the window and the recorder in one process over temporary folders, with simulated devices and
microphones and a stub standing in for ElevenLabs, in person, with the class and the microphone,
and for the microphone check. A simulated class of twelve minutes with the microphone runs with the
suite, and one of four hours on request. Google is a small Google in memory (`FakeGoogle`) with a
browser and a listener the tests play, and with Drive's folders, files and resumable uploads, faults
included; a few tests use the real listener on `127.0.0.1` with the test as the browser, and the
store's tests use Windows' real data protection in a temporary folder, never `%APPDATA%\ClassScribe`.
The recordings the upload and free-space tests use are small files in temporary folders, in the
course and date layout and the old flat one. YouTube is played too: yt-dlp by a stand-in that writes
the files it would and checks every argument list it is given, Chrome by a stand-in starter and
windows whose titles change on a test clock, YouTube's oEmbed address by the same HTTP stub as
ElevenLabs, and a batch of two videos each way runs end to end. A test run turns on a switch under
which the real yt-dlp, Deno, FFmpeg and Chrome, Chrome's real windows and youtube.com itself are
refused, so a test that forgot its stand-in fails rather than reaching them; the only real program
YouTube's tool tests start is Windows' own hidden `cmd.exe`. The dry-run kit's tests start a hidden
Windows PowerShell 5.1 of their own to parse its scripts and try its checks on temporary folders, and
run its stand-in for ElevenLabs on `127.0.0.1` with ClassScribe's own client; on request, one builds
a stand-in `ClassScribe.exe` that records nothing and runs the whole dry run with it in temporary
folders. No test ever starts ClassScribe itself, records, opens a device or a microphone, plays a
sound, opens a browser, runs yt-dlp, or sends anything to ElevenLabs, Google or YouTube, and none
reads or writes the real `%APPDATA%\ClassScribe`, `%LOCALAPPDATA%\ClassScribe` or
`%USERPROFILE%\ClassScribe`. The suite holds 4,610 tests and takes about four minutes; a test
run that hangs is stopped after 10 minutes and reported as failed (`ClassScribe.Tests.runsettings`).
