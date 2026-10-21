// ElevenLabsStub.cs: the dry-run kit's stand-in for ElevenLabs, on this computer only (see README.md beside it).
// A .NET 10 file-based app, started by Start-DryRun.ps1:
//
//   dotnet run tools\dry-run\ElevenLabsStub.cs -- <port> <log file> [--ready <file>] [--delay <seconds>] [--parent <pid>]
//
// It listens on http://127.0.0.1:<port>/ alone (port 0 takes a free one, which --ready reports) and answers:
//   POST /v1/speech-to-text      reads the upload, finds the stretches of the WAV with sound (a simple energy gate),
//                                places a canned text's words on them with start and end times, and answers in
//                                ElevenLabs' shape, after --delay seconds (2 by default);
//   GET  /v1/user/subscription   answers a plausible account, whose credits grow by each transcript's seconds;
//   anything else                404, logged as UNEXPECTED.
// It logs sizes, times and the number of keyterms sent; never a header (the key travels in one), never the terms.
// Ctrl+C, or ending the process, stops it; with --parent it also ends by itself once that process ends (the script passes
// its own, so the stand-in never outlives a dry run whose window was closed with its X). The test project compiles this
// file with DRY_RUN_KIT_TESTS defined, which leaves out the one line of program below, so that its tests reach the parts
// (DryRunStandInTests). ASCII only, as everything in tools\ is (DryRunScriptTests checks it). The types sit in a block
// namespace, since a file-scoped one cannot follow top-level statements.

#if !DRY_RUN_KIT_TESTS
return await ClassScribe.DryRun.StandInProgram.MainAsync(args);
#endif

namespace ClassScribe.DryRun
{
    using System.Buffers.Binary;
    using System.Diagnostics;
    using System.Globalization;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;

    /// <summary>A stretch of an upload in which the energy gate heard sound, in seconds from its start.</summary>
    /// <param name="Start">Where the sound begins.</param>
    /// <param name="End">Where it ends.</param>
    public sealed record SoundStretch(double Start, double End);

    /// <summary>What the stand-in learned from an upload: its format, its length, and where it has sound.</summary>
    /// <param name="SampleRate">Samples a second.</param>
    /// <param name="Channels">The number of channels.</param>
    /// <param name="Seconds">The audio's length.</param>
    /// <param name="Stretches">The stretches with sound, in time order.</param>
    public sealed record UploadAudio(int SampleRate, int Channels, double Seconds, IReadOnlyList<SoundStretch> Stretches);

    /// <summary>One word the stand-in places on the sound.</summary>
    /// <param name="Text">The word.</param>
    /// <param name="Start">Where it starts, in seconds.</param>
    /// <param name="End">Where it ends, in seconds.</param>
    public sealed record PlacedWord(string Text, double Start, double End);

    /// <summary>
    /// Finds where a WAV has sound with a simple energy gate: the RMS level of each 50 ms frame, all channels together,
    /// against about -45 dBFS; pauses shorter than <see cref="BridgeSeconds"/> are bridged, and stretches shorter than
    /// <see cref="ShortestSeconds"/> (a click) are left out. It reads PCM of 16, 24 or 32 bits and 32-bit float, as
    /// ClassScribe's upload copy (16 kHz mono 16-bit) and its recordings are.
    /// </summary>
    public static class SoundFinder
    {
        /// <summary>The length of one frame of the gate.</summary>
        public const double FrameSeconds = 0.05;

        /// <summary>The RMS level, as a fraction of full scale, from which a frame has sound: about -45 dBFS.</summary>
        public const double Threshold = 0.0056;

        /// <summary>The longest pause that stays inside one stretch.</summary>
        public const double BridgeSeconds = 0.6;

        /// <summary>The shortest stretch kept.</summary>
        public const double ShortestSeconds = 0.2;

        /// <summary>Reads <paramref name="wav"/> and finds its stretches with sound.</summary>
        /// <exception cref="InvalidDataException">The bytes are not a WAV of a format the stand-in reads; the message says why.</exception>
        public static UploadAudio Read(ReadOnlySpan<byte> wav)
        {
            if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
                throw new InvalidDataException("The upload is not a WAV file: it has no RIFF and WAVE header.");

            int tag = 0, channels = 0, rate = 0, blockAlign = 0, bits = 0, dataStart = -1, dataLength = 0;
            var position = 12;
            while (position + 8 <= wav.Length)
            {
                var id = wav.Slice(position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(position + 4, 4));
                var body = position + 8;
                var left = wav.Length - body;
                if (id.SequenceEqual("fmt "u8) && size >= 16 && left >= 16)
                {
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2));
                    rate = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(body + 4, 4)));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 12, 2));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2));
                    if (tag == 0xFFFE && size >= 40 && left >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 24, 2));
                }
                else if (id.SequenceEqual("data"u8))
                {
                    // A writer that never finished leaves the size at 0 or larger than the file: the rest of the file is the data.
                    dataStart = body;
                    dataLength = size == 0 || size > left ? left : (int)size;
                    break;
                }

                if (size > (uint)left) break;
                position = body + (int)size + (int)(size & 1);
            }

            if (dataStart < 0 || rate <= 0 || channels <= 0)
                throw new InvalidDataException("The upload's WAV has no format or no data chunk.");
            if (!(tag == 1 && bits is 16 or 24 or 32) && !(tag == 3 && bits == 32))
                throw new InvalidDataException($"The upload's WAV is of format {tag} with {bits} bits; the stand-in reads PCM of 16, 24 or 32 bits and 32-bit float.");
            if (blockAlign != channels * bits / 8)
                throw new InvalidDataException($"The upload's WAV says {blockAlign} bytes a frame for {channels} channels of {bits} bits.");

            var data = wav.Slice(dataStart, dataLength);
            var sampleFrames = dataLength / blockAlign;
            var frameLength = Math.Max(1, (int)Math.Round(rate * FrameSeconds));
            var loud = new List<(int First, int Last)>();
            for (var first = 0; first < sampleFrames; first += frameLength)
            {
                var last = Math.Min(sampleFrames, first + frameLength);
                double sum = 0;
                for (var frame = first; frame < last; frame++)
                {
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var sample = Sample(data.Slice(frame * blockAlign + channel * (bits / 8), bits / 8), tag, bits);
                        sum += sample * sample;
                    }
                }

                if (Math.Sqrt(sum / ((last - first) * channels)) >= Threshold) loud.Add((first, last));
            }

            var stretches = new List<SoundStretch>();
            var bridge = (int)Math.Round(rate * BridgeSeconds);
            foreach (var (first, last) in loud)
            {
                if (stretches.Count > 0 && first - (int)Math.Round(stretches[^1].End * rate) < bridge)
                    stretches[^1] = stretches[^1] with { End = (double)last / rate };
                else
                    stretches.Add(new SoundStretch((double)first / rate, (double)last / rate));
            }

            return new UploadAudio(rate, channels, (double)sampleFrames / rate, [.. stretches.Where(s => s.End - s.Start >= ShortestSeconds - 1e-9)]);
        }

        /// <summary>One sample as a fraction of full scale.</summary>
        private static double Sample(ReadOnlySpan<byte> bytes, int tag, int bits) => (tag, bits) switch
        {
            (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
            (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
            (_, 24) => ((bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8)) / 2147483648.0,
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0
        };
    }

    /// <summary>
    /// The stand-in's answers, in ElevenLabs' shape: the transcript, whose words fall on the stretches with sound, one every
    /// <see cref="WordSeconds"/>; the account; and the refusals.
    /// </summary>
    public static class StandInTranscript
    {
        /// <summary>How far apart the words start.</summary>
        public const double WordSeconds = 0.5;

        /// <summary>How long each word lasts.</summary>
        public const double WordLength = 0.35;

        /// <summary>The canned text, taken word by word and begun again when it runs out.</summary>
        private static readonly string[] Canned =
            ("This is the dry run's stand-in for ElevenLabs. Its words fall where the recording has sound, so the times in " +
             "this transcript follow the audio. Nothing was sent to ElevenLabs, and nothing was billed. A pause in the " +
             "lecture starts a new paragraph, and a mark made with M comes between the paragraphs at its own time.").Split(' ');

        /// <summary>Places words on each stretch of <paramref name="audio"/>: each stretch begins by naming itself, then the canned text goes on.</summary>
        public static IReadOnlyList<PlacedWord> Place(UploadAudio audio)
        {
            var words = new List<PlacedWord>();
            var next = 0;
            for (var index = 0; index < audio.Stretches.Count; index++)
            {
                var stretch = audio.Stretches[index];
                var slots = Math.Max(1, (int)Math.Floor((stretch.End - stretch.Start) / WordSeconds + 1e-9));
                var lead = new[] { "Stretch", (index + 1).ToString(CultureInfo.InvariantCulture), "at",
                    ((int)stretch.Start).ToString(CultureInfo.InvariantCulture), "seconds." };
                for (var slot = 0; slot < slots; slot++)
                {
                    var start = stretch.Start + slot * WordSeconds;
                    var text = slot < lead.Length ? lead[slot] : Canned[next++ % Canned.Length];
                    words.Add(new PlacedWord(text, start, Math.Min(start + WordLength, stretch.End)));
                }
            }

            return words;
        }

        /// <summary>The transcript of <paramref name="audio"/> as ElevenLabs answers it, under <paramref name="transcriptionId"/>.</summary>
        public static string Answer(UploadAudio audio, string transcriptionId) => Answer(audio, transcriptionId, Place(audio));

        /// <summary>The transcript of <paramref name="audio"/> with <paramref name="words"/> placed already, as ElevenLabs answers it.</summary>
        public static string Answer(UploadAudio audio, string transcriptionId, IReadOnlyList<PlacedWord> words) => Json(writer =>
        {
            writer.WriteString("language_code", "eng");
            writer.WriteNumber("language_probability", 1.0);
            writer.WriteString("text", string.Join(" ", words.Select(word => word.Text)));
            writer.WriteStartArray("words");
            for (var i = 0; i < words.Count; i++)
            {
                if (i > 0) Item(writer, " ", words[i - 1].End, words[i].Start, "spacing");
                Item(writer, words[i].Text, words[i].Start, words[i].End, "word");
            }

            writer.WriteEndArray();
            writer.WriteNumber("audio_duration_secs", Math.Round(audio.Seconds, 3));
            writer.WriteString("transcription_id", transcriptionId);
        });

        /// <summary>The account as ElevenLabs' GET /v1/user/subscription answers it.</summary>
        public static string Subscription(long used, long limit, DateTimeOffset reset) => Json(writer =>
        {
            writer.WriteString("tier", "dry_run_stand_in");
            writer.WriteNumber("character_count", used);
            writer.WriteNumber("character_limit", limit);
            writer.WriteBoolean("can_extend_character_limit", false);
            writer.WriteNumber("next_character_count_reset_unix", reset.ToUnixTimeSeconds());
            writer.WriteString("status", "active");
            writer.WriteStartObject("current_overage");
            writer.WriteString("amount", "0.00");
            writer.WriteString("currency", "usd");
            writer.WriteEndObject();
        });

        /// <summary>A refusal in ElevenLabs' shape, with <paramref name="message"/>.</summary>
        public static string Refusal(string status, string message) => Json(writer =>
        {
            writer.WriteStartObject("detail");
            writer.WriteString("status", status);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });

        /// <summary>One item of the word list.</summary>
        private static void Item(Utf8JsonWriter writer, string text, double start, double end, string type)
        {
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteNumber("start", Math.Round(start, 3));
            writer.WriteNumber("end", Math.Round(end, 3));
            writer.WriteString("type", type);
            writer.WriteString("speaker_id", "speaker_0");
            writer.WriteNumber("logprob", 0.0);
            writer.WriteEndObject();
        }

        /// <summary>A JSON object written by <paramref name="write"/>, as text.</summary>
        private static string Json(Action<Utf8JsonWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    /// <summary>
    /// A speech-to-text request as the stand-in reads it from its multipart/form-data body: the audio, the other fields,
    /// and how many glossary terms came (one keyterms part each).
    /// </summary>
    public sealed class StandInUpload
    {
        /// <summary>The audio of the file part.</summary>
        public required ReadOnlyMemory<byte> Audio { get; init; }

        /// <summary>The file part's file name, as sent.</summary>
        public string? FileName { get; init; }

        /// <summary>How many keyterms parts came.</summary>
        public int Keyterms { get; init; }

        /// <summary>The other fields, by name: the first value of each.</summary>
        public required IReadOnlyDictionary<string, string> Fields { get; init; }

        /// <summary>The value of the field <paramref name="name"/>, or null when it was not sent.</summary>
        public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;

        /// <summary>Reads the request's body, sent with <paramref name="contentType"/>.</summary>
        /// <exception cref="InvalidDataException">The body is not multipart/form-data, or has no file part; the message says why.</exception>
        public static StandInUpload Read(string contentType, ReadOnlyMemory<byte> body)
        {
            if (!contentType.TrimStart().StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The request is not multipart/form-data but {(contentType.Length == 0 ? "of no type" : contentType.Split(';')[0])}.");

            var boundary = Parameter(contentType, "boundary")
                ?? throw new InvalidDataException("The request's multipart/form-data has no boundary.");
            var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
            var between = Encoding.ASCII.GetBytes("\r\n--" + boundary);
            var span = body.Span;
            var position = span.IndexOf(delimiter);
            if (position < 0) throw new InvalidDataException("The request's body has no part.");

            position += delimiter.Length;
            ReadOnlyMemory<byte>? audio = null;
            string? fileName = null;
            var keyterms = 0;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            while (!span[position..].StartsWith("--"u8))
            {
                if (!span[position..].StartsWith("\r\n"u8)) throw new InvalidDataException("A part of the request does not begin on a new line.");

                position += 2;
                var headerLength = span[position..].IndexOf("\r\n\r\n"u8);
                if (headerLength < 0) throw new InvalidDataException("A part of the request has no end to its headers.");

                var disposition = Encoding.UTF8.GetString(span.Slice(position, headerLength)).Split("\r\n")
                    .FirstOrDefault(line => line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase)) ?? "";
                var start = position + headerLength + 4;
                var length = span[start..].IndexOf(between);
                if (length < 0) throw new InvalidDataException("A part of the request has no end.");

                var name = Parameter(disposition, "name") ?? "";
                var content = body.Slice(start, length);
                if (name == "file")
                {
                    audio = content;
                    fileName = Parameter(disposition, "filename");
                }
                else if (name == "keyterms")
                {
                    keyterms++;
                }
                else
                {
                    fields.TryAdd(name, Encoding.UTF8.GetString(content.Span));
                }

                position = start + length + between.Length;
            }

            if (audio is not { } file) throw new InvalidDataException("The request has no file part.");

            return new StandInUpload { Audio = file, FileName = fileName, Keyterms = keyterms, Fields = fields };
        }

        /// <summary>The parameter <paramref name="name"/> of a header such as Content-Type or Content-Disposition, unquoted; null when it is not there.</summary>
        private static string? Parameter(string header, string name)
        {
            foreach (var part in header.Split(';').Skip(1))
            {
                var equals = part.IndexOf('=');
                if (equals < 0 || !part[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

                var value = part[(equals + 1)..].Trim();
                return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
            }

            return null;
        }
    }

    /// <summary>The stand-in's log: a line per request, stamped with the time, written to the file at once and shown on the console when asked.</summary>
    /// <param name="path">The log file; lines are added to it.</param>
    /// <param name="echo">True to show each line on the console too.</param>
    public sealed class StandInLog(string path, bool echo)
    {
        private readonly Lock _gate = new();

        /// <summary>Writes <paramref name="line"/>, stamped with the time of day.</summary>
        public void Write(string line)
        {
            var stamped = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line;
            lock (_gate)
            {
                File.AppendAllText(path, stamped + Environment.NewLine);
                if (echo) Console.WriteLine(stamped);
            }
        }
    }

    /// <summary>
    /// The stand-in's server, on .NET's HttpListener at http://127.0.0.1:&lt;port&gt;/ only, as ClassScribe's own listener for
    /// Google's redirect is. Each request is answered on its own task, so the account can be read while an upload is
    /// under way, as ClassScribe does.
    /// </summary>
    public sealed class StandInServer : IAsyncDisposable
    {
        /// <summary>The word that begins the log line of a request to a path the stand-in does not know.</summary>
        public const string Unexpected = "UNEXPECTED";

        /// <summary>The largest upload the stand-in reads: about 13 hours of ClassScribe's upload copy.</summary>
        private const long LargestUpload = 1_500_000_000;

        /// <summary>The credits of the plan the stand-in's account shows.</summary>
        private const long Limit = 500_000;

        private readonly HttpListener _listener;
        private readonly StandInLog _log;
        private readonly TimeSpan _delay;
        private readonly DateTimeOffset _reset = DateTimeOffset.UtcNow.AddDays(30);
        private readonly List<Task> _handlers = [];
        private readonly Lock _gate = new();
        private readonly Task _loop;
        private long _used = 123_456;
        private int _transcripts;

        /// <summary>Takes over a listener that is listening, and begins answering.</summary>
        private StandInServer(HttpListener listener, int port, StandInLog log, TimeSpan delay)
        {
            _listener = listener;
            _log = log;
            _delay = delay;
            Address = new Uri($"http://127.0.0.1:{port}/");
            _log.Write($"The stand-in for ElevenLabs listens on {Address} (process {Environment.ProcessId}); nothing it answers leaves this computer");
            _loop = Task.Run(ListenAsync);
        }

        /// <summary>Where the stand-in answers: http://127.0.0.1:&lt;port&gt;/.</summary>
        public Uri Address { get; }

        /// <summary>Writes <paramref name="line"/> to the stand-in's log, as the program does to say why the stand-in stops.</summary>
        public void Note(string line) => _log.Write(line);

        /// <summary>
        /// Starts the stand-in on <paramref name="port"/>, or on a free port when it is 0, logging to <paramref name="logPath"/>
        /// (and to the console when <paramref name="echo"/>), and answering a transcript after <paramref name="delay"/>.
        /// </summary>
        /// <exception cref="HttpListenerException">The port could not be taken.</exception>
        public static StandInServer Start(int port, string logPath, bool echo = true, TimeSpan delay = default)
        {
            var log = new StandInLog(logPath, echo);
            for (var attempt = 1; ; attempt++)
            {
                var chosen = port == 0 ? FreePort() : port;
                var listener = new HttpListener { IgnoreWriteExceptions = true };
                try
                {
                    listener.Prefixes.Add($"http://127.0.0.1:{chosen}/");
                    listener.Start();
                    return new StandInServer(listener, chosen, log, delay);
                }
                catch (HttpListenerException) when (port == 0 && attempt < 5)
                {
                    // Deliberately not rethrown: another program took the free port in the meantime; another is tried.
                    listener.Close();
                }
                catch (HttpListenerException ex)
                {
                    listener.Close();
                    log.Write($"The stand-in could not listen on port {chosen}: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>Stops listening, lets the requests under way finish, and logs that it stopped.</summary>
        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _loop;
            Task[] running;
            lock (_gate) running = [.. _handlers];
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10));
            _listener.Close();
            _log.Write("The stand-in stopped");
        }

        /// <summary>A port of 127.0.0.1 that is free now.</summary>
        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>Takes requests until the listener stops, each answered on its own task.</summary>
        private async Task ListenAsync()
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    // Deliberately not rethrown: the listener was stopped, which is how the stand-in ends.
                    return;
                }

                lock (_gate)
                {
                    _handlers.RemoveAll(task => task.IsCompleted);
                    _handlers.Add(Task.Run(() => AnswerAsync(context)));
                }
            }
        }

        /// <summary>Answers one request by its method and path; a failure is answered 500 and logged.</summary>
        private async Task AnswerAsync(HttpListenerContext context)
        {
            var method = context.Request.HttpMethod;
            var path = context.Request.Url?.AbsolutePath ?? "?";
            try
            {
                if (method == "POST" && path == "/v1/speech-to-text")
                {
                    await TranscribeAsync(context);
                }
                else if (method == "GET" && path == "/v1/user/subscription")
                {
                    var used = Interlocked.Read(ref _used);
                    await SendAsync(context.Response, 200, StandInTranscript.Subscription(used, Limit, _reset));
                    _log.Write(FormattableString.Invariant($"GET /v1/user/subscription: answered 200 ({used:N0} of {Limit:N0} credits used)"));
                }
                else
                {
                    await SendAsync(context.Response, 404, StandInTranscript.Refusal("not_found", "The dry run's stand-in for ElevenLabs does not answer this path."));
                    _log.Write($"{Unexpected}: {method} {path} (answered 404)");
                }
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: this runs on a task of its own, where nothing would see it; the request is
                // answered 500, as a fault of ElevenLabs' would be, and the log says what went wrong.
                _log.Write($"{method} {path}: failed ({ex.GetType().Name}: {ex.Message}); answered 500");
                try
                {
                    await SendAsync(context.Response, 500, StandInTranscript.Refusal("internal_error", ex.Message));
                }
                catch (Exception answering) when (answering is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    // Deliberately not rethrown: the connection is gone or the answer was begun already; the log has the failure.
                }
            }
        }

        /// <summary>Reads an upload, finds its sound, and answers its transcript, with a line in the log.</summary>
        private async Task TranscribeAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var watch = Stopwatch.StartNew();
            if (request.ContentLength64 > LargestUpload)
            {
                await SendAsync(context.Response, 413, StandInTranscript.Refusal("file_too_large", "The upload is larger than the stand-in reads."));
                _log.Write(FormattableString.Invariant($"POST /v1/speech-to-text: {Megabytes(request.ContentLength64)} MB refused as too large (answered 413)"));
                return;
            }

            using var buffer = new MemoryStream(request.ContentLength64 > 0 ? (int)request.ContentLength64 : 1 << 20);
            var chunk = new byte[1 << 20];
            int read;
            while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > LargestUpload)
                {
                    await SendAsync(context.Response, 413, StandInTranscript.Refusal("file_too_large", "The upload is larger than the stand-in reads."));
                    _log.Write(FormattableString.Invariant($"POST /v1/speech-to-text: more than {Megabytes(LargestUpload)} MB refused as too large (answered 413)"));
                    return;
                }

                buffer.Write(chunk, 0, read);
            }

            var received = FormattableString.Invariant($"POST /v1/speech-to-text: {Megabytes(buffer.Length)} MB in {watch.Elapsed.TotalSeconds:0.0} s");
            StandInUpload upload;
            UploadAudio audio;
            try
            {
                upload = StandInUpload.Read(request.ContentType ?? "", buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
                audio = SoundFinder.Read(upload.Audio.Span);
            }
            catch (InvalidDataException ex)
            {
                // Deliberately not rethrown: a request the stand-in cannot read is answered 400, as ElevenLabs answers one.
                await SendAsync(context.Response, 400, StandInTranscript.Refusal("invalid_request", ex.Message));
                _log.Write($"{received}; refused: {ex.Message} (answered 400)");
                return;
            }

            if (_delay > TimeSpan.Zero) await Task.Delay(_delay);

            var words = StandInTranscript.Place(audio);
            var id = FormattableString.Invariant($"dry-run-{Interlocked.Increment(ref _transcripts)}");
            Interlocked.Add(ref _used, Math.Max(1, (long)Math.Round(audio.Seconds)));
            await SendAsync(context.Response, 200, StandInTranscript.Answer(audio, id, words));
            var sound = TimeSpan.FromSeconds(audio.Stretches.Sum(s => s.End - s.Start));
            _log.Write(FormattableString.Invariant(
                $"{received}; model {upload.Field("model_id") ?? "none"}; {upload.Keyterms} keyterms; audio {TimeSpan.FromSeconds(Math.Round(audio.Seconds)):c} ({audio.SampleRate} Hz, {audio.Channels} ch); {audio.Stretches.Count} stretches with sound ({TimeSpan.FromSeconds(Math.Round(sound.TotalSeconds)):c}); {words.Count} words; answered 200 ({id})"));
        }

        /// <summary>Sends <paramref name="json"/> with <paramref name="status"/> and closes the response.</summary>
        private static async Task SendAsync(HttpListenerResponse response, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = status;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        /// <summary>A number of bytes in megabytes of a million, as upload speeds count them, to one decimal.</summary>
        private static string Megabytes(long bytes) => (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>The stand-in as a program: its arguments, its ready file, Ctrl+C, and the process it ends with.</summary>
    public static class StandInProgram
    {
        /// <summary>How to start the stand-in.</summary>
        public const string Usage = "Usage: dotnet run tools\\dry-run\\ElevenLabsStub.cs -- <port, or 0 for a free one> <log file> [--ready <file>] [--delay <seconds>] [--parent <process id>]";

        /// <summary>
        /// Runs the stand-in as <paramref name="args"/> say (see <see cref="RunAsync"/>) until Ctrl+C, or until the --parent
        /// process ends.
        /// </summary>
        public static async Task<int> MainAsync(string[] args)
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            return await RunAsync(args, stop.Token);
        }

        /// <summary>
        /// Starts the stand-in as <paramref name="args"/> say, writes the ready file ("&lt;process id&gt; &lt;port&gt;") once it
        /// listens, and runs until <paramref name="stop"/> is cancelled or, given --parent, until that process ends: the dry
        /// run passes its own, so that the stand-in never outlives it, even when its window is closed with its X and its own
        /// end never comes. Exit codes: 0 stopped, 2 a mistake in the arguments, 3 the port could not be taken, 4 the --parent
        /// process had ended already, so nothing would have stopped the stand-in.
        /// </summary>
        public static async Task<int> RunAsync(string[] args, CancellationToken stop)
        {
            string? ready = null;
            int? parentId = null;
            var delay = TimeSpan.FromSeconds(2);
            var positional = new List<string>();
            var mistake = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--ready" && i + 1 < args.Length)
                {
                    ready = args[++i];
                }
                else if (args[i] == "--delay" && i + 1 < args.Length)
                {
                    mistake |= !double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds is < 0 or > 600;
                    if (!mistake) delay = TimeSpan.FromSeconds(seconds);
                }
                else if (args[i] == "--parent" && i + 1 < args.Length)
                {
                    var read = int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
                    mistake |= !read;
                    if (read) parentId = id;
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            if (mistake || positional.Count != 2 || !int.TryParse(positional[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            Process? parent = null;
            if (parentId is { } parentProcess)
            {
                try
                {
                    parent = Process.GetProcessById(parentProcess);
                    // Holds the process now, so that its end is seen even if its id is given to another process later.
                    _ = parent.SafeHandle;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // Deliberately not rethrown: the process the stand-in was to end with has ended already, so nothing would
                    // ever stop the stand-in; it does not start, says why, and the exit code tells the script.
                    parent?.Dispose();
                    Console.Error.WriteLine($"The process {parentProcess} that started the stand-in has ended already, so the stand-in does not start.");
                    return 4;
                }
            }

            using (parent)
            {
                StandInServer server;
                try
                {
                    server = StandInServer.Start(port, Path.GetFullPath(positional[1]), echo: true, delay);
                }
                catch (HttpListenerException ex)
                {
                    // Deliberately not rethrown: the reason is logged and told, and the exit code tells the script.
                    Console.Error.WriteLine($"The stand-in could not listen on port {port}: {ex.Message}");
                    return 3;
                }

                await using (server)
                {
                    if (ready is not null)
                    {
                        var staged = ready + ".new";
                        await File.WriteAllTextAsync(staged, FormattableString.Invariant($"{Environment.ProcessId} {server.Address.Port}"), CancellationToken.None);
                        File.Move(staged, ready, overwrite: true);
                    }

                    try
                    {
                        if (parent is null)
                        {
                            await Task.Delay(Timeout.Infinite, stop);
                        }
                        else
                        {
                            await parent.WaitForExitAsync(stop);
                            server.Note(FormattableString.Invariant($"The process that started the stand-in ({parentId}) ended, so the stand-in stops"));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Deliberately not rethrown: Ctrl+C, or the caller's own stop, is how the stand-in is asked to stop.
                    }
                }
            }

            return 0;
        }
    }
}
