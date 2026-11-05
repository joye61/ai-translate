using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AiTranslate.Platform.Windows;

/// <summary>Owns detached copies until Win32 takes ownership on a successful restore.</summary>
internal sealed class ClipboardSnapshot : IDisposable
{
    private readonly List<(uint Format, STGMEDIUM Medium)> _copies = [];
    internal uint Sequence { get; private set; }

    internal static ClipboardSnapshot? TryTake()
    {
        if (!TryOpen(0)) return null;
        var snapshot = new ClipboardSnapshot();
        var complete = false;
        try
        {
            uint format = 0;
            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                // Private, owner-display and application-owned GDI handles have no generic clone.
                // Leave such a clipboard in place rather than restoring only part of its contents.
                var medium = format switch
                {
                    2 or 9 or 0x82 => TYMED.TYMED_GDI,
                    3 or 0x83 => TYMED.TYMED_MFPICT,
                    14 or 0x8E => TYMED.TYMED_ENHMF,
                    >= 1 and <= 17 or 0x81 or >= 0xC000 and <= 0xFFFF => TYMED.TYMED_HGLOBAL,
                    _ => TYMED.TYMED_NULL
                };
                if (medium == TYMED.TYMED_NULL) return null;
                var source = NativeMethods.GetClipboardData(format);
                if (source == 0) return null;
                var copy = medium == TYMED.TYMED_ENHMF ? CopyEnhMetaFile(source, null) :
                    OleDuplicateData(source, (ushort)(format is 0x82 or 0x83 ? format - 0x80 : format), 0);
                if (copy == 0) return null;
                snapshot._copies.Add((format, new STGMEDIUM { tymed = medium, unionmember = copy }));
            }
            if (Marshal.GetLastWin32Error() != 0) return null;
            snapshot.Sequence = NativeMethods.GetClipboardSequenceNumber();
            complete = true;
            return snapshot;
        }
        finally
        {
            NativeMethods.CloseClipboard();
            if (!complete) snapshot.Dispose();
        }
    }

    internal static bool TryOpen(nint owner)
    {
        for (var i = 0; i < 8; i++)
        {
            if (NativeMethods.OpenClipboard(owner)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    // Called with the clipboard open and still holding the verified target application's copy.
    internal void Restore()
    {
        if (!NativeMethods.EmptyClipboard()) return;
        for (var i = 0; i < _copies.Count; i++)
        {
            var (format, medium) = _copies[i];
            if (NativeMethods.SetClipboardData(format, medium.unionmember) != 0)
                _copies[i] = (format, default);
        }
    }

    public void Dispose()
    {
        foreach (var (_, value) in _copies)
        {
            var medium = value;
            if (medium.unionmember != 0) ReleaseStgMedium(ref medium);
        }
        _copies.Clear();
    }

    [DllImport("ole32.dll")]
    private static extern nint OleDuplicateData(nint source, ushort format, uint flags);
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("gdi32.dll", EntryPoint = "CopyEnhMetaFileW", CharSet = CharSet.Unicode)]
    private static extern nint CopyEnhMetaFile(nint source, string? path);
}
