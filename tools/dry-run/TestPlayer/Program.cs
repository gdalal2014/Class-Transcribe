// ClassScribeTestPlayer: plays a WAV file ALOUD through Windows' default output device, from a process of its own, so
// that a dry run can record a known "class" with app loopback (record --app ClassScribeTestPlayer). It opens no
// microphone. Its audio session exists from the start: the lead-in is played as silence, so ClassScribe finds the app
// before the sound begins. Start-DryRun.ps1 starts it only with -Player, after Gulzar has typed PLAY; no test runs it.
//
// Usage: ClassScribeTestPlayer <file.wav> [seconds of silence first, 0 to 600]
// Exit codes: 0 played to the end, 2 a mistake in the arguments.
using System.Globalization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

const string Usage = "Usage: ClassScribeTestPlayer <file.wav> [seconds of silence first, 0 to 600]";

var leadIn = 0.0;
if (args.Length is < 1 or > 2 || !File.Exists(args[0])
    || (args.Length == 2 && (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out leadIn) || leadIn is < 0 or > 600)))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

using var reader = new WaveFileReader(args[0]);
var samples = reader.ToSampleProvider();
ISampleProvider source = samples.WaveFormat.Channels == 1 ? new MonoToStereoSampleProvider(samples) : samples;
if (source.WaveFormat.SampleRate != 48000) source = new WdlResamplingSampleProvider(source, 48000);
source = new OffsetSampleProvider(source) { DelayBy = TimeSpan.FromSeconds(leadIn) };

using var output = new WasapiOut(AudioClientShareMode.Shared, 200);
output.Init(source);
output.Play();
Console.WriteLine($"Playing {Path.GetFileName(args[0])} after {leadIn.ToString("0.#", CultureInfo.InvariantCulture)} s of silence, as process {Environment.ProcessId}");
while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(100);
Thread.Sleep(500);
Console.WriteLine("Done");
return 0;
