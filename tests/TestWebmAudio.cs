using System;
using System.Collections.Generic;
using System.IO;
using Concentus;
using Concentus.Enums;

namespace WebmCodecPlugin.Tests;

/// <summary>
/// Builds tiny WebM (Matroska / Opus) clips in memory so the demux tests
/// don't need a committed binary fixture. A 440 Hz sine is encoded with the
/// same Concentus stack the real codec.opus plugin decodes with, then muxed
/// into a hand-written Matroska container here. Keeping the muxer in the
/// test project (rather than depending on a third-party muxer) makes the
/// fixtures deterministic and fully under our control — see
/// <c>tests/fixtures/README.txt</c>.
///
/// <para>The clip carries one A_OPUS track (track number 1), 20 ms
/// SimpleBlocks split across two Clusters, an Info Duration, and an optional
/// Cues index so the seek path can be exercised.</para>
/// </summary>
internal static class TestWebmAudio
{
    public const int SampleRate = 48000;
    private const int FrameSamplesPerChannel = 960;   // 20 ms @ 48 kHz
    private const long TimecodeScaleNs = 1_000_000;    // 1 ms units
    private const int FrameMs = 20;
    private const int OpusTrackNumber = 1;

    // ── Matroska element IDs (encoded form) ──
    private const uint EBML = 0x1A45DFA3;
    private const uint DocType = 0x4282;
    private const uint EBMLVersion = 0x4286;
    private const uint Segment = 0x18538067;
    private const uint Info = 0x1549A966;
    private const uint TimecodeScale = 0x2AD7B1;
    private const uint Duration = 0x4489;
    private const uint Tracks = 0x1654AE6B;
    private const uint TrackEntry = 0xAE;
    private const uint TrackNumber = 0xD7;
    private const uint TrackType = 0x83;
    private const uint CodecID = 0x86;
    private const uint CodecPrivate = 0x63A2;
    private const uint AudioEl = 0xE1;
    private const uint ChannelsEl = 0x9F;
    private const uint Cluster = 0x1F43B675;
    private const uint Timecode = 0xE7;
    private const uint SimpleBlock = 0xA3;
    private const uint Cues = 0x1C53BB6B;
    private const uint CuePoint = 0xBB;
    private const uint CueTime = 0xB3;
    private const uint CueTrackPositions = 0xB7;
    private const uint CueTrack = 0xF7;
    private const uint CueClusterPosition = 0xF1;

    /// <summary>Encode <paramref name="seconds"/> of a sine tone and mux it
    /// into a complete WebM-Opus byte stream.</summary>
    public static byte[] CreateWebmOpusClip(int channels = 2, double seconds = 0.5,
        double frequencyHz = 440.0, bool includeCues = true)
    {
        List<byte[]> packets = EncodeOpusPackets(channels, seconds, frequencyHz, out int frameCount);

        // EBML header.
        byte[] header = Master(EBML,
            Elem(EBMLVersion, UIntBytes(1)),
            Elem(DocType, AsciiBytes("webm")));

        // Segment children, assembled in order so Cluster byte offsets
        // (relative to the Segment data start) can be computed for Cues.
        byte[] info = Master(Info,
            Elem(TimecodeScale, UIntBytes((ulong)TimecodeScaleNs)),
            Elem(Duration, DoubleBytes(frameCount * (double)FrameMs)));

        byte[] tracks = Master(Tracks,
            Master(TrackEntry,
                Elem(TrackNumber, UIntBytes(OpusTrackNumber)),
                Elem(TrackType, UIntBytes(2)),                  // 2 = audio
                Elem(CodecID, AsciiBytes("A_OPUS")),
                Elem(CodecPrivate, OpusHead(channels)),
                Master(AudioEl, Elem(ChannelsEl, UIntBytes((ulong)channels)))));

        // Split frames across two clusters.
        int half = Math.Max(1, frameCount / 2);
        byte[] cluster0 = BuildCluster(0, packets, 0, Math.Min(half, packets.Count));
        byte[] cluster1 = packets.Count > half
            ? BuildCluster(half * FrameMs, packets, half, packets.Count)
            : Array.Empty<byte>();

        // Cluster offsets are relative to the Segment data start (the first
        // Segment child). Order: Info, Tracks, Cluster0, [Cluster1], [Cues].
        long cluster0Pos = info.Length + tracks.Length;
        long cluster1Pos = cluster0Pos + cluster0.Length;

        byte[] cues = Array.Empty<byte>();
        if (includeCues)
        {
            var cuePoints = new List<byte[]>
            {
                BuildCuePoint(0, cluster0Pos),
            };
            if (cluster1.Length > 0)
                cuePoints.Add(BuildCuePoint(half * FrameMs, cluster1Pos));
            cues = Master(Cues, cuePoints.ToArray());
        }

        var segmentChildren = new List<byte[]> { info, tracks, cluster0 };
        if (cluster1.Length > 0) segmentChildren.Add(cluster1);
        if (cues.Length > 0) segmentChildren.Add(cues);

        byte[] segment = Master(Segment, segmentChildren.ToArray());

        using var ms = new MemoryStream();
        ms.Write(header, 0, header.Length);
        ms.Write(segment, 0, segment.Length);
        return ms.ToArray();
    }

    private static List<byte[]> EncodeOpusPackets(int channels, double seconds, double frequencyHz, out int frameCount)
    {
        int totalSamples = (int)(SampleRate * seconds);
        frameCount = totalSamples / FrameSamplesPerChannel;
        if (frameCount < 2) frameCount = 2;   // ensure two clusters' worth

        IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 64000;

        var packets = new List<byte[]>(frameCount);
        var pcm = new short[FrameSamplesPerChannel * channels];
        var packetBuf = new byte[4000];
        double step = 2.0 * Math.PI * frequencyHz / SampleRate;
        long n = 0;

        for (int f = 0; f < frameCount; f++)
        {
            for (int s = 0; s < FrameSamplesPerChannel; s++)
            {
                short v = (short)(0.5 * Math.Sin(step * n++) * short.MaxValue);
                for (int c = 0; c < channels; c++)
                    pcm[s * channels + c] = v;
            }
            int len = encoder.Encode(pcm, FrameSamplesPerChannel, packetBuf, packetBuf.Length);
            var packet = new byte[len];
            Array.Copy(packetBuf, packet, len);
            packets.Add(packet);
        }
        return packets;
    }

    private static byte[] BuildCluster(long clusterTimecodeMs, List<byte[]> packets, int start, int end)
    {
        var children = new List<byte[]> { Elem(Timecode, UIntBytes((ulong)clusterTimecodeMs)) };
        for (int i = start; i < end; i++)
        {
            short rel = (short)((i - start) * FrameMs);
            children.Add(Elem(SimpleBlock, SimpleBlockPayload(OpusTrackNumber, rel, packets[i])));
        }
        return Master(Cluster, children.ToArray());
    }

    private static byte[] BuildCuePoint(long timeMs, long clusterPos) =>
        Master(CuePoint,
            Elem(CueTime, UIntBytes((ulong)timeMs)),
            Master(CueTrackPositions,
                Elem(CueTrack, UIntBytes(OpusTrackNumber)),
                Elem(CueClusterPosition, UIntBytes((ulong)clusterPos))));

    // ── SimpleBlock payload: [track VINT][int16 rel timecode BE][flags][frame] ──
    private static byte[] SimpleBlockPayload(int track, short relTimecode, byte[] frame)
    {
        byte[] trackVint = EncodeVintSize((ulong)track);   // small track numbers encode like sizes
        var buf = new byte[trackVint.Length + 2 + 1 + frame.Length];
        int p = 0;
        Array.Copy(trackVint, 0, buf, p, trackVint.Length); p += trackVint.Length;
        buf[p++] = (byte)((relTimecode >> 8) & 0xFF);
        buf[p++] = (byte)(relTimecode & 0xFF);
        buf[p++] = 0x80;   // keyframe, no lacing
        Array.Copy(frame, 0, buf, p, frame.Length);
        return buf;
    }

    // OpusHead: magic + version + channels + pre-skip + input rate + gain + mapping family.
    private static byte[] OpusHead(int channels)
    {
        var b = new byte[19];
        byte[] magic = System.Text.Encoding.ASCII.GetBytes("OpusHead");
        Array.Copy(magic, b, 8);
        b[8] = 1;                       // version
        b[9] = (byte)channels;          // channel count
        b[10] = 0x38; b[11] = 0x01;     // pre-skip 312 (LE)
        b[12] = 0x80; b[13] = 0xBB; b[14] = 0x00; b[15] = 0x00;   // 48000 (LE)
        b[16] = 0; b[17] = 0;           // output gain
        b[18] = 0;                      // mapping family 0
        return b;
    }

    // ── EBML byte-level encoding helpers ──

    /// <summary>Encode an element: ID bytes + VINT size + content.</summary>
    private static byte[] Elem(uint id, byte[] content)
    {
        byte[] idBytes = IdBytes(id);
        byte[] size = EncodeVintSize((ulong)content.Length);
        var buf = new byte[idBytes.Length + size.Length + content.Length];
        int p = 0;
        Array.Copy(idBytes, 0, buf, p, idBytes.Length); p += idBytes.Length;
        Array.Copy(size, 0, buf, p, size.Length); p += size.Length;
        Array.Copy(content, 0, buf, p, content.Length);
        return buf;
    }

    private static byte[] Master(uint id, params byte[][] children)
    {
        int total = 0;
        foreach (var c in children) total += c.Length;
        var content = new byte[total];
        int p = 0;
        foreach (var c in children) { Array.Copy(c, 0, content, p, c.Length); p += c.Length; }
        return Elem(id, content);
    }

    /// <summary>Element IDs are written in their encoded form (the marker
    /// bit is already part of the constant), big-endian, using the minimal
    /// byte count.</summary>
    private static byte[] IdBytes(uint id)
    {
        if (id <= 0xFF) return new[] { (byte)id };
        if (id <= 0xFFFF) return new[] { (byte)(id >> 8), (byte)id };
        if (id <= 0xFFFFFF) return new[] { (byte)(id >> 16), (byte)(id >> 8), (byte)id };
        return new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };
    }

    /// <summary>Encode a size/track value as a Matroska VINT: smallest length
    /// whose data bits fit the value, with the length-marker bit set.</summary>
    private static byte[] EncodeVintSize(ulong value)
    {
        for (int len = 1; len <= 8; len++)
        {
            ulong max = (1UL << (7 * len)) - 1;   // all-ones is reserved (unknown size)
            if (value < max)
            {
                var buf = new byte[len];
                for (int i = len - 1; i >= 0; i--)
                {
                    buf[i] = (byte)(value & 0xFF);
                    value >>= 8;
                }
                buf[0] |= (byte)(0x80 >> (len - 1));   // length marker
                return buf;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(value), "Value too large for an 8-byte VINT.");
    }

    private static byte[] UIntBytes(ulong value)
    {
        if (value == 0) return new byte[] { 0 };
        int len = 0;
        ulong v = value;
        while (v > 0) { len++; v >>= 8; }
        var buf = new byte[len];
        for (int i = len - 1; i >= 0; i--) { buf[i] = (byte)(value & 0xFF); value >>= 8; }
        return buf;
    }

    private static byte[] DoubleBytes(double value)
    {
        byte[] b = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);   // EBML floats are big-endian
        return b;
    }

    private static byte[] AsciiBytes(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    /// <summary>Root-mean-square of an interleaved buffer — an energy proxy
    /// for lossy round-trip comparisons.</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sumSq = 0;
        for (int i = 0; i < samples.Length; i++) sumSq += (double)samples[i] * samples[i];
        return Math.Sqrt(sumSq / samples.Length);
    }
}
