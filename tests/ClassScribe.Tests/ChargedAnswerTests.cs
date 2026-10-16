using ClassScribe.Transcription;

namespace ClassScribe.Tests;

/// <summary>The mark a failed transcription carries when ElevenLabs had already answered, and charged for the answer.</summary>
[TestFixture]
public class ChargedAnswerTests
{
    private const string Kept = @"C:\Classes\Kalam\Kalam_2026-10-15_20-55-27Z.unread.json";
    private const string Id = "id-1";

    /// <summary>
    /// The mark goes into the exception's Data under ClassScribe.Charged, ClassScribe.KeptAnswer and
    /// ClassScribe.TranscriptionId, so it travels with the exception through every rethrow, whatever its type, and it
    /// reads back as it was made.
    /// </summary>
    [Test]
    public void A_kept_answer_is_marked_in_the_exceptions_data_and_read_back()
    {
        var error = new IOException("The disk is full.");

        ChargedAnswer.Mark(error, Kept, Id);

        Assert.That(error.Data["ClassScribe.Charged"], Is.EqualTo(true));
        Assert.That(error.Data["ClassScribe.KeptAnswer"], Is.EqualTo(Kept));
        Assert.That(error.Data["ClassScribe.TranscriptionId"], Is.EqualTo(Id));
        Assert.That(ChargedAnswer.Of(error), Is.EqualTo(new ChargedAnswer(Kept, Id)));
    }

    /// <summary>An answer that could be kept nowhere is still marked as charged for, with no place to look but with its transcription ID.</summary>
    [Test]
    public void An_answer_kept_nowhere_is_still_marked_as_charged_for()
    {
        var error = new IOException("The disk is full.");

        ChargedAnswer.Mark(error, keptIn: null, Id);

        Assert.That(error.Data["ClassScribe.Charged"], Is.EqualTo(true));
        Assert.That(error.Data.Contains("ClassScribe.KeptAnswer"), Is.False);
        Assert.That(ChargedAnswer.Of(error), Is.EqualTo(new ChargedAnswer(KeptIn: null, Id)));
    }

    /// <summary>
    /// An answer whose transcription ID is not known, such as one that could not be read, is still marked as charged
    /// for, without an ID.
    /// </summary>
    [Test]
    public void An_answer_without_a_known_transcription_id_is_marked_without_one()
    {
        var error = new TranscriptionException("ElevenLabs answered in a form this app cannot read.");

        ChargedAnswer.Mark(error, keptIn: null, transcriptionId: null);

        Assert.That(error.Data["ClassScribe.Charged"], Is.EqualTo(true));
        Assert.That(error.Data.Contains("ClassScribe.TranscriptionId"), Is.False);
        Assert.That(ChargedAnswer.Of(error), Is.EqualTo(new ChargedAnswer(KeptIn: null, TranscriptionId: null)));
    }

    /// <summary>A failure that came before ElevenLabs charged for anything carries no mark.</summary>
    [Test]
    public void A_failure_before_anything_was_charged_has_no_charged_answer() =>
        Assert.That(ChargedAnswer.Of(new TranscriptionException("ElevenLabs is down.")), Is.Null);
}
