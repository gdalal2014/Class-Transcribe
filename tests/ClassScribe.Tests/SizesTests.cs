namespace ClassScribe.Tests;

/// <summary>How sizes are written everywhere (architecture 3.15 a: the words for sizes in Core).</summary>
[TestFixture]
public class SizesTests
{
    /// <summary>Below a kilobyte a size is written in bytes, whole.</summary>
    [TestCase(0L, "0 bytes")]
    [TestCase(1023L, "1023 bytes")]
    public void Below_a_kilobyte_a_size_is_written_in_bytes(long bytes, string expected) =>
        Assert.That(Sizes.Describe(bytes), Is.EqualTo(expected));

    /// <summary>
    /// In the units Google One shows, 1 KB being 1024 bytes and so on up: one decimal below 10 of a unit, none from 10 up,
    /// as the README's example of today's class says it (727 MB).
    /// </summary>
    [TestCase(1536L, "1.5 KB")]
    [TestCase(762_314_752L, "727 MB")]
    [TestCase(1_319_413_953_331L, "1.2 TB")]
    [TestCase(32_985_348_833_280L, "30 TB")]
    public void Larger_sizes_are_written_in_Google_Ones_units(long bytes, string expected) =>
        Assert.That(Sizes.Describe(bytes), Is.EqualTo(expected));
}