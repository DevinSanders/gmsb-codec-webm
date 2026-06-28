using System;
using System.Collections.Generic;
using System.IO;
using NEbml.Core;

namespace WebmCodecPlugin;

/// <summary>
/// A minimal Matroska / EBML demuxer specialised for the audio path that
/// <c>codec.webm</c> needs: it locates the first <c>A_OPUS</c> track and
/// yields that track's Opus packets in presentation order, plus the
/// metadata (<see cref="TotalTime"/>, <see cref="CurrentTime"/>,
/// <see cref="CanSeek"/>) the host's scrub/loop UX depends on.
///
/// <para><b>What NEbml gives us, and what's ours.</b>
/// <see cref="EbmlReader"/> handles the low-level EBML framing — VINT
/// length encoding, element-ID parsing, and master-element nesting via
/// <see cref="EbmlReader.EnterContainer"/> / <see cref="EbmlReader.LeaveContainer"/>.
/// Everything Matroska-specific — knowing that <c>0x1F43B675</c> is a
/// Cluster, that a SimpleBlock's payload is <c>[track VINT][int16
/// timecode][flags][lacing?][frames…]</c>, that Cues map presentation
/// time to a Segment-relative byte offset — is implemented here.</para>
///
/// <para><b>Reader model.</b> The header (EBML / Info / Tracks / Cues) is
/// parsed once up front from a throwaway reader. Cluster playback uses a
/// separate <see cref="_clusterReader"/> positioned at a Cluster boundary
/// and reading Clusters as top-level siblings — the bytes of a Cluster
/// element are identical whether it's read as a child of Segment or as a
/// root element, so the same walk serves both first-play and post-seek.
/// Seeking rebuilds <see cref="_clusterReader"/> at the target Cluster's
/// byte offset. Every reader wraps the shared input in a
/// <see cref="NonClosingStream"/> so the input's lifetime stays with the
/// owning <see cref="WebmOpusWaveStream"/>, not with any reader.</para>
///
/// <para>Single responsibility: turn a WebM byte stream into a sequence of
/// Opus packets. It does not decode — that's the borrowed
/// <c>codec.opus</c> frame decoder's job.</para>
/// </summary>
internal sealed class MatroskaDemuxer
{
    // ── Matroska element IDs (encoded VINT form — what EbmlReader.ElementId.EncodedValue returns) ──
    private const ulong IdEbml               = 0x1A45DFA3;
    private const ulong IdDocType            = 0x4282;
    private const ulong IdSegment            = 0x18538067;
    private const ulong IdSeekHead           = 0x114D9B74;
    private const ulong IdSeek               = 0x4DBB;
    private const ulong IdSeekId             = 0x53AB;
    private const ulong IdSeekPosition       = 0x53AC;
    private const ulong IdInfo               = 0x1549A966;
    private const ulong IdTimecodeScale      = 0x2AD7B1;
    private const ulong IdDuration           = 0x4489;
    private const ulong IdTracks             = 0x1654AE6B;
    private const ulong IdTrackEntry         = 0xAE;
    private const ulong IdTrackNumber        = 0xD7;
    private const ulong IdTrackType          = 0x83;
    private const ulong IdCodecId            = 0x86;
    private const ulong IdCodecPrivate       = 0x63A2;
    private const ulong IdAudio              = 0xE1;
    private const ulong IdChannels           = 0x9F;
    private const ulong IdCluster            = 0x1F43B675;
    private const ulong IdTimecode           = 0xE7;   // cluster timecode
    private const ulong IdSimpleBlock        = 0xA3;
    private const ulong IdBlockGroup         = 0xA0;
    private const ulong IdBlock              = 0xA1;
    private const ulong IdCues               = 0x1C53BB6B;
    private const ulong IdCuePoint           = 0xBB;
    private const ulong IdCueTime            = 0xB3;
    private const ulong IdCueTrackPositions  = 0xB7;
    private const ulong IdCueTrack           = 0xF7;
    private const ulong IdCueClusterPosition = 0xF1;

    private const long TrackTypeAudio = 2;

    private readonly Stream _stream;          // shared input; owned by the WaveStream, never closed here.
    private readonly bool _streamSeekable;

    // Header-derived metadata.
    private long _opusTrackNumber = -1;
    private double _timecodeScaleNs = 1_000_000.0;   // Matroska default = 1 ms
    private double _secondsPerTimecode = 1_000_000.0 / 1e9;
    private TimeSpan _totalTime = TimeSpan.Zero;
    private long _segmentDataStart = -1;             // absolute offset of the Segment's first child (Cues/SeekHead positions are relative to this)
    private long _firstClusterFilePos = -1;          // absolute offset of the first Cluster element

    // (presentation time, absolute byte offset of the Cluster), sorted by time.
    private readonly List<(double Time, long ClusterPos)> _cues = new();

    // ── Cluster-playback state ──
    private EbmlReader? _clusterReader;
    private bool _inCluster;
    private ulong _clusterTimecode;
    private double _currentTimeSeconds;

    // Current block's decoded frame table (offsets into _blockBuf). A block
    // is normally one Opus frame, but Matroska lacing can pack several.
    private byte[] _blockBuf = new byte[8192];
    private readonly byte[] _headerPeek = new byte[16];  // enough for track VINT (≤8) + int16 tc + flags + lace count
    private int[] _frameOffsets = new int[8];
    private int[] _frameLengths = new int[8];
    private int _frameCount;
    private int _frameIndex;
    private long _blockTimecode;

    public MatroskaDemuxer(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _streamSeekable = stream.CanSeek;

        ParseHeader();

        if (_opusTrackNumber < 0)
            throw new NotSupportedException(
                "This WebM file has no A_OPUS audio track. codec.webm v1 decodes Opus-in-Matroska only " +
                "(Vorbis-in-WebM is a future addition). If the file is video-only, there is no audio to play.");

        // Position the playback reader at the first Cluster so the first
        // Read() works without needing a seek. If the file has no clusters
        // at all, TryReadPacket simply reports EOF.
        if (_firstClusterFilePos >= 0)
            _clusterReader = NewReaderAt(_firstClusterFilePos);
    }

    /// <summary>Channel count declared by the track (Audio &gt; Channels, or
    /// the OpusHead in CodecPrivate). Informational only — the borrowed
    /// decoder always emits stereo and upmixes mono itself, so the
    /// <see cref="WebmOpusWaveStream"/> takes its channel count from the
    /// decoder, not from here.</summary>
    public int Channels { get; private set; } = 2;

    /// <summary>Total duration from <c>Info\Duration</c>, or
    /// <see cref="TimeSpan.Zero"/> when the file omits it (common for
    /// live-muxed recordings). Drives the host's scrub length.</summary>
    public TimeSpan TotalTime => _totalTime;

    /// <summary>Presentation time of the most recently yielded packet.</summary>
    public TimeSpan CurrentTime => TimeSpan.FromSeconds(_currentTimeSeconds);

    /// <summary>Seekable only when the input is seekable AND the file
    /// carries a Cues index. Without Cues we'd have to linear-scan, which
    /// the host's scrub contract doesn't want — it shows the live-stream
    /// UI instead.</summary>
    public bool CanSeek => _streamSeekable && _cues.Count > 0;

    /// <summary>Yields the next Opus packet for the selected track, as a
    /// span into an internal buffer valid until the next call. Returns
    /// <c>false</c> at end of stream. Runs on the audio thread — no
    /// allocations on the steady-state path.</summary>
    public bool TryReadPacket(out ReadOnlySpan<byte> packet)
    {
        while (true)
        {
            // 1. Drain frames already parsed from the current block.
            while (_frameIndex < _frameCount)
            {
                int off = _frameOffsets[_frameIndex];
                int len = _frameLengths[_frameIndex];
                _frameIndex++;
                if (len <= 0) continue;   // skip empty laced frames; Opus has none in practice
                _currentTimeSeconds = _blockTimecode * _secondsPerTimecode;
                packet = new ReadOnlySpan<byte>(_blockBuf, off, len);
                return true;
            }

            if (_clusterReader == null)
            {
                packet = default;
                return false;
            }

            // 2. Make sure we're inside a Cluster.
            if (!_inCluster)
            {
                if (!AdvanceToNextCluster())
                {
                    packet = default;
                    return false;
                }
            }

            // 3. Pull the next block (for our track) out of this Cluster.
            if (!ReadNextBlockInCluster())
            {
                // Cluster exhausted — pop back out and look for the next one.
                _clusterReader.LeaveContainer();
                _inCluster = false;
                continue;
            }
            // ReadNextBlockInCluster set up _frameCount ≥ 1; loop to drain.
        }
    }

    /// <summary>Reposition to the Cluster at or before <paramref name="target"/>
    /// using the Cues index. No-op when <see cref="CanSeek"/> is false.</summary>
    public void SeekTo(TimeSpan target)
    {
        if (!CanSeek) return;

        double t = target.TotalSeconds;
        if (t < 0) t = 0;

        long clusterPos = _cues[0].ClusterPos;
        double clusterTime = _cues[0].Time;
        foreach (var cue in _cues)
        {
            if (cue.Time <= t) { clusterPos = cue.ClusterPos; clusterTime = cue.Time; }
            else break;
        }

        _clusterReader = NewReaderAt(clusterPos);
        _inCluster = false;
        _frameCount = 0;
        _frameIndex = 0;
        _clusterTimecode = 0;
        // Best estimate until the first post-seek block refines it. Opus's
        // decoder carries some cross-packet state, so the very first frames
        // after a seek may glitch briefly before settling — acceptable, and
        // matches how the sibling Ogg-Opus path behaves on scrub.
        _currentTimeSeconds = clusterTime;
    }

    // ── Header parsing ────────────────────────────────────────────────

    private void ParseHeader()
    {
        if (_streamSeekable) _stream.Position = 0;
        var r = NewReader();

        // EBML header — verify this is actually WebM/Matroska.
        if (!r.ReadNext() || r.ElementId.EncodedValue != IdEbml)
            throw new InvalidDataException("Not an EBML stream: missing the \\EBML header element.");
        VerifyDocType(r);

        // Segment — the one master element that holds everything else.
        if (!r.ReadNext() || r.ElementId.EncodedValue != IdSegment)
            throw new InvalidDataException("Malformed WebM: expected a \\Segment element after the EBML header.");

        long cuesAbsFromSeekHead = -1;
        r.EnterContainer();
        bool first = true;
        while (r.ReadNext())
        {
            if (first)
            {
                // The first child's start IS the Segment data start, which is
                // the origin for all Segment-relative positions (Cues, SeekHead).
                _segmentDataStart = r.ElementPosition;
                first = false;
            }

            ulong id = r.ElementId.EncodedValue;
            if (id == IdSeekHead)
            {
                long p = ScanSeekHeadForCues(r);
                if (p >= 0) cuesAbsFromSeekHead = p;
            }
            else if (id == IdInfo) ParseInfo(r);
            else if (id == IdTracks) ParseTracks(r);
            else if (id == IdCues) ParseCues(r);
            else if (id == IdCluster)
            {
                // First Cluster marks where the audio payload starts (Info and
                // Tracks always precede it). Record it once.
                if (_firstClusterFilePos < 0) _firstClusterFilePos = r.ElementPosition;

                // Stop the header walk early only if the seek index is already
                // in hand: either inline Cues parsed before the clusters, or a
                // SeekHead pointer we can follow (the common ffmpeg layout —
                // SeekHead up top, Cues at the very end). Otherwise keep
                // scanning past the clusters (their data is skipped, not read)
                // to catch trailing inline Cues from muxers that emit no
                // SeekHead.
                if (_cues.Count > 0 || cuesAbsFromSeekHead >= 0) break;
            }
            // anything else (Tags, Chapters, Attachments, Void…) is skipped
        }

        // Cues most often live at the end of the file, pointed to by the
        // SeekHead. If we didn't find them inline, follow that pointer now.
        if (_cues.Count == 0 && cuesAbsFromSeekHead >= 0 && _streamSeekable)
            ParseCuesAt(cuesAbsFromSeekHead);
    }

    private static void VerifyDocType(EbmlReader r)
    {
        r.EnterContainer();
        string? docType = null;
        while (r.ReadNext())
        {
            if (r.ElementId.EncodedValue == IdDocType)
                docType = r.ReadAscii();
        }
        r.LeaveContainer();

        if (docType != null &&
            !docType.Equals("webm", StringComparison.OrdinalIgnoreCase) &&
            !docType.Equals("matroska", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Unsupported EBML DocType '{docType}'. codec.webm handles 'webm' and 'matroska' containers only.");
        }
    }

    private void ParseInfo(EbmlReader r)
    {
        r.EnterContainer();
        double durationRaw = 0;
        bool hasDuration = false;
        while (r.ReadNext())
        {
            ulong id = r.ElementId.EncodedValue;
            if (id == IdTimecodeScale) _timecodeScaleNs = r.ReadUInt();
            else if (id == IdDuration) { durationRaw = r.ReadFloat(); hasDuration = true; }
        }
        r.LeaveContainer();

        _secondsPerTimecode = _timecodeScaleNs / 1e9;
        if (hasDuration)
            _totalTime = TimeSpan.FromSeconds(durationRaw * _secondsPerTimecode);
    }

    private void ParseTracks(EbmlReader r)
    {
        r.EnterContainer();
        while (r.ReadNext())
        {
            if (r.ElementId.EncodedValue != IdTrackEntry) continue;

            r.EnterContainer();
            long number = -1;
            long type = -1;
            string? codecId = null;
            int channels = 0;
            while (r.ReadNext())
            {
                ulong id = r.ElementId.EncodedValue;
                if (id == IdTrackNumber) number = (long)r.ReadUInt();
                else if (id == IdTrackType) type = (long)r.ReadUInt();
                else if (id == IdCodecId) codecId = r.ReadAscii();
                else if (id == IdAudio)
                {
                    r.EnterContainer();
                    while (r.ReadNext())
                        if (r.ElementId.EncodedValue == IdChannels) channels = (int)r.ReadUInt();
                    r.LeaveContainer();
                }
            }
            r.LeaveContainer();

            // First Opus audio track wins. A_OPUS is the exact CodecID; match
            // with StartsWith to tolerate any trailing variant.
            if (_opusTrackNumber < 0 &&
                number >= 0 &&
                (type == TrackTypeAudio || type < 0) &&
                codecId != null &&
                codecId.StartsWith("A_OPUS", StringComparison.Ordinal))
            {
                _opusTrackNumber = number;
                if (channels > 0) Channels = channels;
            }
        }
        r.LeaveContainer();
    }

    /// <summary>Scan a SeekHead for the entry pointing at Cues; returns the
    /// absolute file offset of Cues, or -1 if not referenced.</summary>
    private long ScanSeekHeadForCues(EbmlReader r)
    {
        long cuesAbs = -1;
        r.EnterContainer();
        while (r.ReadNext())
        {
            if (r.ElementId.EncodedValue != IdSeek) continue;

            r.EnterContainer();
            ulong seekId = 0;
            long seekPos = -1;
            bool haveId = false;
            while (r.ReadNext())
            {
                ulong id = r.ElementId.EncodedValue;
                if (id == IdSeekId)
                {
                    int n = ReadElementBytes(r, _headerPeek, (int)r.ElementSize);
                    seekId = 0;
                    for (int i = 0; i < n; i++) seekId = (seekId << 8) | _headerPeek[i];
                    haveId = true;
                }
                else if (id == IdSeekPosition) seekPos = (long)r.ReadUInt();
            }
            r.LeaveContainer();

            if (haveId && seekId == IdCues && seekPos >= 0 && _segmentDataStart >= 0)
                cuesAbs = _segmentDataStart + seekPos;
        }
        r.LeaveContainer();
        return cuesAbs;
    }

    private void ParseCuesAt(long absPosition)
    {
        if (!_streamSeekable) return;
        var r = NewReaderAt(absPosition);
        if (r.ReadNext() && r.ElementId.EncodedValue == IdCues)
            ParseCues(r);
    }

    private void ParseCues(EbmlReader r)
    {
        r.EnterContainer();
        while (r.ReadNext())
        {
            if (r.ElementId.EncodedValue != IdCuePoint) continue;

            r.EnterContainer();
            long cueTime = -1;
            long clusterPos = -1;
            while (r.ReadNext())
            {
                ulong id = r.ElementId.EncodedValue;
                if (id == IdCueTime) cueTime = (long)r.ReadUInt();
                else if (id == IdCueTrackPositions)
                {
                    r.EnterContainer();
                    long pos = -1;
                    long track = -1;
                    while (r.ReadNext())
                    {
                        ulong cid = r.ElementId.EncodedValue;
                        if (cid == IdCueClusterPosition) pos = (long)r.ReadUInt();
                        else if (cid == IdCueTrack) track = (long)r.ReadUInt();
                    }
                    r.LeaveContainer();
                    // Prefer the position for our track; accept an
                    // untracked one as a fallback.
                    if (pos >= 0 && (track < 0 || track == _opusTrackNumber))
                        clusterPos = pos;
                }
            }
            r.LeaveContainer();

            if (cueTime >= 0 && clusterPos >= 0 && _segmentDataStart >= 0)
                _cues.Add((cueTime * _secondsPerTimecode, _segmentDataStart + clusterPos));
        }
        r.LeaveContainer();

        _cues.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    // ── Cluster / block walking ───────────────────────────────────────

    private bool AdvanceToNextCluster()
    {
        var r = _clusterReader!;
        while (r.ReadNext())
        {
            if (r.ElementId.EncodedValue == IdCluster)
            {
                r.EnterContainer();
                _inCluster = true;
                _clusterTimecode = 0;
                return true;
            }
            // Trailing non-Cluster top-level elements (Cues, Tags, …) — skip;
            // the next ReadNext steps over their data automatically.
        }
        return false;
    }

    /// <summary>Reads forward within the current Cluster until it finds a
    /// block belonging to the selected Opus track, populating the frame
    /// table. Returns <c>false</c> when the Cluster's children are
    /// exhausted.</summary>
    private bool ReadNextBlockInCluster()
    {
        var r = _clusterReader!;
        while (r.ReadNext())
        {
            ulong id = r.ElementId.EncodedValue;
            if (id == IdTimecode)
            {
                _clusterTimecode = r.ReadUInt();
            }
            else if (id == IdSimpleBlock)
            {
                if (TryParseBlock(r)) return true;
            }
            else if (id == IdBlockGroup)
            {
                r.EnterContainer();
                bool got = false;
                while (r.ReadNext())
                {
                    if (r.ElementId.EncodedValue == IdBlock)
                        got = TryParseBlock(r);
                }
                r.LeaveContainer();
                if (got) return true;
            }
            // BlockDuration, ReferenceBlock, etc. — ignore.
        }
        return false;
    }

    /// <summary>Parse a (Simple)Block's payload into the frame table. Peeks
    /// the block header first and bails cheaply (without buffering the whole
    /// payload) when the block is for a different track — important for
    /// <c>video/webm</c> files where video keyframes are large.</summary>
    private bool TryParseBlock(EbmlReader r)
    {
        int size = (int)r.ElementSize;
        if (size <= 0) return false;

        // Peek just the header region to read the track number.
        int peek = ReadElementBytes(r, _headerPeek, Math.Min(_headerPeek.Length, size));
        if (peek < 4) return false;   // need track VINT + int16 timecode + flags at minimum

        ulong track = ReadVInt(_headerPeek, 0, out int trackLen);
        if (trackLen <= 0 || (long)track != _opusTrackNumber)
            return false;             // not our track — leave the rest unread; next ReadNext skips it

        // Our track: assemble the full payload (header bytes already read + the rest).
        EnsureBlockBuf(size);
        Array.Copy(_headerPeek, 0, _blockBuf, 0, peek);
        if (peek < size)
        {
            int got = ReadElementBytes(r, _blockBuf, size - peek, peek);
            if (peek + got < size) size = peek + got;   // short read — clamp to what we actually have
        }

        return ParseFrameTable(size, trackLen);
    }

    /// <summary>Decode the block framing (timecode, flags, optional lacing)
    /// in <see cref="_blockBuf"/> into <see cref="_frameOffsets"/> /
    /// <see cref="_frameLengths"/>.</summary>
    private bool ParseFrameTable(int size, int trackLen)
    {
        int p = trackLen;
        if (p + 3 > size) return false;

        short relTime = (short)((_blockBuf[p] << 8) | _blockBuf[p + 1]);
        p += 2;
        byte flags = _blockBuf[p];
        p += 1;

        _blockTimecode = (long)_clusterTimecode + relTime;
        _frameIndex = 0;

        int lacing = flags & 0x06;
        if (lacing == 0x00)
        {
            // No lacing — the whole remainder is one frame.
            EnsureFrameCapacity(1);
            _frameOffsets[0] = p;
            _frameLengths[0] = size - p;
            _frameCount = 1;
            return true;
        }

        if (p >= size) return false;
        int laceCount = _blockBuf[p] + 1;
        p += 1;
        EnsureFrameCapacity(laceCount);

        switch (lacing)
        {
            case 0x04: // fixed-size lacing — equal frames
            {
                int total = size - p;
                int each = laceCount > 0 ? total / laceCount : 0;
                for (int i = 0; i < laceCount; i++)
                {
                    _frameOffsets[i] = p + i * each;
                    _frameLengths[i] = each;
                }
                break;
            }
            case 0x02: // Xiph lacing — sizes coded as runs of 0xFF + remainder
            {
                int off = p;
                long sumKnown = 0;
                for (int i = 0; i < laceCount - 1; i++)
                {
                    int s = 0;
                    while (p < size && _blockBuf[p] == 0xFF) { s += 0xFF; p++; }
                    if (p < size) { s += _blockBuf[p]; p++; }
                    _frameLengths[i] = s;
                    sumKnown += s;
                }
                off = p;
                for (int i = 0; i < laceCount - 1; i++) { _frameOffsets[i] = off; off += _frameLengths[i]; }
                _frameOffsets[laceCount - 1] = off;
                _frameLengths[laceCount - 1] = size - off;
                break;
            }
            default: // 0x06 EBML lacing — first size is a VINT, rest are signed deltas
            {
                long sz = (long)ReadVInt(_blockBuf, p, out int l0);
                p += l0;
                _frameLengths[0] = (int)sz;
                for (int i = 1; i < laceCount - 1; i++)
                {
                    long delta = ReadSignedVInt(_blockBuf, p, out int li);
                    p += li;
                    sz += delta;
                    _frameLengths[i] = (int)sz;
                }
                int off = p;
                for (int i = 0; i < laceCount - 1; i++) { _frameOffsets[i] = off; off += _frameLengths[i]; }
                _frameOffsets[laceCount - 1] = off;
                _frameLengths[laceCount - 1] = size - off;
                break;
            }
        }

        _frameCount = laceCount;
        return true;
    }

    // ── Low-level helpers ─────────────────────────────────────────────

    /// <summary>Read an unsigned Matroska VINT from a buffer, stripping the
    /// length-marker bit. <paramref name="length"/> receives the encoded
    /// byte count, or 0 on a malformed descriptor.</summary>
    private static ulong ReadVInt(byte[] b, int pos, out int length)
    {
        if (pos >= b.Length) { length = 0; return 0; }
        byte first = b[pos];
        int mask = 0x80;
        length = 1;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }
        if (length > 8 || pos + length > b.Length) { length = 0; return 0; }

        ulong value = (ulong)(first & (mask - 1));
        for (int i = 1; i < length; i++)
            value = (value << 8) | b[pos + i];
        return value;
    }

    /// <summary>Read a signed Matroska VINT (used by EBML-lacing size
    /// deltas): the unsigned value biased by <c>2^(7n-1) − 1</c>.</summary>
    private static long ReadSignedVInt(byte[] b, int pos, out int length)
    {
        ulong raw = ReadVInt(b, pos, out length);
        if (length <= 0) return 0;
        long bias = (1L << (7 * length - 1)) - 1;
        return (long)raw - bias;
    }

    /// <summary>Read up to <paramref name="count"/> bytes of the current
    /// element's data into <paramref name="buffer"/> at
    /// <paramref name="offset"/>, looping until satisfied or the element
    /// ends. Returns the number of bytes actually read.</summary>
    private static int ReadElementBytes(EbmlReader r, byte[] buffer, int count, int offset = 0)
    {
        int total = 0;
        while (total < count)
        {
            int n = r.ReadBinary(buffer, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private void EnsureBlockBuf(int size)
    {
        if (_blockBuf.Length < size)
        {
            int cap = _blockBuf.Length;
            while (cap < size) cap *= 2;
            var grown = new byte[cap];
            // No need to preserve contents — callers always copy the header
            // peek back in after growing.
            _blockBuf = grown;
        }
    }

    private void EnsureFrameCapacity(int count)
    {
        if (_frameOffsets.Length < count)
        {
            _frameOffsets = new int[count];
            _frameLengths = new int[count];
        }
    }

    private EbmlReader NewReader() => new EbmlReader(new NonClosingStream(_stream));

    private EbmlReader NewReaderAt(long absolutePosition)
    {
        _stream.Position = absolutePosition;
        return NewReader();
    }

    /// <summary>Stream wrapper that forwards everything to the shared input
    /// but ignores Dispose/Close, so an <see cref="EbmlReader"/> disposing
    /// its source never closes the file the owning WaveStream is
    /// responsible for.</summary>
    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;
        public NonClosingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { /* never close the shared input */ }
    }
}
