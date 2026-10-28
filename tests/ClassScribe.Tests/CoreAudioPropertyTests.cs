using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using ClassScribe.Commands;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Runs;

namespace ClassScribe.Tests;

/// <summary>
/// The readers of a device's property store: ClassScribe's own declarations of PROPERTYKEY, PROPVARIANT and
/// IPropertyStore, and the helpers that read a string, a whole number, a yes or no and an identifier through them.
/// The values are laid out as Windows leaves them in a PROPVARIANT, and read through a store the test plays; no
/// device is opened, and nothing is played or recorded. PropVariantClear, which frees what a value holds, is the one
/// real Windows call, and the test checks that every value read is handed to it.
/// </summary>
[TestFixture, Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class CoreAudioPropertyTests
{
    /// <summary>PKEY_AudioEndpoint_JackSubType's value for the built-in microphone array: KSNODETYPE_MICROPHONE_ARRAY.</summary>
    private static readonly Guid MicrophoneArray = new("DFF21BE5-F70F-11D0-B917-00A0C9223196");

    /// <summary>A PROPVARIANT is 24 bytes in a 64-bit process and 16 in a 32-bit one; a PROPERTYKEY is a GUID and a number.</summary>
    [Test]
    public void The_structures_are_laid_out_as_Windows_lays_them_out()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Marshal.SizeOf<PropVariantRaw>(), Is.EqualTo(IntPtr.Size == 8 ? 24 : 16));
            Assert.That(Marshal.SizeOf<PropertyKeyRaw>(), Is.EqualTo(20));
            Assert.That(Marshal.OffsetOf<PropVariantRaw>(nameof(PropVariantRaw.Type)), Is.EqualTo((IntPtr)0));
            Assert.That(Marshal.OffsetOf<PropVariantRaw>(nameof(PropVariantRaw.Value)), Is.EqualTo((IntPtr)8));
            Assert.That(Marshal.OffsetOf<PropertyKeyRaw>(nameof(PropertyKeyRaw.FormatId)), Is.EqualTo((IntPtr)0));
            Assert.That(Marshal.OffsetOf<PropertyKeyRaw>(nameof(PropertyKeyRaw.PropertyId)), Is.EqualTo((IntPtr)16));
        }
    }

    /// <summary>A string value (VT_LPWSTR) is read from the pointer Windows leaves in the variant's first word.</summary>
    [Test]
    public void A_string_is_read_from_the_bytes_Windows_leaves()
    {
        const string name = "Microphone Array (Intel® Smart Sound Technology for Digital Microphones)";
        var text = Marshal.StringToCoTaskMemUni(name);
        try
        {
            var variant = Recorded(PropVariantRaw.StringType, BitConverter.GetBytes((long)text));

            Assert.That(variant.AsString(), Is.EqualTo(name));
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>A whole number (VT_UI4) is the variant's first four bytes, whatever follows them.</summary>
    [TestCase(new byte[] { 1, 0, 0, 0, 0xAA, 0xAA, 0xAA, 0xAA }, 1u)]
    [TestCase(new byte[] { 0xF0, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 }, 0xFFFFFFF0u)]
    public void A_whole_number_is_read_from_the_bytes_Windows_leaves(byte[] value, uint expected) =>
        Assert.That(Recorded(PropVariantRaw.UInt32Type, value).AsUInt32(), Is.EqualTo(expected));

    /// <summary>A yes or no (VT_BOOL) is a 16-bit VARIANT_BOOL: -1 (0xFFFF) for yes, 0 for no.</summary>
    [TestCase(new byte[] { 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, true)]
    [TestCase(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [TestCase(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, true)]
    public void A_yes_or_no_is_read_from_the_bytes_Windows_leaves(byte[] value, bool expected) =>
        Assert.That(Recorded(PropVariantRaw.BoolType, value).AsBool(), Is.EqualTo(expected));

    /// <summary>An identifier is read from a class id (VT_CLSID), which points at a GUID, and from a string that spells one, as the jack subtype does.</summary>
    [Test]
    public void An_identifier_is_read_from_a_class_id_and_from_its_text()
    {
        var guid = Marshal.AllocCoTaskMem(16);
        var text = Marshal.StringToCoTaskMemUni("{DFF21BE5-F70F-11D0-B917-00A0C9223196}");
        try
        {
            Marshal.StructureToPtr(MicrophoneArray, guid, false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Recorded(PropVariantRaw.ClassIdType, BitConverter.GetBytes((long)guid)).AsGuid(), Is.EqualTo(MicrophoneArray));
                Assert.That(Recorded(PropVariantRaw.StringType, BitConverter.GetBytes((long)text)).AsGuid(), Is.EqualTo(MicrophoneArray));
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(guid);
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>
    /// A value of another type than the one asked for reads as nothing rather than as a wrong value: an empty variant
    /// (VT_EMPTY, what Windows leaves for a property the device lacks) reads as nothing of any kind, a number is no
    /// string, a signed number (VT_I4) is not taken for an unsigned one, and a string that spells no identifier is none.
    /// </summary>
    [Test]
    public void A_value_of_another_type_reads_as_nothing()
    {
        var text = Marshal.StringToCoTaskMemUni("BTHENUM");
        try
        {
            var empty = Recorded(PropVariantRaw.EmptyType, new byte[8]);
            var number = Recorded(PropVariantRaw.UInt32Type, [1, 0, 0, 0, 0, 0, 0, 0]);
            var signed = Recorded(3, [1, 0, 0, 0, 0, 0, 0, 0]);
            var word = Recorded(PropVariantRaw.StringType, BitConverter.GetBytes((long)text));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(empty.AsString(), Is.Null);
                Assert.That(empty.AsUInt32(), Is.Null);
                Assert.That(empty.AsBool(), Is.Null);
                Assert.That(empty.AsGuid(), Is.Null);
                Assert.That(number.AsString(), Is.Null);
                Assert.That(number.AsBool(), Is.Null);
                Assert.That(signed.AsUInt32(), Is.Null);
                Assert.That(word.AsUInt32(), Is.Null);
                Assert.That(word.AsGuid(), Is.Null);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>
    /// Each reader asks the store for its key and reads the value as its kind: the name, the form factor, whether
    /// unprocessed audio is supported, and the jack subtype as an identifier. A property the store lacks reads as null.
    /// </summary>
    [Test]
    public void Each_reader_reads_its_kind_of_value_from_the_store()
    {
        var store = new FakePropertyStore();
        store.Add(PropertyKeys.FriendlyName, "Microphone Array (Intel® Smart Sound Technology)");
        store.Add(PropertyKeys.EnumeratorName, "INTELAUDIO");
        store.Add(PropertyKeys.FormFactor, 1u);
        store.Add(PropertyKeys.RawProcessingSupported, true);
        store.Add(PropertyKeys.JackSubType, MicrophoneArray.ToString("B").ToUpperInvariant());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CoreAudio.ReadString(store, PropertyKeys.FriendlyName), Is.EqualTo("Microphone Array (Intel® Smart Sound Technology)"));
            Assert.That(CoreAudio.ReadString(store, PropertyKeys.EnumeratorName), Is.EqualTo("INTELAUDIO"));
            Assert.That(CoreAudio.ReadUInt(store, PropertyKeys.FormFactor), Is.EqualTo(1u));
            Assert.That(CoreAudio.ReadBool(store, PropertyKeys.RawProcessingSupported), Is.True);
            Assert.That(CoreAudio.ReadGuid(store, PropertyKeys.JackSubType), Is.EqualTo(MicrophoneArray));
            Assert.That(CoreAudio.ReadString(store, new PropertyKeyRaw(Guid.NewGuid(), 1)), Is.Null, "a property the device lacks");
            Assert.That(CoreAudio.ReadUInt(store, PropertyKeys.FriendlyName), Is.Null, "a string is no number");
            Assert.That(store.Asked, Has.Count.EqualTo(7));
        }
    }

    /// <summary>A store that fails to answer makes the reader fail with Windows' result, rather than answer null as if the property were missing.</summary>
    [Test]
    public void A_store_that_fails_makes_the_reader_fail_with_its_result()
    {
        var store = new FakePropertyStore { Failure = FakeResults.DeviceInvalidated };

        var failure = Assert.Throws<COMException>(() => CoreAudio.ReadString(store, PropertyKeys.FriendlyName));

        Assert.That(failure!.HResult, Is.EqualTo(FakeResults.DeviceInvalidated));
    }

    /// <summary>
    /// Whatever a value holds is freed after it is read, whether or not it was of the kind asked for. Windows counts the
    /// references to an object a variant holds (VT_UNKNOWN): one is taken for the variant, and the reader's
    /// PropVariantClear gives it back, so the count is where it was before the read.
    /// </summary>
    [Test]
    public void Every_value_read_is_freed_whatever_it_held()
    {
        var store = new FakePropertyStore();
        var held = new object();
        var pointer = Marshal.GetIUnknownForObject(held);
        try
        {
            store.AddObject(PropertyKeys.FriendlyName, pointer);   // takes a reference of its own
            var before = Marshal.AddRef(pointer) - 1;
            Marshal.Release(pointer);

            Assert.That(CoreAudio.ReadString(store, PropertyKeys.FriendlyName), Is.Null, "an object is no string");

            var after = Marshal.AddRef(pointer) - 1;
            Marshal.Release(pointer);
            Assert.That(after, Is.EqualTo(before), "the reference taken for the variant was given back");
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Every string property of a store is listed with its key, in the store's order, numbers and the rest left out: the Bluetooth check reads them all.</summary>
    [Test]
    public void Every_string_property_is_listed_with_its_key()
    {
        var store = new FakePropertyStore();
        var vendor = new PropertyKeyRaw(new Guid("B3F8FA53-0004-438E-9003-51A46E139BFC"), 2);
        store.Add(PropertyKeys.FriendlyName, "Headset (soundcore Q30)");
        store.Add(PropertyKeys.FormFactor, 5u);
        store.Add(vendor, "BTHENUM\\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001000A");
        store.Add(PropertyKeys.RawProcessingSupported, false);

        var strings = CoreAudio.ReadStringProperties(store);

        Assert.That(strings, Is.EqualTo(new[]
        {
            (PropertyKeys.FriendlyName, "Headset (soundcore Q30)"),
            (vendor, "BTHENUM\\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001000A")
        }));
    }

    /// <summary>The keys ClassScribe reads are the ones Microsoft documents, by format identifier and number.</summary>
    [Test]
    public void The_well_known_keys_are_the_documented_ones()
    {
        var device = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        var endpoint = new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PropertyKeys.FriendlyName, Is.EqualTo(new PropertyKeyRaw(device, 14)));
            Assert.That(PropertyKeys.EnumeratorName, Is.EqualTo(new PropertyKeyRaw(device, 24)));
            Assert.That(PropertyKeys.FormFactor, Is.EqualTo(new PropertyKeyRaw(endpoint, 0)));
            Assert.That(PropertyKeys.JackSubType, Is.EqualTo(new PropertyKeyRaw(endpoint, 8)));
            Assert.That(PropertyKeys.RawProcessingSupported, Is.EqualTo(new PropertyKeyRaw(new Guid("8943B373-388C-4395-B557-BC6DBAFFAFDB"), 2)));
        }
    }

    /// <summary>A variant as Windows leaves it: the type in the first two bytes, six reserved bytes, then the value's first eight bytes.</summary>
    private static PropVariantRaw Recorded(ushort type, byte[] value)
    {
        var bytes = new byte[Marshal.SizeOf<PropVariantRaw>()];
        BitConverter.GetBytes(type).CopyTo(bytes, 0);
        value.AsSpan(0, Math.Min(value.Length, IntPtr.Size)).CopyTo(bytes.AsSpan(8));
        return MemoryMarshal.Read<PropVariantRaw>(bytes);
    }

    /// <summary>
    /// A property store as a test plays it, holding the values it was given. Each value is handed out as Windows hands it
    /// out, in memory the reader must free: a string in task memory, an object with a reference taken for the variant.
    /// A property it lacks answers an empty variant, as Windows does; while <see cref="Failure"/> is set, every read
    /// answers that result instead.
    /// </summary>
    private sealed class FakePropertyStore : IPropertyStoreRaw
    {
        private readonly List<(PropertyKeyRaw Key, Func<PropVariantRaw> Value)> _properties = [];
        private readonly List<PropertyKeyRaw> _asked = [];

        /// <summary>When not 0, the HRESULT every read answers with.</summary>
        public int Failure { get; init; }

        /// <summary>The keys asked for, in order.</summary>
        public IReadOnlyList<PropertyKeyRaw> Asked => _asked;

        /// <summary>Gives the store a string property.</summary>
        public void Add(PropertyKeyRaw key, string value) => _properties.Add((key, () =>
        {
            var text = Marshal.StringToCoTaskMemUni(value);
            return new PropVariantRaw { Type = PropVariantRaw.StringType, Value = text };
        }));

        /// <summary>Gives the store a whole-number property.</summary>
        public void Add(PropertyKeyRaw key, uint value) => _properties.Add((key, () => new PropVariantRaw { Type = PropVariantRaw.UInt32Type, Value = (IntPtr)(long)value }));

        /// <summary>Gives the store a yes-or-no property.</summary>
        public void Add(PropertyKeyRaw key, bool value) => _properties.Add((key, () => new PropVariantRaw { Type = PropVariantRaw.BoolType, Value = (IntPtr)(value ? 0xFFFF : 0) }));

        /// <summary>Gives the store a property holding an object (VT_UNKNOWN), a reference to which is taken for each variant handed out.</summary>
        public void AddObject(PropertyKeyRaw key, IntPtr unknown) => _properties.Add((key, () =>
        {
            Marshal.AddRef(unknown);
            return new PropVariantRaw { Type = PropVariantRaw.UnknownType, Value = unknown };
        }));

        /// <inheritdoc />
        public int GetCount(out int count)
        {
            count = _properties.Count;
            return Failure == 0 ? FakeResults.Ok : Failure;
        }

        /// <inheritdoc />
        public int GetAt(int index, out PropertyKeyRaw key)
        {
            key = Failure == 0 ? _properties[index].Key : default;
            return Failure == 0 ? FakeResults.Ok : Failure;
        }

        /// <inheritdoc />
        public int GetValue(ref PropertyKeyRaw key, out PropVariantRaw value)
        {
            _asked.Add(key);
            value = default;
            if (Failure != 0) return Failure;

            var wanted = key;
            var found = _properties.FirstOrDefault(property => property.Key.Equals(wanted));
            if (found.Value is not null) value = found.Value();
            return FakeResults.Ok;
        }

        /// <inheritdoc />
        public int SetValue(ref PropertyKeyRaw key, ref PropVariantRaw value) => throw new AssertionException("The readers set a property.");

        /// <inheritdoc />
        public int Commit() => throw new AssertionException("The readers committed a property.");
    }
}

/// <summary>
/// The contracts the microphone is built on, and the fakes that play them: the packets a capture hands over, each with
/// its frame count, the time stamp Windows gives the first frame and Windows' flags, so that a session can place it on
/// the recording's clock; the fake output devices saying whether they are heard in the room and the fake apps saying
/// which device they play through; the rule for which app the microphone follows, and how it and the microphone's state
/// are written down in the run files and in the link's messages, so that a build before them reads what a build after them
/// writes, and the other way round.
/// </summary>
[TestFixture]
public sealed class MicrophoneContractTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2, 32, IsFloat: true);

    /// <summary>A microphone's state as the gate that follows the class app fills it in: every field set, none at its default.</summary>
    private static readonly MicrophoneState Gated = new("Microphone Array", On: true, Capturing: true, Problem: null,
        Follows: "chrome", Gate: MicrophoneGateState.NotListening, Why: "chrome is not listening");

    /// <summary>The three kinds of follow rule, each with the word that names it in a file.</summary>
    private static IEnumerable<TestCaseData> FollowRules()
    {
        yield return new TestCaseData(new FollowRule.Named("chrome"), "named").SetArgDisplayNames("named");
        yield return new TestCaseData(new FollowRule.Any(), "any").SetArgDisplayNames("any");
        yield return new TestCaseData(new FollowRule.None(), "none").SetArgDisplayNames("none");
    }

    /// <summary>
    /// A request whose microphone follows an app is written to the request file with the rule named by "kind", next to the
    /// name part when there is one, and reads back as it was. The rule is a kind among kinds, as a class source is, so
    /// the file tells chrome from any app from no app rather than reading every rule as the first kind.
    /// </summary>
    [TestCaseSource(nameof(FollowRules))]
    public void A_follow_rule_is_named_by_kind_in_the_request_file_and_reads_back(FollowRule rule, string kind)
    {
        var request = TestRuns.Request(TestRuns.Id()) with
        {
            Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice("Array", rule))
        };

        var json = JsonSerializer.Serialize(request, RecorderJson.Default.RecordRequest);
        var written = JsonNode.Parse(json)!["source"]!["microphone"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written["namePart"]!.GetValue<string>(), Is.EqualTo("Array"));
            Assert.That(written["follows"]!["kind"]!.GetValue<string>(), Is.EqualTo(kind));
            Assert.That(written["follows"]!["namePart"]?.GetValue<string>(), Is.EqualTo((rule as FollowRule.Named)?.NamePart));
            Assert.That(JsonSerializer.Deserialize(json, RecorderJson.Default.RecordRequest), Is.EqualTo(request));
        }
    }

    /// <summary>
    /// The same over the link, which reads strictly: a Welcome carrying a run whose microphone follows an app, and the
    /// state of a microphone the gate is holding, with the app it follows, why it is not recording and the words for it,
    /// read back as they were sent. The gate is written by name, so both ends agree whatever order a build lists it in.
    /// </summary>
    [Test]
    public void A_follow_rule_and_the_gates_state_travel_over_the_link()
    {
        var run = TestRuns.Record(TestRuns.Id(), RunState.Recording) with
        {
            Request = TestRuns.Request(TestRuns.Id()) with
            {
                Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("chrome"), new MicrophoneChoice(null, new FollowRule.Named("zoom")))
            }
        };
        var state = new RecorderState(RecorderPhase.Recording, "Kalam", TimeSpan.FromSeconds(10), "chrome", WaitingFor: null, Gated,
            UploadPercent: null, StopsAtUtc: null, Attention.None, InWindow: false);
        var welcome = new Welcome(LinkProtocol.Version, "1.0.0+test", @"C:\x\ClassScribe.exe", run, state, OldestSeq: 7);
        var update = new StateUpdate(state);

        var welcomeJson = JsonSerializer.Serialize(welcome, LinkJson.Default.Welcome);
        var updateJson = JsonSerializer.Serialize(update, LinkJson.Default.StateUpdate);
        var microphone = JsonNode.Parse(updateJson)!["state"]!["microphone"]!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(microphone["follows"]!.GetValue<string>(), Is.EqualTo("chrome"));
            Assert.That(microphone["gate"]!.GetValue<string>(), Is.EqualTo("NotListening"));
            Assert.That(microphone["why"]!.GetValue<string>(), Is.EqualTo("chrome is not listening"));
            Assert.That(JsonSerializer.Deserialize(welcomeJson, LinkJson.Default.Welcome), Is.EqualTo(welcome));
            Assert.That(JsonSerializer.Deserialize(updateJson, LinkJson.Default.StateUpdate), Is.EqualTo(update));
        }
    }

    /// <summary>
    /// A microphone's state from a build before the gate, with only its first four fields, reads with the gate's fields at
    /// their defaults through the run files and over the link alike: no app followed, Unfollowed rather than the first value
    /// of the list, and no words; and a microphone choice from such a build follows no app. The window of a newer build
    /// then shows an older recorder's microphone as recorded whenever it is on, which is what that recorder does.
    /// </summary>
    [Test]
    public void A_state_and_a_choice_from_a_build_before_the_gate_read_with_its_defaults()
    {
        const string state = """{"name":"Microphone Array","on":true,"capturing":false,"problem":"muted in Windows"}""";
        const string choice = """{"namePart":"Array"}""";
        var expected = new MicrophoneState("Microphone Array", On: true, Capturing: false, Problem: "muted in Windows");

        var fromFile = JsonSerializer.Deserialize(state, RecorderJson.Default.MicrophoneState);
        var overLink = JsonSerializer.Deserialize(state, LinkJson.Default.MicrophoneState);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromFile, Is.EqualTo(expected));
            Assert.That(overLink, Is.EqualTo(expected));
            Assert.That(fromFile!.Gate, Is.EqualTo(MicrophoneGateState.Unfollowed));
            Assert.That(fromFile.Follows, Is.Null);
            Assert.That(fromFile.Why, Is.Null);
            Assert.That(overLink!.Gate, Is.EqualTo(MicrophoneGateState.Unfollowed));
            Assert.That(JsonSerializer.Deserialize(choice, RecorderJson.Default.MicrophoneChoice), Is.EqualTo(new MicrophoneChoice("Array")));
        }
    }

    /// <summary>
    /// The app to follow is spelled as a setting or an option spells it: auto for any app, none for no app, in any case,
    /// and otherwise part of an app's name, trimmed, with a trailing .exe dropped since a process name has none. An empty
    /// value spells no rule.
    /// </summary>
    [Test]
    public void The_follow_rule_is_read_from_its_words()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(FollowRule.TryParse("auto", out var any) && any is FollowRule.Any, Is.True);
            Assert.That(FollowRule.TryParse(" NONE ", out var none) && none is FollowRule.None, Is.True);
            Assert.That(FollowRule.TryParse(" Chrome.exe ", out var named) ? named : null, Is.EqualTo(new FollowRule.Named("Chrome")));
            Assert.That(FollowRule.TryParse("ms-teams", out var teams) ? teams : null, Is.EqualTo(new FollowRule.Named("ms-teams")));
            Assert.That(FollowRule.TryParse("", out var empty), Is.False);
            Assert.That(empty, Is.Null);
            Assert.That(FollowRule.TryParse("   ", out _), Is.False);
            Assert.That(FollowRule.TryParse(".exe", out _), Is.False);
            Assert.That(FollowRule.TryParse(null, out _), Is.False);
        }
    }

    /// <summary>
    /// A named rule matches a process whose name is or contains the name part, in any case, as --app matches apps: chrome
    /// matches Chrome's chrome.exe utility process and, as --app chrome does today, chromedriver too; it does not match
    /// msedge. Any part of ClassScribe's own name matches ClassScribe, which is why the settings refuse one.
    /// </summary>
    [TestCase("chrome", "chrome", true)]
    [TestCase("chrome", "Chrome", true)]
    [TestCase("chrome", "chromedriver", true)]
    [TestCase("chrome", "msedge", false)]
    [TestCase("scribe", "ClassScribe", true)]
    [TestCase("ClassScribe", "ClassScribe", true)]
    public void A_named_rule_matches_a_process_whose_name_contains_the_name_part(string namePart, string processName, bool matches) =>
        Assert.That(new FollowRule.Named(namePart).Matches(processName), Is.EqualTo(matches));

    /// <summary>A packet carries its audio, its frames, its stamp in 100-nanosecond units and its flags; delivered as before, it carries no stamp and no flags.</summary>
    [Test]
    public void A_packet_carries_its_stamp_and_its_flags()
    {
        var capture = new FakeAudioCapture(Stereo48k);
        var packets = new List<AudioDataEventArgs>();
        capture.DataAvailable += (_, packet) => packets.Add(packet);
        var audio = TestAudio.Floats(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f);

        capture.Deliver(audio, qpc100ns: 1_234_567_890, PacketFlags.Discontinuity | PacketFlags.Silent);
        capture.Deliver(audio);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(packets[0].Buffer, Is.SameAs(audio));
            Assert.That(packets[0].ByteCount, Is.EqualTo(24));
            Assert.That(packets[0].Frames, Is.EqualTo(3));
            Assert.That(packets[0].Qpc100ns, Is.EqualTo(1_234_567_890));
            Assert.That(packets[0].Flags, Is.EqualTo(PacketFlags.Discontinuity | PacketFlags.Silent));
            Assert.That(packets[1].Qpc100ns, Is.Null);
            Assert.That(packets[1].Flags, Is.EqualTo(PacketFlags.None));
            Assert.That(packets[1].Frames, Is.EqualTo(3));
        }
    }

    /// <summary>A packet whose stamp Windows flags as wrong carries no stamp, whatever number came with the flag, so that nothing is placed by it.</summary>
    [Test]
    public void A_packet_whose_stamp_is_flagged_wrong_carries_none()
    {
        var packet = new AudioDataEventArgs(new byte[8], 8, frames: 1, qpc100ns: 500, PacketFlags.TimestampError);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(packet.Qpc100ns, Is.Null);
            Assert.That(packet.Flags, Is.EqualTo(PacketFlags.TimestampError));
        }
    }

    /// <summary>
    /// The fake provider says which output devices are heard in the room, as the speakers are and headphones are not,
    /// and through which device each app plays, by the device's identifier, which the fake makes the same as its name
    /// unless told otherwise.
    /// </summary>
    [Test]
    public void The_fake_provider_tells_the_devices_heard_in_the_room_and_each_apps_device()
    {
        var provider = new FakeCaptureProvider();
        provider.ConnectDevice("Speakers", level: 0.2f, heardInTheRoom: true);
        provider.ConnectDevice("Headphones");
        provider.AddApp(4242, "chrome", "Speakers", level: 0.3f);
        provider.AddApp(5151, "msedge", "Headphones", deviceId: "headphones-id");

        var devices = provider.GetOutputDevices();
        var apps = provider.GetPlayingApps().OrderBy(app => app.ProcessId).ToList();
        provider.SetHeardInTheRoom("Headphones", true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(devices, Is.EqualTo(new[]
            {
                new OutputDevice("Speakers", "Speakers", IsDefault: true, Level: 0.2f, HeardInTheRoom: true),
                new OutputDevice("Headphones", "Headphones", IsDefault: false, Level: 0, HeardInTheRoom: false)
            }));
            Assert.That(apps, Is.EqualTo(new[]
            {
                new PlayingApp(4242, "chrome", "Speakers", 0.3f, DeviceId: "Speakers"),
                new PlayingApp(5151, "msedge", "Headphones", 0, DeviceId: "headphones-id")
            }));
            Assert.That(provider.GetOutputDevices().Single(device => device.Id == "Headphones").HeardInTheRoom, Is.True);
        }
    }
}
