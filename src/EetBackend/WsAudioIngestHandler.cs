using System.Buffers.Binary;
using System.Net.WebSockets;

public sealed class WsAudioIngestHandler
{
    private const uint HandshakeMagic = 0x41445043; // "ADPC"
    private const uint AdpcmFrameMagic = 0x41445046; // "ADPF"
    private const uint PcmFrameMagic = 0x464D4350; // "PCMF"
    private const int HandshakeLength = 32;
    private const int FrameHeaderLength = 12;
    private static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<WsAudioIngestHandler> _logger;
    private readonly S3FileUploader _uploader;
    private readonly S3Options _s3Options;

    public WsAudioIngestHandler(
        ILogger<WsAudioIngestHandler> logger,
        S3FileUploader uploader,
        Microsoft.Extensions.Options.IOptions<S3Options> s3Options
    )
    {
        _logger = logger;
        _uploader = uploader;
        _s3Options = s3Options.Value;
    }

    public async Task HandleAsync(HttpContext context, string? hwid)
    {
        var hwidTag = SanitizeTag(hwid);
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[64 * 1024];
        Handshake? handshake = null;
        uint? expectedSeq = null;
        long totalFrames = 0;
        long totalBytes = 0;
        long totalPcmBytes = 0;
        var env = context.RequestServices.GetRequiredService<IHostEnvironment>();
        var projectRoot =
            Directory.GetParent(env.ContentRootPath)?.Parent?.FullName ?? env.ContentRootPath;
        var baseDir = Path.Combine(projectRoot, "data", "received");
        var streamDir = hwidTag is null ? baseDir : Path.Combine(baseDir, hwidTag);
        Directory.CreateDirectory(streamDir);
        FileStream? currentFile = null;
        string? currentFilePath = null;
        WavWriter? wavWriter = null;
        string? currentWavPath = null;
        bool wavEnabled = false;
        DateTime currentFileStartUtc = DateTime.MinValue;

        try
        {
            while (true)
            {
                // Reassemble fragmented WebSocket messages into a single payload.
                var message = await ReceiveFullMessageAsync(ws, buffer, context.RequestAborted);
                if (message is null)
                {
                    break;
                }

                var msg = message.Value;
                if (msg.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "bye",
                        context.RequestAborted
                    );
                    break;
                }

                if (msg.MessageType != WebSocketMessageType.Binary)
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "binary required",
                        context.RequestAborted
                    );
                    break;
                }

                var data = msg.Payload;
                // First binary message must be the handshake.
                if (handshake is null)
                {
                    if (!TryParseHandshake(data, out var hs))
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.PolicyViolation,
                            "invalid handshake",
                            context.RequestAborted
                        );
                        break;
                    }

                    handshake = hs;
                    wavEnabled = (hs.Codec == 0) || (hs.Codec == 1 && hs.Channels == 1);
                    _logger.LogInformation(
                        "Handshake OK stream_id={StreamId} sample_rate={SampleRate} channels={Channels} codec={Codec} frame_samples={FrameSamples}",
                        hs.StreamId,
                        hs.SampleRate,
                        hs.Channels,
                        hs.Codec,
                        hs.FrameSamples
                    );
                    if (!wavEnabled)
                    {
                        _logger.LogWarning(
                            "WAV output disabled (requires PCM or IMA ADPCM mono). stream_id={StreamId} codec={Codec} channels={Channels}",
                            hs.StreamId,
                            hs.Codec,
                            hs.Channels
                        );
                    }
                    continue;
                }

                // All following messages must be audio frames.
                if (!TryParseAudioFrame(data, out var frame))
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "invalid audio frame",
                        context.RequestAborted
                    );
                    break;
                }

                if (!IsFrameCodecMatch(handshake.Value.Codec, frame.Magic))
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "frame codec mismatch",
                        context.RequestAborted
                    );
                    break;
                }

                if (!ValidateFramePayload(handshake.Value, frame))
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "frame payload size invalid",
                        context.RequestAborted
                    );
                    break;
                }

                if (expectedSeq is not null && frame.Seq != expectedSeq.Value)
                {
                    _logger.LogWarning(
                        "Frame sequence gap stream_id={StreamId} expected={Expected} got={Got}",
                        handshake.Value.StreamId,
                        expectedSeq.Value,
                        frame.Seq
                    );
                }

                expectedSeq = frame.Seq + 1;
                totalFrames++;
                totalBytes += frame.Raw.Length;

                var now = DateTime.UtcNow;
                if (currentFile is null || (now - currentFileStartUtc) >= RotationInterval)
                {
                    // Rotate output files every interval.
                    if (currentFile is not null)
                    {
                        currentFile.Dispose();
                        if (currentFilePath is not null)
                        {
                            await UploadIfEnabledAsync(currentFilePath, hwidTag, context.RequestAborted);
                        }
                    }

                    if (wavWriter is not null)
                    {
                        wavWriter.Dispose();
                        if (currentWavPath is not null)
                        {
                            await UploadIfEnabledAsync(currentWavPath, hwidTag, context.RequestAborted);
                        }
                    }

                    currentFileStartUtc = now;
                    var fileName = hwidTag is null
                        ? $"stream_{handshake.Value.StreamId}_{now:yyyyMMdd_HHmmss}.bin"
                        : $"stream_{handshake.Value.StreamId}_{hwidTag}_{now:yyyyMMdd_HHmmss}.bin";
                    var path = Path.Combine(streamDir, fileName);
                    currentFilePath = path;
                    currentFile = new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read
                    );
                    if (wavEnabled)
                    {
                        var wavPath = Path.ChangeExtension(path, ".wav");
                        currentWavPath = wavPath;
                        wavWriter = new WavWriter(
                            wavPath,
                            (int)handshake.Value.SampleRate,
                            (short)handshake.Value.Channels,
                            16
                        );
                        _logger.LogInformation(
                        "Opened new WAV file stream_id={StreamId} hwid={Hwid} path={Path}",
                        handshake.Value.StreamId,
                        hwidTag ?? "-",
                        wavPath
                    );
                    }
                    _logger.LogInformation(
                        "Opened new stream file stream_id={StreamId} hwid={Hwid} path={Path}",
                        handshake.Value.StreamId,
                    hwidTag ?? "-",
                    path
                );
            }

                await currentFile!.WriteAsync(frame.Raw, context.RequestAborted);
                await currentFile.FlushAsync(context.RequestAborted);

                if (wavWriter is not null)
                {
                    if (handshake.Value.Codec == 0)
                    {
                        // PCM frames can be written directly.
                        var pcm = frame.Payload.ToArray();
                        await wavWriter.WriteAsync(pcm, context.RequestAborted);
                        totalPcmBytes += pcm.Length;
                    }
                    else
                    {
                        // ADPCM frames must be decoded to PCM.
                        if (!TryDecodeImaAdpcm(frame.Payload, out var pcm))
                        {
                            _logger.LogWarning(
                                "ADPCM decode failed stream_id={StreamId} seq={Seq}",
                                handshake.Value.StreamId,
                                frame.Seq
                            );
                        }
                        else
                        {
                            await wavWriter.WriteAsync(pcm, context.RequestAborted);
                            totalPcmBytes += pcm.Length;
                        }
                    }
                }
            }
        }
        finally
        {
            if (currentFile is not null)
            {
                currentFile.Dispose();
                if (currentFilePath is not null)
                {
                    await UploadIfEnabledAsync(currentFilePath, hwidTag, CancellationToken.None);
                }
            }

            if (wavWriter is not null)
            {
                wavWriter.Dispose();
                if (currentWavPath is not null)
                {
                    await UploadIfEnabledAsync(currentWavPath, hwidTag, CancellationToken.None);
                }
            }
            if (handshake is not null)
            {
                _logger.LogInformation(
                    "Stream closed stream_id={StreamId} hwid={Hwid} frames={Frames} bytes={Bytes} pcm_bytes={PcmBytes}",
                    handshake.Value.StreamId,
                    hwidTag ?? "-",
                    totalFrames,
                    totalBytes,
                    totalPcmBytes
                );
            }
        }
    }

    private static async Task<WsMessage?> ReceiveFullMessageAsync(
        WebSocket ws,
        byte[] buffer,
        CancellationToken ct
    )
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult? result = null;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new WsMessage(result.MessageType, Array.Empty<byte>());
            }

            if (result.Count > 0)
            {
                ms.Write(buffer, 0, result.Count);
            }
        } while (!result.EndOfMessage);

        return new WsMessage(result.MessageType, ms.ToArray());
    }

    private static bool TryParseHandshake(byte[] data, out Handshake handshake)
    {
        handshake = default;
        if (data.Length < HandshakeLength)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        if (magic != HandshakeMagic)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        var headerLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
        if (version != 1 || headerLen != HandshakeLength)
        {
            return false;
        }

        handshake = new Handshake
        {
            StreamId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)),
            SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)),
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16, 2)),
            Codec = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18, 2)),
            FrameSamples = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(20, 2)),
            TimestampMs = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24, 8)),
        };

        return true;
    }

    private static bool TryParseAudioFrame(byte[] data, out AudioFrame frame)
    {
        frame = default;
        if (data.Length < FrameHeaderLength)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        if (magic != AdpcmFrameMagic && magic != PcmFrameMagic)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (length != (uint)(data.Length - FrameHeaderLength))
        {
            return false;
        }

        frame = new AudioFrame(seq, magic, data);
        return true;
    }

    private readonly record struct WsMessage(WebSocketMessageType MessageType, byte[] Payload);

    private readonly record struct Handshake(
        uint StreamId,
        uint SampleRate,
        ushort Channels,
        ushort Codec,
        ushort FrameSamples,
        ulong TimestampMs
    );

    private readonly record struct AudioFrame(uint Seq, uint Magic, byte[] Raw)
    {
        public ReadOnlySpan<byte> Payload =>
            Raw.AsSpan(FrameHeaderLength, Raw.Length - FrameHeaderLength);
    }

    private static bool IsFrameCodecMatch(ushort codec, uint magic)
    {
        return (codec == 0 && magic == PcmFrameMagic) || (codec == 1 && magic == AdpcmFrameMagic);
    }

    private static bool ValidateFramePayload(Handshake hs, AudioFrame frame)
    {
        if (hs.Codec == 0)
        {
            int expected = hs.FrameSamples * hs.Channels * 2;
            return frame.Payload.Length == expected;
        }

        if (frame.Payload.Length < 4)
        {
            return false;
        }

        int expectedMin = 4;
        int expectedMax = expectedMin + (hs.FrameSamples * hs.Channels / 2) + 16;
        return frame.Payload.Length >= expectedMin && frame.Payload.Length <= expectedMax;
    }

    private async Task UploadIfEnabledAsync(string path, string? hwid, CancellationToken ct)
    {
        if (!_uploader.Enabled)
        {
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".bin" && !_s3Options.UploadBin)
        {
            return;
        }

        if (ext == ".wav" && !_s3Options.UploadWav)
        {
            return;
        }

        var prefix = string.IsNullOrWhiteSpace(_s3Options.Prefix)
            ? "received"
            : _s3Options.Prefix.Trim().Trim('/');
        var folder = hwid is null ? prefix : $"{prefix}/{hwid}";
        var key = $"{folder}/{Path.GetFileName(path)}";
        await _uploader.UploadIfEnabledAsync(path, key, ct);
    }

    private static string? SanitizeTag(string? value)
    {
        // Keep filenames safe and predictable.
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[value.Length];
        int len = 0;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                buffer[len++] = ch;
            }
            else if (ch == ':' || ch == '.')
            {
                buffer[len++] = '_';
            }
        }

        return len == 0 ? null : new string(buffer.Slice(0, len));
    }

    private static bool TryDecodeImaAdpcm(ReadOnlySpan<byte> adpcm, out byte[] pcm)
    {
        pcm = Array.Empty<byte>();
        if (adpcm.Length < 4)
        {
            return false;
        }

        int predictor = BinaryPrimitives.ReadInt16LittleEndian(adpcm.Slice(0, 2));
        int index = adpcm[2];
        if (index < 0 || index > 88)
        {
            return false;
        }

        int sampleCount = (adpcm.Length - 4) * 2;
        pcm = new byte[sampleCount * 2];
        int pcmOffset = 0;

        int step = StepTable[index];
        for (int i = 4; i < adpcm.Length; i++)
        {
            byte b = adpcm[i];
            DecodeNibble(b & 0x0F, ref predictor, ref index, ref step, pcm, ref pcmOffset);
            DecodeNibble((b >> 4) & 0x0F, ref predictor, ref index, ref step, pcm, ref pcmOffset);
        }

        return true;
    }

    private static void DecodeNibble(
        int nibble,
        ref int predictor,
        ref int index,
        ref int step,
        byte[] pcm,
        ref int pcmOffset
    )
    {
        int diff = step >> 3;
        if ((nibble & 1) != 0)
        {
            diff += step >> 2;
        }

        if ((nibble & 2) != 0)
        {
            diff += step >> 1;
        }

        if ((nibble & 4) != 0)
        {
            diff += step;
        }

        if ((nibble & 8) != 0)
        {
            diff = -diff;
        }

        predictor += diff;
        if (predictor > short.MaxValue)
        {
            predictor = short.MaxValue;
        }
        else if (predictor < short.MinValue)
        {
            predictor = short.MinValue;
        }

        index += IndexTable[nibble];
        if (index < 0)
        {
            index = 0;
        }
        else if (index > 88)
        {
            index = 88;
        }

        step = StepTable[index];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(pcmOffset, 2), (short)predictor);
        pcmOffset += 2;
    }

    private static readonly int[] IndexTable =
    {
        -1,
        -1,
        -1,
        -1,
        2,
        4,
        6,
        8,
        -1,
        -1,
        -1,
        -1,
        2,
        4,
        6,
        8
    };

    private static readonly int[] StepTable =
    {
        7,
        8,
        9,
        10,
        11,
        12,
        13,
        14,
        16,
        17,
        19,
        21,
        23,
        25,
        28,
        31,
        34,
        37,
        41,
        45,
        50,
        55,
        60,
        66,
        73,
        80,
        88,
        97,
        107,
        118,
        130,
        143,
        157,
        173,
        190,
        209,
        230,
        253,
        279,
        307,
        337,
        371,
        408,
        449,
        494,
        544,
        598,
        658,
        724,
        796,
        876,
        963,
        1060,
        1166,
        1282,
        1411,
        1552,
        1707,
        1878,
        2066,
        2272,
        2499,
        2749,
        3024,
        3327,
        3660,
        4026,
        4428,
        4871,
        5358,
        5894,
        6484,
        7132,
        7845,
        8630,
        9493,
        10442,
        11487,
        12635,
        13899,
        15289,
        16818,
        18500,
        20350,
        22385,
        24623,
        27086,
        29794,
        32767
    };

    private sealed class WavWriter : IDisposable
    {
        private readonly FileStream _stream;
        private long _dataBytes;

        public WavWriter(string path, int sampleRate, short channels, short bitsPerSample)
        {
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            WriteHeader(sampleRate, channels, bitsPerSample);
        }

        public async Task WriteAsync(byte[] pcm, CancellationToken ct)
        {
            if (pcm.Length == 0)
            {
                return;
            }

            await _stream.WriteAsync(pcm, ct);
            _dataBytes += pcm.Length;
        }

        public void Dispose()
        {
            try
            {
                _stream.Flush();
                _stream.Seek(4, SeekOrigin.Begin);
                WriteUInt32((uint)(36 + _dataBytes));
                _stream.Seek(40, SeekOrigin.Begin);
                WriteUInt32((uint)_dataBytes);
                _stream.Flush();
            }
            finally
            {
                _stream.Dispose();
            }
        }

        private void WriteHeader(int sampleRate, short channels, short bitsPerSample)
        {
            int byteRate = sampleRate * channels * (bitsPerSample / 8);
            short blockAlign = (short)(channels * (bitsPerSample / 8));

            WriteBytes("RIFF");
            WriteUInt32(0);
            WriteBytes("WAVE");

            WriteBytes("fmt ");
            WriteUInt32(16);
            WriteUInt16(1);
            WriteUInt16((ushort)channels);
            WriteUInt32((uint)sampleRate);
            WriteUInt32((uint)byteRate);
            WriteUInt16((ushort)blockAlign);
            WriteUInt16((ushort)bitsPerSample);

            WriteBytes("data");
            WriteUInt32(0);
        }

        private void WriteBytes(string value)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            _stream.Write(bytes, 0, bytes.Length);
        }

        private void WriteUInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            _stream.Write(buffer);
        }

        private void WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            _stream.Write(buffer);
        }
    }
}
