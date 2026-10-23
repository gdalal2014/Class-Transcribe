namespace ClassScribe.Tests;

/// <summary>
/// The repository's folders (architecture 2.4, its glossary checks arriving with slice 1, 8.2): the app ships no glossary, so
/// none sits under src and no project file under src copies one to its output; the example glossary is kept outside src, in
/// examples\glossary, which only the test project names. Files are listed with Directory.EnumerateFiles, so no process is
/// started and a stray untracked file is caught too; every bin and obj folder, which builds leave, is skipped.
/// </summary>
[TestFixture]
public class RepositoryLayoutTests
{
    /// <summary>The repository's root: the folder above the tests that holds ClassScribe.sln.</summary>
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "ClassScribe.sln"))) return folder.FullName;
        }

        throw new InvalidOperationException("ClassScribe.sln was not found above the test folder.");
    }

    /// <summary>Every file under <paramref name="folder"/> but those in a bin or obj folder, as paths relative to <paramref name="root"/>.</summary>
    private static IEnumerable<string> FilesUnder(string root, string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file))
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    /// <summary>True for a file whose name says it is a glossary: a text file named for keyterms or a glossary.</summary>
    private static bool IsGlossary(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("keyterm", StringComparison.OrdinalIgnoreCase) || name.Contains("glossary", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>No glossary sits under src: the app ships none, and a build never puts one beside the exe.</summary>
    [Test]
    public void No_glossary_sits_under_src()
    {
        var root = RepositoryRoot();

        Assert.That(FilesUnder(root, Path.Combine(root, "src")).Where(IsGlossary), Is.Empty);
    }

    /// <summary>No project file under src copies a glossary to its output, or names one at all.</summary>
    [Test]
    public void No_project_under_src_copies_a_glossary()
    {
        var root = RepositoryRoot();
        var projects = FilesUnder(root, Path.Combine(root, "src")).Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.That(projects, Is.Not.Empty);
        foreach (var project in projects)
        {
            var text = File.ReadAllText(Path.Combine(root, project));
            Assert.That(text.Contains("keyterm", StringComparison.OrdinalIgnoreCase) || text.Contains("glossary", StringComparison.OrdinalIgnoreCase), Is.False,
                $"{project} names a glossary");
        }
    }

    /// <summary>
    /// The example glossary is in examples\glossary, Gulzar's master copy being his own in his settings folder (question 12),
    /// and the test project links it for its own tests alone.
    /// </summary>
    [Test]
    public void The_example_glossary_is_kept_outside_src_and_only_the_tests_link_it()
    {
        var root = RepositoryRoot();

        Assert.That(File.Exists(Path.Combine(root, "examples", "glossary", "keyterms-islamic-sciences.txt")), Is.True);
        Assert.That(File.ReadAllText(Path.Combine(root, "tests", "ClassScribe.Tests", "ClassScribe.Tests.csproj")),
            Does.Contain(@"..\..\examples\glossary\keyterms-islamic-sciences.txt"));
        Assert.That(File.Exists(Path.Combine(AppContext.BaseDirectory, "Examples", "keyterms-islamic-sciences.txt")), Is.True, "copied to the tests' output");
    }
}
