using ClassScribe.Commands;

namespace ClassScribe.Tests;

/// <summary>The command lines the app prints for later, such as to try a transcript again.</summary>
[TestFixture]
public class CommandLineTests
{
    /// <summary>The transcribe command names the program by its full path, so it runs as printed from any folder, published or built with dotnet run.</summary>
    [Test]
    public void The_transcribe_command_names_the_program_by_its_full_path()
    {
        var command = CommandLine.Transcribe(@"C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav", @"C:\Program Files\ClassScribe\ClassScribe.exe", @"C:\Program Files\ClassScribe\");

        Assert.That(command, Is.EqualTo(@"& ""C:\Program Files\ClassScribe\ClassScribe.exe"" transcribe ""C:\Users\gdala\ClassScribe\Kalam\Kalam_2026-10-14_19-30-00Z.wav"""));
    }

    /// <summary>Started as "dotnet ClassScribe.dll", the process is dotnet itself, so the command runs the app's dll with it.</summary>
    [Test]
    public void Run_by_dotnet_the_transcribe_command_runs_the_app_with_dotnet()
    {
        var command = CommandLine.Transcribe(@"D:\Classes\lecture.mp4", @"C:\Program Files\dotnet\dotnet.exe", @"D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\");

        Assert.That(command, Is.EqualTo(@"dotnet ""D:\Repos\Class-Transcribe\src\ClassScribe\bin\Debug\net10.0\ClassScribe.dll"" transcribe ""D:\Classes\lecture.mp4"""));
    }
}
