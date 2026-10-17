using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// What a capture endpoint's property store says about a microphone, and what the choice of microphone reads from it:
/// the built-in array by its jack subtype on a bus that is neither USB nor Bluetooth, a Bluetooth microphone by its
/// enumerator or by any string property naming BTHENUM, BTHLE or Bluetooth, an input that records what the computer
/// plays by its name, and whether unprocessed audio is supported. The stores are played by fakes; nothing is opened,
/// played or recorded. The one real Windows call is PropVariantClear, which frees each value read.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class EndpointFactsTests
{
    /// <summary>The five endpoints of this laptop, by identifier, each with what the rule must say about it: array, Bluetooth, loopback input, and the Bluetooth evidence.</summary>
    private static IEnumerable<TestCaseData> ThisLaptop()
    {
        yield return new TestCaseData(LaptopEndpoints.ArrayId, true, false, false, null).SetArgDisplayNames("the array");
        yield return new TestCaseData(LaptopEndpoints.PixelBudsId, false, true, false, "enumerator BTHLE").SetArgDisplayNames("Pixel Buds");
        yield return new TestCaseData(LaptopEndpoints.SoundcoreId, false, true, false, $"{LaptopEndpoints.IntelDriverKey} = {LaptopEndpoints.SoundcoreDriver}").SetArgDisplayNames("soundcore");
        yield return new TestCaseData(LaptopEndpoints.ObsbotId, false, false, false, null).SetArgDisplayNames("OBSBOT");
        yield return new TestCaseData(LaptopEndpoints.StereoMixId, false, false, true, null).SetArgDisplayNames("Stereo Mix");
    }

    /// <summary>
    /// The rule tells this laptop's five endpoints apart: the array is the built-in one and nothing else; the Pixel Buds
    /// are Bluetooth by their BTHLE bus; the soundcore headset is Bluetooth although its bus is INTELAUDIO like the
    /// array's, because Intel's driver properties say so; the OBSBOT on USB is neither; Stereo Mix records what plays.
    /// </summary>
    [TestCaseSource(nameof(ThisLaptop))]
    public void This_laptops_five_endpoints_are_told_apart(string id, bool array, bool bluetooth, bool loopback, string? evidence)
    {
        var endpoint = LaptopEndpoints.All().Single(candidate => candidate.Id == id);

        var facts = EndpointFacts.Read(endpoint.Store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(facts.IsBuiltInArray, Is.EqualTo(array), "built-in array");
            Assert.That(facts.IsBluetooth, Is.EqualTo(bluetooth), "Bluetooth");
            Assert.That(facts.IsLoopbackInput, Is.EqualTo(loopback), "loopback input");
            Assert.That(facts.BluetoothEvidence, Is.EqualTo(evidence), "evidence");
        }
    }

    /// <summary>A Bluetooth bus is evidence by itself, whichever of Windows' Bluetooth enumerators it is, in any case.</summary>
    [TestCase("BTHENUM")]
    [TestCase("BTHLE")]
    [TestCase("BTHLEDEVICE")]
    [TestCase("BTHHFENUM")]
    [TestCase("bthenum")]
    public void A_Bluetooth_enumerator_is_evidence_by_itself(string enumerator)
    {
        var facts = EndpointFacts.Read(new FakeCaptureEndpoint("{id}", "Headset (Buds)", enumerator).Store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(facts.IsBluetooth, Is.True);
            Assert.That(facts.BluetoothEvidence, Is.EqualTo($"enumerator {enumerator}"));
        }
    }

    /// <summary>
    /// A string property naming BTHENUM, BTHLE or Bluetooth is evidence whatever its key, in any case, and the evidence
    /// names the key and the value: on this laptop the soundcore's bus hides behind Intel's INTELAUDIO enumerator and shows
    /// only in a property of Intel's driver. The properties the listing names, the name among them, count too.
    /// </summary>
    [TestCase("BTHENUM", LaptopEndpoints.SoundcoreBus)]
    [TestCase("BTHLE", @"{1}.BTHLE\DEV_E8EC4A5B0E12\8&1234&0&0001")]
    [TestCase("Bluetooth", "Intel Smart Sound Technology for Bluetooth Audio")]
    [TestCase("bluetooth", "a bluetooth headset")]
    public void A_string_property_naming_Bluetooth_is_evidence_whatever_its_key(string word, string value)
    {
        var endpoint = new FakeCaptureEndpoint("{id}", "Headset (soundcore)", "INTELAUDIO").With(LaptopEndpoints.IntelBusKey, value);

        var facts = EndpointFacts.Read(endpoint.Store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(facts.IsBluetooth, Is.True, word);
            Assert.That(facts.BluetoothEvidence, Is.EqualTo($"{LaptopEndpoints.IntelBusKey} = {value}"));
            Assert.That(EndpointFacts.Read(new FakeCaptureEndpoint("{id}", "Headset (Bluetooth Hands-Free)", "INTELAUDIO").Store).BluetoothEvidence,
                Is.EqualTo($"{PropertyKeys.FriendlyName} = Headset (Bluetooth Hands-Free)"), "the name is a string property too");
        }
    }

    /// <summary>With nothing naming Bluetooth anywhere, a microphone is not Bluetooth, and there is no evidence.</summary>
    [Test]
    public void Without_evidence_a_microphone_is_not_Bluetooth()
    {
        var facts = EndpointFacts.Read(LaptopEndpoints.Obsbot().With(LaptopEndpoints.IntelDriverKey, "USB Audio Device").Store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(facts.IsBluetooth, Is.False);
            Assert.That(facts.BluetoothEvidence, Is.Null);
        }
    }

    /// <summary>
    /// The built-in array is a microphone array by its jack subtype on a bus that is neither USB nor Bluetooth: an array
    /// on USB is some other device's, and one on a Bluetooth bus is a headset's. A plain microphone on the laptop's own bus
    /// is not the array either.
    /// </summary>
    [Test]
    public void An_array_on_a_USB_or_Bluetooth_bus_is_not_the_built_in_one()
    {
        var builtIn = EndpointFacts.Read(new FakeCaptureEndpoint("{a}", "Microphone Array", "INTELAUDIO", EndpointFacts.MicrophoneArrayJack).Store);
        var onUsb = EndpointFacts.Read(new FakeCaptureEndpoint("{b}", "Microphone Array", "usb", EndpointFacts.MicrophoneArrayJack).Store);
        var onBluetooth = EndpointFacts.Read(new FakeCaptureEndpoint("{c}", "Microphone Array", "BTHENUM", EndpointFacts.MicrophoneArrayJack).Store);
        var byProperty = EndpointFacts.Read(new FakeCaptureEndpoint("{d}", "Microphone Array", "INTELAUDIO", EndpointFacts.MicrophoneArrayJack).With(LaptopEndpoints.IntelBusKey, LaptopEndpoints.SoundcoreBus).Store);
        var plain = EndpointFacts.Read(new FakeCaptureEndpoint("{e}", "Microphone", "INTELAUDIO", LaptopEndpoints.MicrophoneJack).Store);
        var noJack = EndpointFacts.Read(new FakeCaptureEndpoint("{f}", "Microphone Array", "INTELAUDIO").Store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(builtIn.IsBuiltInArray, Is.True, "on the laptop's own bus");
            Assert.That(onUsb.IsBuiltInArray, Is.False, "on USB");
            Assert.That(onBluetooth.IsBuiltInArray, Is.False, "on a Bluetooth bus");
            Assert.That(byProperty.IsBuiltInArray, Is.False, "Bluetooth by a property");
            Assert.That(plain.IsBuiltInArray, Is.False, "a plain microphone");
            Assert.That(noJack.IsBuiltInArray, Is.False, "no jack subtype");
        }
    }

    /// <summary>An input that records what the computer plays is known by its name, in any case; a microphone is not one.</summary>
    [TestCase("Stereo Mix (Realtek(R) Audio)", true)]
    [TestCase("What U Hear (Sound Blaster)", true)]
    [TestCase("Wave Out Mix (VIA)", true)]
    [TestCase("stereo mix", true)]
    [TestCase("Microphone (USB Audio)", false)]
    [TestCase("Mixer Input", false)]
    public void A_loopback_input_is_known_by_its_name(string name, bool loopback) =>
        Assert.That(EndpointFacts.Read(new FakeCaptureEndpoint("{id}", name).Store).IsLoopbackInput, Is.EqualTo(loopback));

    /// <summary>
    /// The facts are read from the store by their keys: the name, the enumerator, the jack subtype as an identifier and
    /// whether unprocessed audio is supported; and every string property is read for the Bluetooth check.
    /// </summary>
    [Test]
    public void The_facts_are_read_from_the_store()
    {
        var facts = EndpointFacts.Read(LaptopEndpoints.Soundcore().Store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(facts.Name, Is.EqualTo("Headset (soundcore Liberty 5 Pro)"));
            Assert.That(facts.Enumerator, Is.EqualTo("INTELAUDIO"));
            Assert.That(facts.JackSubType, Is.EqualTo(LaptopEndpoints.HeadsetMicrophoneJack));
            Assert.That(facts.RawProcessingSupported, Is.False);
            Assert.That(facts.StringProperties.Select(property => property.Key), Is.EqualTo(new[]
            {
                PropertyKeys.FriendlyName, PropertyKeys.EnumeratorName, PropertyKeys.JackSubType, LaptopEndpoints.IntelDriverKey, LaptopEndpoints.IntelBusKey
            }));
        }
    }

    /// <summary>A store that lacks the properties reads as no name, an empty enumerator, no jack subtype and no unprocessed audio, rather than failing.</summary>
    [Test]
    public void Missing_properties_read_as_nothing()
    {
        var facts = EndpointFacts.Read(new FakeCaptureEndpoint("{id}", name: null, enumerator: null).Store);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(facts.Name, Is.Null);
            Assert.That(facts.Enumerator, Is.Empty);
            Assert.That(facts.JackSubType, Is.Null);
            Assert.That(facts.RawProcessingSupported, Is.False);
            Assert.That(facts.IsBuiltInArray, Is.False);
            Assert.That(facts.IsBluetooth, Is.False);
            Assert.That(facts.IsLoopbackInput, Is.False);
        }
    }

    /// <summary>A store that fails, as the store of an endpoint that has just gone does, makes the read fail with Windows' result, for the listing to leave the endpoint out.</summary>
    [Test]
    public void A_store_that_fails_makes_the_read_fail_with_its_result()
    {
        var store = new FakeEndpointStore { Failure = FakeResults.DeviceInvalidated };

        var failure = Assert.Throws<COMException>(() => EndpointFacts.Read(store));

        Assert.That(failure!.HResult, Is.EqualTo(FakeResults.DeviceInvalidated));
    }

    /// <summary>
    /// A microphone is made from the facts with the identifier and the default flag the listing adds; an endpoint without
    /// a name is named by its identifier, so that the log can still say which it is.
    /// </summary>
    [Test]
    public void A_microphone_is_made_from_the_facts()
    {
        var soundcore = EndpointFacts.Read(LaptopEndpoints.Soundcore().Store).ToMicrophone(LaptopEndpoints.SoundcoreId, isDefault: false);
        var array = EndpointFacts.Read(LaptopEndpoints.Array().Store).ToMicrophone(LaptopEndpoints.ArrayId, isDefault: true);
        var nameless = EndpointFacts.Read(new FakeCaptureEndpoint("{nameless}", name: null, enumerator: "USB").Store).ToMicrophone("{nameless}", isDefault: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(soundcore, Is.EqualTo(new Microphone(LaptopEndpoints.SoundcoreId, "Headset (soundcore Liberty 5 Pro)", IsDefault: false, IsBuiltInArray: false,
                IsBluetooth: true, IsLoopbackInput: false, RawSupported: false, "INTELAUDIO", $"{LaptopEndpoints.IntelDriverKey} = {LaptopEndpoints.SoundcoreDriver}")));
            Assert.That(array, Is.EqualTo(new Microphone(LaptopEndpoints.ArrayId, LaptopEndpoints.ArrayName, IsDefault: true, IsBuiltInArray: true,
                IsBluetooth: false, IsLoopbackInput: false, RawSupported: true, "INTELAUDIO", null)));
            Assert.That(nameless.Name, Is.EqualTo("{nameless}"));
            Assert.That(nameless.Enumerator, Is.EqualTo("USB"));
        }
    }

    /// <summary>The jack subtypes and the words the rule rests on are the documented ones.</summary>
    [Test]
    public void The_rule_rests_on_the_documented_identifiers_and_words()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(EndpointFacts.MicrophoneArrayJack, Is.EqualTo(new Guid("DFF21BE5-F70F-11D0-B917-00A0C9223196")), "KSNODETYPE_MICROPHONE_ARRAY");
            Assert.That(EndpointFacts.ProcessingMicrophoneArrayJack, Is.EqualTo(new Guid("DFF21BE6-F70F-11D0-B917-00A0C9223196")), "KSNODETYPE_PROCESSING_MICROPHONE_ARRAY");
            Assert.That(EndpointFacts.BluetoothEnumerators, Is.EqualTo(new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM" }));
            Assert.That(EndpointFacts.BluetoothWords, Is.EqualTo(new[] { "BTHENUM", "BTHLE", "Bluetooth" }));
            Assert.That(EndpointFacts.LoopbackInputNames, Is.EqualTo(new[] { "Stereo Mix", "What U Hear", "Wave Out Mix" }));
        }
    }

    /// <summary>A processing microphone array, which some drivers report for the built-in array, counts as the array too.</summary>
    [Test]
    public void A_processing_microphone_array_is_the_built_in_array_too()
    {
        var facts = EndpointFacts.Read(new FakeCaptureEndpoint("{id}", "Microphone Array", "INTELAUDIO", EndpointFacts.ProcessingMicrophoneArrayJack).Store);

        Assert.That(facts.IsBuiltInArray, Is.True);
    }
}
