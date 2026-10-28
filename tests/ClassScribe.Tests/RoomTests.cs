using ClassScribe.Storage;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>The room recording takes on a drive (architecture 3.15 a: Room in Application, from the run's monitor).</summary>
[TestFixture]
public class RoomTests
{
    /// <summary>An hour of a class takes an hour of its one track at 48 kHz and an hour of the 16 kHz upload copy, 16-bit mono.</summary>
    [Test]
    public void An_hour_of_a_class_takes_its_track_and_its_upload_copy()
    {
        Assert.That(Room.TrackBytesPerHour, Is.EqualTo(48_000L * 2 * 3600));
        Assert.That(Room.UploadBytesPerHour, Is.EqualTo(16_000L * 2 * 3600));
        Assert.That(Room.For(TimeSpan.FromHours(1)), Is.EqualTo(460_800_000L));
        Assert.That(Room.For(TimeSpan.FromHours(3)), Is.EqualTo(1_382_400_000L));
    }

    /// <summary>A part of a byte counts as a whole one, so that the room is never less than the recording takes.</summary>
    [Test]
    public void A_part_of_a_byte_counts_as_a_whole_one() =>
        Assert.That(Room.For(TimeSpan.FromTicks(1)), Is.EqualTo(1L));

    /// <summary>A YouTube batch keeps room for a class as long as a run's start warns below: three hours.</summary>
    [Test]
    public void A_batch_keeps_room_for_the_class_a_start_warns_below()
    {
        Assert.That(Room.SpaceWarningBelow, Is.EqualTo(TimeSpan.FromHours(3)));
        Assert.That(DownloadPlan.KeptForAClass, Is.EqualTo(Room.SpaceWarningBelow));
        Assert.That(DownloadPlan.ClassReserveBytes, Is.EqualTo(Room.For(Room.SpaceWarningBelow)));
    }
}