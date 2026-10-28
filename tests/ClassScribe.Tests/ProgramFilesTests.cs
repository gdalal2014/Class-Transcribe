using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>The names of ClassScribe's program files (architecture 3.15 a and 5.4: ProgramFiles in Core).</summary>
[TestFixture]
public class ProgramFilesTests
{
    /// <summary>
    /// The names are the program's own assembly's, the one Program runs in: ClassScribe, ClassScribe.exe, and the
    /// ClassScribe.dll a recorder or a camera process under dotnet is started with, whichever assembly the constants move to.
    /// </summary>
    [Test]
    public void The_names_are_the_programs_own_assemblys()
    {
        var name = typeof(App).Assembly.GetName().Name;

        Assert.That(ProgramFiles.Name, Is.EqualTo(name).And.EqualTo("ClassScribe"));
        Assert.That(ProgramFiles.Exe, Is.EqualTo(name + ".exe"));
        Assert.That(ProgramFiles.Dll, Is.EqualTo(name + ".dll"));
    }
}