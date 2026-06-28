using System.IO;

namespace WebmCodecPlugin.Tests;

/// <summary>Seekable in-memory stream that records whether it was disposed,
/// so the Stream-overload ownership-transfer contract can be asserted.</summary>
internal sealed class TrackingStream : MemoryStream
{
    public TrackingStream(byte[] bytes) : base(bytes, writable: false) { }

    public bool Disposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
