using System;
using System.IO;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace WebmCodecPlugin;

/// <summary>
/// The <see cref="WaveStream"/> bridge for WebM-Opus. Holds a
/// <see cref="MatroskaDemuxer"/> (the container half) and a borrowed
/// <see cref="IAudioFrameDecoder"/> from the <c>codec.opus</c> plugin (the
/// codec half), and glues them into the host's pull-based audio contract.
///
/// <para>Output is IEEE float at the decoder's native 48 kHz / stereo —
/// the decoder upmixes mono and resamples internally per the
/// <see cref="IAudioFrameDecoder"/> contract, so the
/// <see cref="WaveFormat"/> tracks the decoder, not the source channel
/// count.</para>
///
/// <para>Mirrors the float-to-byte bridge shape of <c>gmsb-codec-opus</c>'s
/// <c>OpusOggWaveStream</c>: <see cref="Read"/> drains a pre-allocated
/// float scratch buffer into the caller's byte buffer with
/// <see cref="Buffer.BlockCopy"/>, decoding one Opus packet at a time when
/// the scratch runs dry. Everything the audio thread touches is allocated
/// up front in the constructor.</para>
///
/// <para><b>Ownership.</b> This stream owns, and disposes, three things:
/// the seekable stream the demuxer reads (a file handle, or an in-memory
/// copy of a handed-in Stream), the caller's original Stream when one was
/// handed in (the SDK transfers ownership on the Stream overload), and the
/// borrowed decoder.</para>
/// </summary>
internal sealed class WebmOpusWaveStream : WaveStream
{
    private readonly Stream _stream;          // seekable input the demuxer reads (owned)
    private readonly Stream? _ownedSource;    // caller's original Stream in Stream mode (owned); null in file mode
    private readonly MatroskaDemuxer _demuxer;
    private readonly IAudioFrameDecoder _decoder;   // borrowed from codec.opus; disposed here

    private readonly float[] _scratch;        // one decoded packet's interleaved float PCM
    private int _pendingOffset;               // float index into _scratch not yet handed out
    private int _pendingLength;               // float count valid in _scratch

    public override WaveFormat WaveFormat { get; }

    /// <summary>File-path mode — opens and owns the file.</summary>
    public WebmOpusWaveStream(string path, IAudioFrameDecoder decoder)
        : this(File.OpenRead(path), ownedSource: null, decoder) { }

    /// <summary>Stream mode — used by transport plugins (e.g.
    /// <c>codec.webstream</c>). Matroska's Cues-based seek needs a seekable
    /// input, so a forward-only Stream is buffered into a seekable
    /// <see cref="MemoryStream"/> up front (the same trade-off the sibling
    /// Ogg-Opus path makes). A stream that is already seekable is used in
    /// place. Either way the caller's Stream is disposed on
    /// <see cref="Dispose(bool)"/>.</summary>
    public static WebmOpusWaveStream FromStream(Stream source, IAudioFrameDecoder decoder)
    {
        if (source.CanSeek)
            return new WebmOpusWaveStream(source, ownedSource: null, decoder);

        var buffered = new MemoryStream();
        source.CopyTo(buffered);
        buffered.Position = 0;
        return new WebmOpusWaveStream(buffered, ownedSource: source, decoder);
    }

    private WebmOpusWaveStream(Stream seekableStream, Stream? ownedSource, IAudioFrameDecoder decoder)
    {
        _stream = seekableStream;
        _ownedSource = ownedSource;
        _decoder = decoder;

        try
        {
            _demuxer = new MatroskaDemuxer(_stream);
        }
        catch
        {
            // Constructor failed after we took ownership of the streams /
            // borrowed decoder — release them so we don't leak a file handle
            // or the borrowed decoder when the host never gets a WaveStream
            // to dispose.
            _decoder.Dispose();
            _stream.Dispose();
            _ownedSource?.Dispose();
            throw;
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_decoder.SampleRate, _decoder.Channels);
        _scratch = new float[_decoder.MaxFrameSamples * _decoder.Channels];
    }

    public override bool CanSeek => _demuxer.CanSeek;

    public override long Length =>
        (long)(_demuxer.TotalTime.TotalSeconds * WaveFormat.AverageBytesPerSecond);

    public override long Position
    {
        get => (long)(_demuxer.CurrentTime.TotalSeconds * WaveFormat.AverageBytesPerSecond);
        set
        {
            if (!_demuxer.CanSeek) return;
            double seconds = value / (double)WaveFormat.AverageBytesPerSecond;
            if (seconds < 0) seconds = 0;
            _demuxer.SeekTo(TimeSpan.FromSeconds(seconds));
            // Discard anything decoded from before the seek.
            _pendingOffset = 0;
            _pendingLength = 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int floatsRequested = count / sizeof(float);
        if (floatsRequested <= 0) return 0;

        int floatsProduced = 0;
        while (floatsProduced < floatsRequested)
        {
            // Drain leftovers from the last decoded packet first.
            if (_pendingOffset < _pendingLength)
            {
                int remaining = floatsRequested - floatsProduced;
                int available = _pendingLength - _pendingOffset;
                int n = Math.Min(remaining, available);

                Buffer.BlockCopy(
                    _scratch, _pendingOffset * sizeof(float),
                    buffer, offset + floatsProduced * sizeof(float),
                    n * sizeof(float));

                _pendingOffset += n;
                floatsProduced += n;
                continue;
            }

            // Scratch empty — decode the next Opus packet.
            if (!_demuxer.TryReadPacket(out ReadOnlySpan<byte> packet))
                break;   // end of stream

            if (packet.Length == 0)
                continue;

            int samplesPerChannel = _decoder.Decode(packet, _scratch);
            _pendingOffset = 0;
            _pendingLength = samplesPerChannel * _decoder.Channels;
        }

        return floatsProduced * sizeof(float);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Borrowed decoder is owned by this WaveStream (see the codec
            // borrow handshake in WebmCodecPlugin). _stream is the file or
            // the in-memory copy; _ownedSource is the caller's original
            // Stream in Stream mode — disposing it honours the SDK ownership
            // contract.
            _decoder.Dispose();
            _stream.Dispose();
            _ownedSource?.Dispose();
        }
        base.Dispose(disposing);
    }
}
