using ClassScribe.Core.Audio;

namespace ClassScribe.Tests;

/// <summary>
/// Which processes are the app a name was given for: a whole name beats a part, from the look that first finds a process
/// of exactly that name, and for the rest of the run. The chooser of what to record and the microphone watch both ask it.
/// </summary>
[TestFixture]
public class NamedAppTests
{
    /// <summary>Until a process of exactly the name has been seen, a process whose name is or contains it is the app's, in any case.</summary>
    [Test]
    public void Until_the_app_itself_is_seen_part_of_a_name_is_enough()
    {
        var app = new NamedApp("msedge");

        app.See(["chrome", "msedgewebview2", null]);

        Assert.Multiple(() =>
        {
            Assert.That(app.Name, Is.EqualTo("msedge"));
            Assert.That(app.IsWholeName, Is.False);
            Assert.That(app.Is("msedgewebview2"), Is.True);
            Assert.That(app.Is("MSEdgeWebView2"), Is.True, "in any case");
            Assert.That(app.Is("msedge"), Is.True);
            Assert.That(app.Is("chrome"), Is.False);
        });
    }

    /// <summary>Once a process of exactly the name has been seen, in any case, the name is that app's alone: a namesake is another app.</summary>
    [Test]
    public void Once_the_app_itself_is_seen_the_name_is_its_alone()
    {
        var app = new NamedApp("msedge");

        app.See(["msedgewebview2", "MSEdge"]);

        Assert.Multiple(() =>
        {
            Assert.That(app.IsWholeName, Is.True);
            Assert.That(app.Is("msedge"), Is.True);
            Assert.That(app.Is("MsEdge"), Is.True, "in any case");
            Assert.That(app.Is("msedgewebview2"), Is.False);
        });
    }

    /// <summary>What a look found is remembered for the run: a later look that finds only the namesake does not make the name a part again.</summary>
    [Test]
    public void What_was_seen_is_remembered()
    {
        var app = new NamedApp("msedge");

        app.See(["msedge"]);
        app.See(["msedgewebview2"]);
        app.See([]);

        Assert.Multiple(() =>
        {
            Assert.That(app.IsWholeName, Is.True);
            Assert.That(app.Is("msedgewebview2"), Is.False);
        });
    }

    /// <summary>
    /// One that looks at the same app from another side takes the other's word: given the same name, in any case, it has
    /// seen the app once the other has, and not before. One given another name, or a part of this one, learns nothing.
    /// </summary>
    [Test]
    public void The_word_of_one_that_saw_the_app_is_taken_for_the_same_name_only()
    {
        var chooser = new NamedApp("MSEdge");
        var watch = new NamedApp("msedge");
        var part = new NamedApp("edge");
        var other = new NamedApp("chrome");

        watch.LearnFrom(chooser);
        var beforeItWasSeen = watch.IsWholeName;
        chooser.See(["msedge"]);
        watch.LearnFrom(chooser);
        part.LearnFrom(chooser);
        other.LearnFrom(chooser);

        Assert.Multiple(() =>
        {
            Assert.That(beforeItWasSeen, Is.False);
            Assert.That(watch.IsWholeName, Is.True);
            Assert.That(watch.Is("msedgewebview2"), Is.False);
            Assert.That(part.IsWholeName, Is.False, "edge is a part, whatever msedge has told");
            Assert.That(other.IsWholeName, Is.False);
        });
    }

    /// <summary>A part that no process is named by stays a part, however many looks pass: edge is msedge and msedgewebview2 alike.</summary>
    [Test]
    public void A_part_that_no_process_is_named_by_stays_a_part()
    {
        var app = new NamedApp("edge");

        app.See(["msedge", "msedgewebview2"]);
        app.See(["msedge"]);

        Assert.Multiple(() =>
        {
            Assert.That(app.IsWholeName, Is.False);
            Assert.That(app.Is("msedge"), Is.True);
            Assert.That(app.Is("msedgewebview2"), Is.True);
        });
    }
}
