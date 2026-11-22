using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NextCommander.Platform.Windows;

/// <summary>
/// Dragging files out with the shell's own data object, the one Explorer drags (SHCreateDataObject): the file list,
/// the shell's item list, file names and the rest. Some programs take only that one: Microsoft Teams and other
/// Chromium-based programs accepted files dragged from Explorer but not from the program's own simpler data object
/// (user's report, 22 November 2026).
/// </summary>
public static unsafe partial class ShellDrag
{
    private const int DragDropSDrop = 0x00040100;
    private const int DragDropSCancel = 0x00040101;
    private const int DragDropSUseDefaultCursors = 0x00040102;
    private const uint MkLButton = 0x0001;

    private static readonly StrategyBasedComWrappers s_wrappers = new();
    private static readonly Guid s_dataObject = new("0000010e-0000-0000-C000-000000000046");
    private static readonly Guid s_dropSource = new("00000121-0000-0000-C000-000000000046");

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial nint ILFindLastID(nint pidl);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(nint pidl);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateDataObject(nint folder, uint count, nint* children, nint inner, in Guid riid, out nint dataObject);

    [LibraryImport("ole32.dll")]
    private static partial int DoDragDrop(nint dataObject, nint dropSource, uint allowedEffects, out uint effect);

    /// <summary>
    /// Drags local files and folders of one folder (blocking until the drop, like any OLE drag; the program's own windows
    /// still take the drop). The effect done (1 copy, 2 move, 4 link, 0 nothing), or null when it cannot be done this way
    /// (not Windows, files of several folders, a path the shell does not know): then the caller drags its own way.
    /// </summary>
    public static int? Drag(IReadOnlyList<string> paths, int allowedEffects)
    {
        if (!OperatingSystem.IsWindows() || paths.Count == 0)
        {
            return null;
        }
        var folder = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
        if (folder is null || paths.Any(p => !string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), folder, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        var items = new List<nint>();
        nint folderId = 0;
        nint dataObject = 0;
        nint unknown = 0;
        nint dropSource = 0;
        try
        {
            if (SHParseDisplayName(folder, 0, out folderId, 0, out _) != 0)
            {
                return null;
            }
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, 0, out var item, 0, out _) != 0)
                {
                    return null;
                }
                items.Add(item);
            }
            // the children's own ids: the last part of each full id
            var children = items.Select(ILFindLastID).ToArray();
            fixed (nint* first = children)
            {
                if (SHCreateDataObject(folderId, (uint)children.Length, first, 0, s_dataObject, out dataObject) != 0 || dataObject == 0)
                {
                    return null;
                }
            }
            unknown = s_wrappers.GetOrCreateComInterfaceForObject(new DropSource(), CreateComInterfaceFlags.None);
            if (Marshal.QueryInterface(unknown, s_dropSource, out dropSource) != 0)
            {
                return null;
            }
            var result = DoDragDrop(dataObject, dropSource, (uint)allowedEffects, out var effect);
            return result == DragDropSDrop ? (int)effect : 0;
        }
        finally
        {
            foreach (var pointer in new[] { dropSource, unknown, dataObject }.Where(p => p != 0))
            {
                Marshal.Release(pointer);
            }
            foreach (var item in items)
            {
                ILFree(item);
            }
            if (folderId != 0)
            {
                ILFree(folderId);
            }
        }
    }

    /// <summary>FORMATETC (objidl.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        public ushort Format;
        public nint TargetDevice;
        public uint Aspect;
        public int Index;
        public uint Medium;
    }

    /// <summary>
    /// Whether the shell's data object of these files offers the file list (CF_HDROP), as Explorer's does (tests: the
    /// object is made and released without dragging).
    /// </summary>
    public static bool OffersFileList(IReadOnlyList<string> paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Count == 0 || Path.GetDirectoryName(paths[0].TrimEnd('\\')) is not { } folder)
        {
            return false;
        }
        var items = new List<nint>();
        nint folderId = 0;
        nint dataObject = 0;
        try
        {
            if (SHParseDisplayName(folder, 0, out folderId, 0, out _) != 0)
            {
                return false;
            }
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, 0, out var item, 0, out _) != 0)
                {
                    return false;
                }
                items.Add(item);
            }
            var children = items.Select(ILFindLastID).ToArray();
            fixed (nint* first = children)
            {
                if (SHCreateDataObject(folderId, (uint)children.Length, first, 0, s_dataObject, out dataObject) != 0 || dataObject == 0)
                {
                    return false;
                }
            }
            // IDataObject::QueryGetData is the sixth method (after IUnknown's three, GetData and GetDataHere)
            var format = new FormatEtc { Format = 15 /* CF_HDROP */, Aspect = 1, Index = -1, Medium = 1 /* TYMED_HGLOBAL */ };
            var queryGetData = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, int>)(*(nint**)dataObject)[5];
            return queryGetData(dataObject, &format) == 0;
        }
        finally
        {
            if (dataObject != 0)
            {
                Marshal.Release(dataObject);
            }
            foreach (var item in items)
            {
                ILFree(item);
            }
            if (folderId != 0)
            {
                ILFree(folderId);
            }
        }
    }

    /// <summary>IDropSource: ends the drag when the left button is let go, cancels it on Esc, default cursors.</summary>
    [GeneratedComInterface]
    [Guid("00000121-0000-0000-C000-000000000046")]
    internal partial interface IDropSource
    {
        [PreserveSig]
        int QueryContinueDrag(int escapePressed, uint keyState);

        [PreserveSig]
        int GiveFeedback(uint effect);
    }

    [GeneratedComClass]
    internal sealed partial class DropSource : IDropSource
    {
        public int QueryContinueDrag(int escapePressed, uint keyState) =>
            escapePressed != 0 ? DragDropSCancel : (keyState & MkLButton) == 0 ? DragDropSDrop : 0;

        public int GiveFeedback(uint effect) => DragDropSUseDefaultCursors;
    }
}
