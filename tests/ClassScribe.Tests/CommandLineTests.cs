using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;

namespace ClassScribe.Tests;

/// <summary>The command lines the app prints for later, such as to try a transcript again or to rebuild one for free.</summary>
[TestFixture]
public class CommandLineTests
{
    private const string Exe = @"C:\Program Files\ClassScribe\ClassScribe.exe";
    private const string ExeFolder = @"C:\Program Files\ClassScribe\";
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
    private const string BuildFolder = @"D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\";
    private const string Recording = @"C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav";

    /// <summary>The transcribe command names the program by its full path, so it runs as printed from any folder, published or built with dotnet run.</summary>
    [Test]
    public void The_transcribe_command_names_the_program_by_its_full_path()
    {
        var command = CommandLine.Transcribe(Recording, Exe, ExeFolder);

        Assert.That(command, Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" transcribe ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"""));
    }

    /// <summary>Started as "dotnet ClassScribe.dll", the process is dotnet itself, so the command runs the app's dll with it.</summary>
    [Test]
    public void Run_by_dotnet_the_transcribe_command_runs_the_app_with_dotnet()
    {
        var command = CommandLine.Transcribe(@"D:\Classes\lecture.mp4", Dotnet, BuildFolder);

        Assert.That(command, Is.EqualTo(@"dotnet ""D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\ClassScribe.dll"" transcribe ""D:\Classes\lecture.mp4"""));
    }

    /// <summary>
    /// The form of the transcribe command for a recording that already has a transcript ends with --again, which the
    /// command line needs before it bills a recording a second time.
    /// </summary>
    [Test]
    public void The_again_form_of_the_transcribe_command_ends_with_the_option()
    {
        Assert.That(CommandLine.Transcribe(Recording, Exe, ExeFolder, again: true),
            Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" transcribe ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"" --again"));
        Assert.That(CommandLine.Transcribe(Recording, Dotnet, BuildFolder, again: true),
            Is.EqualTo(@"dotnet ""D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\ClassScribe.dll"" transcribe ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"" --again"));
    }

    /// <summary>The rebuild command names the program by its full path too, or runs the app's dll with dotnet, followed by the path to rebuild.</summary>
    [Test]
    public void The_rebuild_command_names_the_program_by_its_full_path()
    {
        Assert.That(CommandLine.Rebuild(Recording, Exe, ExeFolder),
            Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" rebuild ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"""));
        Assert.That(CommandLine.Rebuild(Recording, Dotnet, BuildFolder),
            Is.EqualTo(@"dotnet ""D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\ClassScribe.dll"" rebuild ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"""));
    }

    /// <summary>Without a program path to go on, as when Windows cannot say which program is running, the commands run the app's dll with dotnet.</summary>
    [Test]
    public void Without_a_program_path_the_commands_run_the_app_with_dotnet()
    {
        Assert.That(CommandLine.Rebuild(Recording, processPath: null, BuildFolder), Does.StartWith(@"dotnet ""D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\ClassScribe.dll"" rebuild "));
        Assert.That(CommandLine.Transcribe(Recording, processPath: null, BuildFolder, again: true), Does.EndWith(" --again"));
    }

    /// <summary>The forms without a program path name the program this very process runs, as the forms with one do for the path given.</summary>
    [Test]
    public void The_short_forms_name_this_program()
    {
        var request = Request(new ClassSource.Automatic());

        Assert.That(CommandLine.Rebuild(Recording), Is.EqualTo(CommandLine.Rebuild(Recording, Environment.ProcessPath, AppContext.BaseDirectory)));
        Assert.That(CommandLine.Transcribe(Recording, again: true), Is.EqualTo(CommandLine.Transcribe(Recording, Environment.ProcessPath, AppContext.BaseDirectory, again: true)));
        Assert.That(CommandLine.Record(request), Is.EqualTo(CommandLine.Record(request, Environment.ProcessPath, AppContext.BaseDirectory)));
    }

    /// <summary>
    /// The command that continues a recording, as the report at the start prints it after an unexpected end: record with the
    /// run's name, its source, its recordings folder and --continue, and nothing else (no --minutes), so that it runs as
    /// printed and adds the next part to the same recording.
    /// </summary>
    [Test]
    public void The_record_command_continues_the_recording_from_the_same_source_into_the_same_folder()
    {
        Assert.That(CommandLine.Record(Request(new ClassSource.Automatic()), Exe, ExeFolder),
            Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" record ""Kalam"" --device auto --output ""D:\Classes"" --continue"));
        Assert.That(CommandLine.Record(Request(new ClassSource.Device("Pixel Buds")), Exe, ExeFolder),
            Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" record ""Kalam"" --device ""Pixel Buds"" --output ""D:\Classes"" --continue"));
        Assert.That(CommandLine.Record(Request(new ClassSource.App("chrome")), Dotnet, BuildFolder),
            Is.EqualTo(@"dotnet ""D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\ClassScribe.dll"" record ""Kalam"" --app ""chrome"" --output ""D:\Classes"" --continue"));
    }

    /// <summary>A run that recorded only continues as record-only, and one that was to open nothing at its end keeps --no-open.</summary>
    [Test]
    public void The_record_command_keeps_record_only_and_no_open()
    {
        var request = Request(new ClassSource.Automatic()) with { Transcribe = false, Open = false, Minutes = 90 };

        Assert.That(CommandLine.Record(request, Exe, ExeFolder),
            Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" record-only ""Kalam"" --device auto --output ""D:\Classes"" --no-open --continue"));
    }

    /// <summary>
    /// The microphone modes of phase 3 keep their microphone: --mic beside the class, --in-person alone, each with the
    /// microphone's name when one was given.
    /// </summary>
    [Test]
    public void The_record_command_keeps_the_microphone()
    {
        var withClass = Request(new ClassSource.Automatic()) with
        {
            Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null))
        };
        var inPerson = Request(new ClassSource.Automatic()) with { Source = new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("OBSBOT")) };

        Assert.That(CommandLine.Record(withClass, Exe, ExeFolder), Does.EndWith(@" record ""Kalam"" --device auto --mic --output ""D:\Classes"" --continue"));
        Assert.That(CommandLine.Record(inPerson, Exe, ExeFolder), Does.EndWith(@" record ""Kalam"" --in-person ""OBSBOT"" --output ""D:\Classes"" --continue"));
    }

    /// <summary>
    /// A rule chosen for the run with --mic-follows, or in the menu, is kept when the recording is continued, so that the
    /// next part follows the same app: the name part, auto or none, as the option spells them. Without one, nothing is
    /// added, and the continued part follows the recorded app or the setting, as the run did.
    /// </summary>
    [TestCase("zoom", @"--mic-follows ""zoom""")]
    [TestCase("auto", @"--mic-follows ""auto""")]
    [TestCase("none", @"--mic-follows ""none""")]
    public void The_record_command_keeps_the_app_the_microphone_was_chosen_to_follow(string word, string option)
    {
        Assert.That(FollowRule.TryParse(word, out var rule), Is.True);
        var request = Request(new ClassSource.App("chrome")) with
        {
            Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("chrome"), new MicrophoneChoice("OBSBOT", rule))
        };

        Assert.That(CommandLine.Record(request, Exe, ExeFolder), Does.EndWith($@" record ""Kalam"" --app ""chrome"" --mic ""OBSBOT"" {option} --output ""D:\Classes"" --continue"));
    }

    /// <summary>
    /// Inside PowerShell's double quotes, $ starts a variable, a backtick escapes the next character, and a double quote
    /// ends the text: the straight one, and the typographic ones PowerShell takes for it too (“, ” and „), which a name or
    /// a folder copied from a web page or a document may hold. Each is escaped with a backtick, so that a name or a path
    /// holding one reaches ClassScribe as it is.
    /// </summary>
    [Test]
    public void PowerShells_special_characters_are_escaped_inside_the_quotes()
    {
        var request = Request(new ClassSource.Device("Speakers \"Main\"")) with { Name = "Fiqh $1 `a", OutputRoot = @"D:\Cla$$es" };

        Assert.That(CommandLine.Record(request, Exe, ExeFolder),
            Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" record ""Fiqh `$1 ``a"" --device ""Speakers `""Main`"""" --output ""D:\Cla`$`$es"" --continue"));
        Assert.That(CommandLine.Transcribe(@"D:\Cla$$es\x.wav", Exe, ExeFolder), Does.EndWith(@" transcribe ""D:\Cla`$`$es\x.wav"""));
        Assert.That(CommandLine.Record(request with { Name = "Kalam “intro”", OutputRoot = @"D:\„Classes“" }, Exe, ExeFolder),
            Does.Contain(@" record ""Kalam `“intro`”"" --device ").And.EndWith(@" --output ""D:\`„Classes`“"" --continue"));
        Assert.That(CommandLine.Rebuild(@"D:\Kalam “intro”\x.md", Exe, ExeFolder), Does.EndWith(@" rebuild ""D:\Kalam `“intro`”\x.md"""));
    }

    /// <summary>A request to record Kalam from <paramref name="source"/> into D:\Classes and transcribe it.</summary>
    private static RecordRequest Request(ClassSource source) =>
        new(TestRuns.Id(), "Kalam", Transcribe: true, new SourcePlan(RecordingMode.Class, source, null), @"D:\Classes", ContinueChoice.Ask, ContinueBaseName: null,
            Minutes: null, Open: true);
}
