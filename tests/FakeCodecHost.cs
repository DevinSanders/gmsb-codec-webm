using System;
using System.Collections.Generic;
using System.IO;
using Concentus;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace WebmCodecPlugin.Tests;

/// <summary>
/// Test doubles that stand in for the host's plugin environment so the
/// codec-borrow handshake can be exercised without the real codec.opus
/// plugin (or a real host) in the process.
///
/// <para><see cref="FakeCodecRegistry"/> maps <c>.opus</c> to a
/// <see cref="StubOpusCodecPlugin"/> whose <c>CreateDecoder</c> returns a
/// genuine Concentus-backed <see cref="IAudioFrameDecoder"/> — the same
/// decode path the real plugin would supply, so a passing decode proves the
/// demuxer handed over valid Opus packets.</para>
/// </summary>
internal sealed class FakePluginContext : IPluginContext
{
    public FakePluginContext(IAudioCodecRegistry? registry) => CodecRegistry = registry;
    public IWindowService? WindowService => null;
    public string PluginDataPath => Path.GetTempPath();
    public IAudioCodecRegistry? CodecRegistry { get; }
    public ISidechainRegistry? Sidechain => null;
}

internal sealed class FakeCodecRegistry : IAudioCodecRegistry
{
    private readonly List<IAudioCodecPlugin> _codecs;
    public FakeCodecRegistry(params IAudioCodecPlugin[] codecs) => _codecs = new(codecs);

    public IEnumerable<IAudioCodecPlugin> All => _codecs;

    public IAudioCodecPlugin? GetByExtension(string extension)
    {
        foreach (var c in _codecs)
            foreach (var p in c.SupportedPatterns)
                if (string.Equals(p, extension, StringComparison.OrdinalIgnoreCase))
                    return c;
        return null;
    }

    public IAudioCodecPlugin? GetByContentType(string mimeType)
    {
        foreach (var c in _codecs)
            foreach (var m in c.SupportedContentTypes)
                if (string.Equals(m, mimeType, StringComparison.OrdinalIgnoreCase))
                    return c;
        return null;
    }
}

/// <summary>Minimal codec plugin that exposes only what the borrow needs:
/// a <c>.opus</c> pattern and a working <see cref="CreateDecoder"/>. Pass
/// <paramref name="exposeDecoder"/>=false to simulate an out-of-date
/// codec.opus that doesn't implement the frame decoder.</summary>
internal sealed class StubOpusCodecPlugin : IAudioCodecPlugin
{
    private readonly bool _exposeDecoder;
    public StubOpusCodecPlugin(bool exposeDecoder = true) => _exposeDecoder = exposeDecoder;

    public string Id => "codec.opus";
    public string Name => "Stub Opus";
    public string Version => "test";
    public string Author => "test";
    public string Description => "test stub";

    public IEnumerable<string> SupportedPatterns => new[] { ".opus" };
    public IEnumerable<string> SupportedContentTypes => new[] { "audio/opus" };

    public void Initialize(IPluginContext context) { }
    public void Shutdown() { }

    public WaveStream CreateStream(string source) => throw new NotSupportedException();

    public IAudioFrameDecoder? CreateDecoder() => _exposeDecoder ? new StubOpusFrameDecoder() : null;
}

/// <summary>Concentus-backed frame decoder mirroring the real
/// <c>OpusFrameDecoder</c>: 48 kHz stereo IEEE-float output, mono upmixed
/// by the decoder.</summary>
internal sealed class StubOpusFrameDecoder : IAudioFrameDecoder
{
    private const int MaxFrameSamplesPerChannel = 5760;   // 120 ms @ 48 kHz
    private readonly IOpusDecoder _decoder;
    private readonly short[] _shortBuffer;

    public int Channels => 2;
    public int SampleRate => 48000;
    public int MaxFrameSamples => MaxFrameSamplesPerChannel;

    public StubOpusFrameDecoder()
    {
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        _shortBuffer = new short[MaxFrameSamplesPerChannel * Channels];
    }

    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        int samples = _decoder.Decode(packet, _shortBuffer, MaxFrameSamplesPerChannel, decode_fec: false);
        int total = samples * Channels;
        for (int i = 0; i < total; i++) pcm[i] = _shortBuffer[i] / 32768f;
        return samples;
    }

    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
