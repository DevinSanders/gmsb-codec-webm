using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using SoundBoard.PluginApi;

namespace WebmCodecPlugin;

/// <summary>
/// <see cref="IAudioCodecPlugin"/> stub for WebM (<c>.webm</c>) playback in
/// Game Master Sound Board. The container is Matroska / EBML — parsed
/// in-process with <a href="https://github.com/oleg-st/NEbml">NEbml</a>
/// — and the Opus packets pulled out of it are decoded by the
/// <c>codec.opus</c> plugin, borrowed at <see cref="CreateStream"/> time
/// through <see cref="IPluginContext.CodecRegistry"/>. This plugin
/// therefore bundles NO Opus decoder of its own.
///
/// <para>The container is demuxed by <see cref="MatroskaDemuxer"/> and the
/// resulting Opus packets are fed to a borrowed
/// <see cref="IAudioFrameDecoder"/> by <see cref="WebmOpusWaveStream"/>.
/// Both <see cref="CreateStream(string)"/> and
/// <see cref="CreateStream(Stream, string)"/> are supported; the latter
/// lets transport plugins (e.g. <c>codec.webstream</c>) hand an open
/// <c>audio/webm</c> stream here for decode.</para>
/// </summary>
public sealed class WebmCodecPlugin : IAudioCodecPlugin
{
    public string Id => "codec.webm";
    public string Name => "WebM Codec";
    public string Description => "Adds .webm playback. Demuxes Matroska in-process and borrows the Opus decoder from codec.opus.";
    public string Version => PluginVersion.OfAssembly(typeof(WebmCodecPlugin));
    public string Author => "Devin Sanders";

    public IEnumerable<string> SupportedPatterns => new[] { ".webm" };

    /// <summary>
    /// MIME types claimed. Only <c>audio/webm</c> — files with
    /// <c>video/webm</c> (Opus audio carried alongside VP8/VP9 video) are
    /// still routable by extension; we'd extract just the audio track,
    /// but claiming <c>video/webm</c> would also pull in pure-video
    /// streams the host can't play. Extension-fallback handles the
    /// ambiguous case without us routing video files we can't actually
    /// decode end to end.
    /// </summary>
    public IEnumerable<string> SupportedContentTypes => new[] { "audio/webm" };

    public bool SupportsStreamInput => true;

    /// <summary>Cached host services. Captured at <see cref="Initialize"/>
    /// time but the codec registry itself is built AFTER all plugins
    /// finish loading (see <see cref="IPluginContext.CodecRegistry"/>
    /// docs) — so the borrow lookup MUST happen lazily at
    /// <see cref="CreateStream"/> time, not here.</summary>
    private IPluginContext? _context;

    public void Initialize(IPluginContext context)
    {
        _context = context;
    }

    public void Shutdown() { }

    public WaveStream CreateStream(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("WebM source path must not be empty.", nameof(source));

        IAudioFrameDecoder decoder = BorrowOpusDecoder();
        return new WebmOpusWaveStream(source, decoder);
    }

    public WaveStream CreateStream(Stream source, string formatHint)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        // Borrow the decoder BEFORE wrapping the stream so that, if the
        // borrow fails (codec.opus missing), we surface the actionable error
        // without having taken ownership of the caller's Stream — they still
        // own it and can dispose it themselves.
        IAudioFrameDecoder decoder = BorrowOpusDecoder();

        // FromStream takes ownership of `source` (the SDK transfers it on the
        // Stream overload): the returned WaveStream's Dispose closes it. It
        // buffers into a seekable MemoryStream when `source` isn't seekable,
        // since Matroska's Cues-based seek needs seekability.
        try
        {
            return WebmOpusWaveStream.FromStream(source, decoder);
        }
        catch
        {
            // Stream construction failed (e.g. malformed container) after the
            // decoder was borrowed — FromStream disposes the streams it took,
            // but the borrowed decoder is only owned once a WaveStream exists,
            // so release it here.
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>The load-bearing codec borrow. Resolved lazily here (not at
    /// <see cref="Initialize"/> time) because the registry is a snapshot
    /// built only after every plugin finishes loading. Returns a fresh,
    /// per-stream decoder owned by the resulting <see cref="WaveStream"/>;
    /// it is stateful across packets, so it is never shared between
    /// tracks.</summary>
    private IAudioFrameDecoder BorrowOpusDecoder()
    {
        // CodecRegistry can be null on headless / CLI hosts that don't run
        // codec dispatch — surface a clean error rather than an NRE.
        IAudioCodecRegistry registry = _context?.CodecRegistry
            ?? throw new InvalidOperationException(
                "codec.webm needs the host's codec registry to borrow the Opus decoder, " +
                "but IPluginContext.CodecRegistry is unavailable. This host does not support " +
                "inter-plugin codec dispatch.");

        IAudioCodecPlugin opus = registry.GetByExtension(".opus")
            ?? throw new InvalidOperationException(
                "codec.webm requires codec.opus to decode WebM Opus tracks. " +
                "Install github.DevinSanders/codec.opus from itch.io.");

        return opus.CreateDecoder()
            ?? throw new InvalidOperationException(
                "The installed codec.opus does not expose a frame decoder. " +
                "Update to a version that implements IAudioCodecPlugin.CreateDecoder.");
    }
}
