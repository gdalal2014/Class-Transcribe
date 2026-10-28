using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// Runs the app's commands. Each run gets a scope of its own, made when it starts and disposed when it ends: fresh Ctrl+C
/// signals, the settings as the gate last accepted them, and a fresh recording. The menu and the command line reach every
/// command through it, so that they resolve whatever the commands' own services are.
/// </summary>
public interface ICommandRunner
{
    /// <summary>Records as <paramref name="request"/> says, started from <paramref name="origin"/>, and transcribes when it asks. Returns the exit code.</summary>
    Task<int> RecordAsync(RecordRequest request, StartOrigin origin);

    /// <summary>
    /// Transcribes what the request names: a class folder, a file of a recording, or any other file. A recording that a
    /// recorder records or transcribes now is refused. Returns the exit code.
    /// </summary>
    Task<int> TranscribeAsync(TranscribeRequest request);

    /// <summary>
    /// Rebuilds, for free, the transcript of a class folder, of a file of a recording, or of an answer from ElevenLabs saved
    /// as .json, and opens it at the end when <paramref name="open"/> is true and the settings ask. Returns the exit code.
    /// </summary>
    Task<int> RebuildAsync(string path, bool open);

    /// <summary>
    /// Makes a lesson's video again, for free, from its camera pieces and its sound (see <see cref="FinishVideoCommand"/>):
    /// finish-video, menu choice 3's V and the report's V. A lesson a live run records, transcribes or finishes is refused.
    /// Returns the exit code.
    /// </summary>
    Task<int> FinishVideoAsync(FinishVideoRequest request);

    /// <summary>
    /// Lists every recording under <paramref name="outputRoot"/>, or under Recording:OutputFolder when it is null, newest
    /// first, with the runs in the background. Returns the exit code.
    /// </summary>
    Task<int> ListAsync(string? outputRoot);

    /// <summary>Lists the output devices, the apps with sound, the microphones and the cameras, each with the option that records it; opens nothing. Returns the exit code.</summary>
    Task<int> DevicesAsync();

    /// <summary>
    /// The camera check (see <see cref="CameraCheckCommand"/>): the cameras, which apps use one, the camera and size a lesson
    /// would take, and the camera's set-up lines; opens the camera, shared, only after a typed Y. Returns the exit code.
    /// </summary>
    Task<int> CameraCheckAsync(CameraCheckRequest request);

    /// <summary>
    /// Shows the recording in progress, or the run <paramref name="runId"/> names. With <paramref name="alert"/>, the window
    /// is one the recorder opened by itself, which ignores every key until Enter. Returns the exit code.
    /// </summary>
    Task<int> AttachAsync(string? runId, bool alert);

    /// <summary>
    /// Menu choice 7: asks whether to record your microphone, with the class or alone in person, which one, and which app
    /// it follows, and keeps the answer for the session (see <see cref="MicrophoneMenu"/>). Returns the exit code.
    /// </summary>
    Task<int> ChooseMicrophoneAsync();

    /// <summary>
    /// Watches what the programs on this computer do with the microphone, as <paramref name="request"/> says, writing a line
    /// for each change to a log, in this window, until it is stopped there. Opens no microphone. Returns the exit code.
    /// </summary>
    Task<int> MicCheckAsync(MicCheckRequest request);

    /// <summary>
    /// Menu choice 9: Google Drive, with the sign-in's state and its keys: sign in, sign out, check with Google (see
    /// <see cref="DriveMenu"/>). Returns the exit code.
    /// </summary>
    Task<int> DriveMenuAsync();

    /// <summary>
    /// Signs in to Google Drive in the browser, with the client in <paramref name="clientFile"/>, the file downloaded from
    /// Google, or with the client kept from before when it is null. Returns the exit code.
    /// </summary>
    Task<int> DriveSignInAsync(string? clientFile);

    /// <summary>Signs out of Google Drive: Google is asked to end ClassScribe's access, and the sign-in is forgotten; the client is kept. Returns the exit code.</summary>
    Task<int> DriveSignOutAsync();

    /// <summary>Shows the Google Drive sign-in's state: the account, the storage used and ClassScribe's folder. Returns the exit code.</summary>
    Task<int> DriveStatusAsync();

    /// <summary>
    /// Uploads to Google Drive what <paramref name="path"/> names, a recording's file or a folder of recordings, then the
    /// rest of the queue that is due; with no path, every recording waiting in the queue (see <see cref="DriveUploadCommand"/>).
    /// Returns the exit code.
    /// </summary>
    Task<int> DriveUploadAsync(string? path);

    /// <summary>
    /// Frees room on this laptop: deletes the audio of the recordings <paramref name="path"/> names once each file's copy on
    /// Google Drive is checked; nothing on Drive is touched (see <see cref="DriveUploadCommand"/>). Returns the exit code.
    /// </summary>
    Task<int> DriveFreeSpaceAsync(string path);

    /// <summary>Runs the batch of YouTube videos <paramref name="request"/> describes (see <see cref="YouTubeCommand"/>). Returns the exit code.</summary>
    Task<int> YouTubeAsync(YouTube.YouTubeRequest request);

    /// <summary>youtube check: where yt-dlp and its helpers are, their versions, and what to install or update. Returns the exit code.</summary>
    Task<int> YouTubeCheckAsync();

    /// <summary>Menu choice Y: asks for the links, the course and whether to transcribe, then runs the batch (see <see cref="YouTubeMenu"/>). Returns the exit code.</summary>
    Task<int> YouTubeMenuAsync();

    /// <summary>settings: shows or changes the settings as <paramref name="request"/> asks (see <see cref="SettingsCommand"/>). Returns the exit code.</summary>
    Task<int> SettingsAsync(SettingsRequest request);

    /// <summary>class: lists, shows or changes the saved classes as <paramref name="request"/> asks (see <see cref="ClassCommand"/>). Returns the exit code.</summary>
    Task<int> ClassAsync(ClassRequest request);

    /// <summary>Menu choice C: the saved classes, to start, add, change or remove one (see <see cref="ClassesMenu"/>). Returns the exit code.</summary>
    Task<int> ClassesMenuAsync();

    /// <summary>Menu choice S: the recordings folder, the camera picture size and the settings file (see <see cref="SettingsMenu"/>). Returns the exit code.</summary>
    Task<int> SettingsMenuAsync();
}

/// <summary>
/// Runs each command in a new scope of the service container, made when the command starts and disposed when it ends. A
/// recording goes to the starter (<see cref="IRecordStarter"/>), which chooses between a recorder in the background and this
/// window. Showing a run goes to the attach command (<see cref="AttachCommand"/>), which finds the run in the run files and
/// shows it through the view of runs; list, devices, the microphone menu, the microphone check and Google Drive (menu
/// choice 9 and the drive command) go to their commands. A command whose services are not registered in the container it
/// runs over says so, before it makes a scope.
/// </summary>
public sealed class CommandRunner(IServiceScopeFactory scopes, IServiceProviderIsService registered, ILogger<CommandRunner> logger) : ICommandRunner
{
    /// <summary>
    /// Records as <paramref name="request"/> says, started from <paramref name="origin"/>, through the starter, in a scope of
    /// its own. Returns the exit code.
    /// </summary>
    public async Task<int> RecordAsync(RecordRequest request, StartOrigin origin)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRecordStarter>().RunAsync(request, origin);
    }

    /// <summary>
    /// Transcribes what the request names, in a scope of its own, unless it names a recording that a live recorder records
    /// now, or is stopping, or transcribes, in the background or in another ClassScribe window: what was sent while it is
    /// recorded would stop short, and one being transcribed would be billed twice. That is said, with where the transcript
    /// will appear, and the exit code is 1.
    /// </summary>
    public async Task<int> TranscribeAsync(TranscribeRequest request)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (BusyWith(scope.ServiceProvider, request.Path) is { } busy)
        {
            if (!busy.Run.IsTranscribingNow)
                logger.LogError("{Recording} is being recorded now; transcribe it once it has stopped", busy.Files.BaseName);
            else if (busy.Run.Record.Home == RunHome.InWindow)
                logger.LogError("{Recording} is being transcribed in another ClassScribe window; its transcript will appear in {Folder}", busy.Files.BaseName, busy.Files.Folder);
            else
                logger.LogError("{Recording} is being transcribed in the background; its transcript will appear in {Folder}", busy.Files.BaseName, busy.Files.Folder);
            return 1;
        }

        return await scope.ServiceProvider.GetRequiredService<TranscribeCommand>().RunAsync(request);
    }

    /// <inheritdoc />
    public async Task<int> RebuildAsync(string path, bool open)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RebuildCommand>().RunAsync(path, open);
    }

    /// <summary>
    /// Makes a lesson's video again with the finish-video command, in a scope of its own, unless a live run records the
    /// lesson, or transcribes it or makes its video now, in the background or in another window: that is said, and the exit
    /// code is 1.
    /// </summary>
    public async Task<int> FinishVideoAsync(FinishVideoRequest request)
    {
        Require<FinishVideoCommand>("Making a lesson's video is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        if (BusyWith(scope.ServiceProvider, request.Path) is { } busy)
        {
            logger.LogError(busy.Run.IsTranscribingNow
                ? "{Recording} is being transcribed, or its video made, now; it appears in {Folder} once it is done"
                : "{Recording} is being recorded now; make its video once it has stopped", busy.Files.BaseName, busy.Files.Folder);
            return 1;
        }

        return await scope.ServiceProvider.GetRequiredService<FinishVideoCommand>().RunAsync(request);
    }

    /// <summary>Lists the recordings with the list command, in a scope of its own. Returns the exit code.</summary>
    public async Task<int> ListAsync(string? outputRoot)
    {
        Require<ListCommand>("The list command is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<ListCommand>().Run(outputRoot);
    }

    /// <summary>Lists the devices and the apps with sound with the devices command, in a scope of its own. Returns the exit code.</summary>
    public async Task<int> DevicesAsync()
    {
        Require<DevicesCommand>("The devices command is not part of this build yet; menu choice 6 lists the output devices and the apps with sound.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DevicesCommand>().RunAsync();
    }

    /// <summary>Runs the camera check with <paramref name="request"/>, in a scope of its own. Returns the exit code.</summary>
    public async Task<int> CameraCheckAsync(CameraCheckRequest request)
    {
        Require<CameraCheckCommand>("The camera check is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CameraCheckCommand>().RunAsync(request);
    }

    /// <summary>
    /// Shows a run with the attach command (<see cref="AttachCommand"/>), in a scope of its own: the run
    /// <paramref name="runId"/> names, or else the one in progress, as a window that did not start it, or, with
    /// <paramref name="alert"/>, as the window the recorder opened by itself. The command finds the run, shows it, tells
    /// how a run that is over ended, and continues a recording whose recorder is lost through
    /// <see cref="RecordAsync(RecordRequest, StartOrigin)"/>. Returns its exit code.
    /// </summary>
    public async Task<int> AttachAsync(string? runId, bool alert)
    {
        Require<AttachCommand>("Showing a recording in the background is not part of this build yet: this build records in its own window.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AttachCommand>().RunAsync(runId, alert);
    }

    /// <summary>Asks about the microphone with the microphone menu (<see cref="MicrophoneMenu"/>), in a scope of its own. Returns the exit code.</summary>
    public async Task<int> ChooseMicrophoneAsync()
    {
        Require<MicrophoneMenu>("Choosing the microphone from the menu is not part of this build yet; set Recording:Mode and Recording:Microphone in appsettings.json instead.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MicrophoneMenu>().RunAsync();
    }

    /// <summary>Runs the microphone check (<see cref="IMicCheckRun"/>) with <paramref name="request"/>, in a scope of its own. Returns its exit code.</summary>
    public async Task<int> MicCheckAsync(MicCheckRequest request)
    {
        Require<IMicCheckRun>("The microphone check is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMicCheckRun>().RunAsync(request);
    }

    /// <summary>Shows menu choice 9, Google Drive (<see cref="DriveMenu"/>), in a scope of its own. Returns the exit code.</summary>
    public async Task<int> DriveMenuAsync()
    {
        Require<DriveMenu>("Google Drive is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DriveMenu>().RunAsync();
    }

    /// <summary>Signs in to Google Drive with the drive command (<see cref="DriveCommand"/>), in a scope of its own. Returns its exit code.</summary>
    public async Task<int> DriveSignInAsync(string? clientFile)
    {
        Require<DriveCommand>("Google Drive is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DriveCommand>().SignInAsync(clientFile);
    }

    /// <summary>Signs out of Google Drive with the drive command, in a scope of its own. Returns its exit code.</summary>
    public async Task<int> DriveSignOutAsync()
    {
        Require<DriveCommand>("Google Drive is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DriveCommand>().SignOutAsync();
    }

    /// <summary>Shows the Google Drive sign-in's state with the drive command, in a scope of its own. Returns its exit code.</summary>
    public async Task<int> DriveStatusAsync()
    {
        Require<DriveCommand>("Google Drive is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DriveCommand>().StatusAsync();
    }

    /// <summary>Uploads to Google Drive with the drive upload command (<see cref="DriveUploadCommand"/>), in a scope of its own. Returns its exit code.</summary>
    public async Task<int> DriveUploadAsync(string? path)
    {
        Require<DriveUploadCommand>("Uploading to Google Drive is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DriveUploadCommand>().UploadAsync(path);
    }

    /// <summary>Frees room on this laptop with the drive free-space command, in a scope of its own. Returns its exit code.</summary>
    public async Task<int> DriveFreeSpaceAsync(string path)
    {
        Require<DriveUploadCommand>("Freeing room once recordings are on Google Drive is not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DriveUploadCommand>().FreeSpaceAsync(path);
    }

    /// <summary>Runs a batch of YouTube videos with the youtube command (<see cref="YouTubeCommand"/>), in a scope of its own. Returns its exit code.</summary>
    public async Task<int> YouTubeAsync(YouTube.YouTubeRequest request)
    {
        Require<YouTubeCommand>("YouTube videos are not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<YouTubeCommand>().RunAsync(request);
    }

    /// <summary>Checks yt-dlp and its helpers with the youtube command, in a scope of its own. Returns its exit code.</summary>
    public async Task<int> YouTubeCheckAsync()
    {
        Require<YouTubeCommand>("YouTube videos are not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<YouTubeCommand>().CheckAsync();
    }

    /// <summary>Shows menu choice Y, YouTube videos (<see cref="YouTubeMenu"/>), in a scope of its own. Returns its exit code.</summary>
    public async Task<int> YouTubeMenuAsync()
    {
        Require<YouTubeMenu>("YouTube videos are not part of this build yet.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<YouTubeMenu>().RunAsync();
    }

    /// <summary>Runs the settings command (<see cref="SettingsCommand"/>) as <paramref name="request"/> asks, in a scope of its own. Returns its exit code.</summary>
    public async Task<int> SettingsAsync(SettingsRequest request)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsCommand>().RunAsync(request);
    }

    /// <summary>Runs the class command (<see cref="ClassCommand"/>) as <paramref name="request"/> asks, in a scope of its own. Returns its exit code.</summary>
    public async Task<int> ClassAsync(ClassRequest request)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ClassCommand>().RunAsync(request);
    }

    /// <summary>Shows menu choice C, the saved classes (<see cref="ClassesMenu"/>), in a scope of its own. Returns its exit code.</summary>
    public async Task<int> ClassesMenuAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ClassesMenu>().RunAsync();
    }

    /// <summary>Shows menu choice S, the settings (<see cref="SettingsMenu"/>), in a scope of its own. Returns its exit code.</summary>
    public async Task<int> SettingsMenuAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettingsMenu>().RunAsync();
    }

    /// <summary>
    /// How a window names a run that another ClassScribe window records, or transcribes, in itself: there is no link to show
    /// it by, and closing that window ends it. Without a full stop, for the caller to end the sentence.
    /// </summary>
    internal static string InAnotherWindow(RunRecord run) =>
        run.State == RunState.Transcribing
            ? LessonRuns.OnlyItsVideoLeft(run)
                ? $"\"{run.Request.Name}\" is having its lesson video made in another ClassScribe window (process {run.ProcessId}), which shows it"
                : $"\"{run.Request.Name}\" is being transcribed in another ClassScribe window (process {run.ProcessId}), which shows it"
            : $"\"{run.Request.Name}\" is being recorded in another ClassScribe window (process {run.ProcessId}), which shows it; closing that window ends the recording";

    /// <summary>
    /// The recording <paramref name="path"/> names and the live run busy with it, recording, stopping or transcribing it, when
    /// there is one; null otherwise, and always while the run files are not part of this build. A folder names the recording
    /// started last in it, any file of a recording that recording, as the transcribe command takes them.
    /// </summary>
    private (RecordingFiles Files, RunView Run)? BusyWith(IServiceProvider services, string path)
    {
        if (!Has<RunJournal>()) return null;

        var fullPath = Path.GetFullPath(path);
        var files = Directory.Exists(fullPath) ? ClassFolders.FindNewest(fullPath)
            : File.Exists(fullPath) ? RecordingFiles.TryParse(fullPath)
            : null;
        if (files is null) return null;

        var busy = services.GetRequiredService<RunJournal>().List()
            .FirstOrDefault(run => (run.IsRecordingNow || run.IsTranscribingNow) && RunView.IsRunOf(run.Record, files));
        return busy is null ? null : (files, busy);
    }

    /// <summary>True when this build registers <typeparamref name="T"/>, so that a scope can make it.</summary>
    private bool Has<T>() => registered.IsService(typeof(T));

    /// <summary>Throws a <see cref="NotSupportedException"/> that says <paramref name="missing"/> when the container does not register <typeparamref name="T"/>.</summary>
    private void Require<T>(string missing)
    {
        if (!Has<T>()) throw new NotSupportedException(missing);
    }
}
