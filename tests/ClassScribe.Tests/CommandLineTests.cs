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
        Assert.That(CommandLine.Rebuild(Recording), Is.EqualTo(CommandLine.Rebuild(Recording, Environment.ProcessPath, AppContext.BaseDirectory)));
        Assert.That(CommandLine.Transcribe(Recording, again: true), Is.EqualTo(CommandLine.Transcribe(Recording, Environment.ProcessPath, AppContext.BaseDirectory, again: true)));
    }
}
