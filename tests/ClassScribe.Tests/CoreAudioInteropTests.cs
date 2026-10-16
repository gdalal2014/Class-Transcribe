using System.Runtime.Versioning;
using ClassScribe.Audio.Windows;
using NAudio.CoreAudioApi;

namespace ClassScribe.Tests;

/// <summary>
/// ClassScribe's own declarations of Windows' audio interfaces, against the real Windows audio stack. Windows
/// hands out one device enumerator per process, and .NET keeps one wrapper per COM object, so two ways of
/// wrapping the enumerator can clash: whichever side came second used to throw InvalidCastException. These
/// tests only list the output devices; nothing is played or recorded.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsAudio")]
[SupportedOSPlatform("windows")]
public sealed class CoreAudioInteropTests
{
    /// <summary>ClassScribe's enumerator, made while one of NAudio's is in use, lists the same output devices as NAudio's.</summary>
    [Test]
    public void The_enumerator_works_beside_an_NAudio_enumerator_made_before_it()
    {
        LetGoOfUnusedWrappers();
        using var naudio = new MMDeviceEnumerator();

        var enumerator = CoreAudio.CreateEnumerator();
        try
        {
            Assert.That(IdsFrom(enumerator), Is.EquivalentTo(IdsFrom(naudio)));
        }
        finally
        {
            CoreAudio.Release(enumerator);
        }
    }

    /// <summary>NAudio's enumerator, made while ClassScribe's is in use, can be made at all, and lists the same output devices.</summary>
    [Test]
    public void An_NAudio_enumerator_made_after_the_enumerator_works_beside_it()
    {
        LetGoOfUnusedWrappers();
        var enumerator = CoreAudio.CreateEnumerator();
        try
        {
            using var naudio = new MMDeviceEnumerator();

            Assert.That(IdsFrom(naudio), Is.EquivalentTo(IdsFrom(enumerator)));
        }
        finally
        {
            CoreAudio.Release(enumerator);
        }
    }

    /// <summary>Releasing leaves alone what is not a COM object, such as a test's fake, and nothing at all.</summary>
    [Test]
    public void Releasing_leaves_other_objects_alone()
    {
        Assert.DoesNotThrow(() => CoreAudio.Release(new object()));
        Assert.DoesNotThrow(() => CoreAudio.Release(null));
    }

    /// <summary>
    /// Lets the garbage collector finish with wrappers an earlier test left unreferenced, so the test starts
    /// from a process where only what it makes itself wraps Windows' enumerator.
    /// </summary>
    private static void LetGoOfUnusedWrappers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>The identifiers of the active output devices, as NAudio's enumerator lists them.</summary>
    private static List<string> IdsFrom(MMDeviceEnumerator enumerator)
    {
        var ids = new List<string>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) ids.Add(device.ID);
        }

        return ids;
    }

    /// <summary>The identifiers of the active output devices, as ClassScribe's enumerator lists them. Every object is released.</summary>
    private static List<string> IdsFrom(IMMDeviceEnumeratorRaw enumerator)
    {
        CoreAudio.Check(enumerator.EnumAudioEndpoints(CoreAudio.Render, CoreAudio.Active, out var collection));
        try
        {
            CoreAudio.Check(collection.GetCount(out var count));
            var ids = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                CoreAudio.Check(collection.Item(i, out var device));
                try
                {
                    CoreAudio.Check(device.GetId(out var id));
                    ids.Add(id);
                }
                finally
                {
                    CoreAudio.Release(device);
                }
            }

            return ids;
        }
        finally
        {
            CoreAudio.Release(collection);
        }
    }
}
