using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DynamicIsland.Interop;

namespace DynamicIsland.Features;

/// <summary>Полка: файлы, которые «положили» на островок и потом достанут по одному или все сразу.</summary>
internal sealed class ShelfService
{
    private readonly Lock _gate = new();
    private readonly List<string> _files = [];

    public event Action? Changed;

    /// <summary>Положили файлы — островок может переключиться на вкладку «Полка».</summary>
    public event Action? Dropped;

    public List<string> Snapshot()
    {
        lock (_gate) return [.. _files];
    }

    public int Count { get { lock (_gate) return _files.Count; } }

    public void Add(IEnumerable<string> paths)
    {
        var added = false;
        lock (_gate)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p) || _files.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                _files.Add(p);
                added = true;
            }
        }
        if (!added) return;
        Changed?.Invoke();
        Dropped?.Invoke();
    }

    public void Remove(string path)
    {
        lock (_gate) _files.Remove(path);
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) _files.Clear();
        Changed?.Invoke();
    }

    /// <summary>Убирает файлы, которые успели удалить или унести.</summary>
    public void Prune()
    {
        bool changed;
        lock (_gate) changed = _files.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)) > 0;
        if (changed) Changed?.Invoke();
    }
}

/// <summary>Принимает файлы, брошенные на островок (OLE drag and drop).</summary>
internal sealed class ShelfDropTarget(ShelfService shelf) : Native.IDropTarget
{
    private const short CF_HDROP = 15;
    private const int DROPEFFECT_COPY = 1;

    private static bool HasFiles(IDataObject data)
    {
        var fmt = Format();
        return data.QueryGetData(ref fmt) == 0;
    }

    private static FORMATETC Format() => new()
    {
        cfFormat = CF_HDROP,
        ptd = 0,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = TYMED.TYMED_HGLOBAL,
    };

    public int DragEnter(IDataObject dataObject, int keyState, Native.POINTL pt, ref int effect)
    {
        effect = HasFiles(dataObject) ? DROPEFFECT_COPY : 0;
        return 0;
    }

    public int DragOver(int keyState, Native.POINTL pt, ref int effect)
    {
        effect = DROPEFFECT_COPY;
        return 0;
    }

    public int DragLeave() => 0;

    public int Drop(IDataObject dataObject, int keyState, Native.POINTL pt, ref int effect)
    {
        effect = 0;
        try
        {
            var fmt = Format();
            if (dataObject.QueryGetData(ref fmt) != 0) return 0;

            dataObject.GetData(ref fmt, out var medium);
            try
            {
                var count = Native.DragQueryFileW(medium.unionmember, 0xFFFFFFFF, null, 0);
                var paths = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    var length = Native.DragQueryFileW(medium.unionmember, i, null, 0);
                    var buffer = new char[length + 1];
                    Native.DragQueryFileW(medium.unionmember, i, buffer, length + 1);
                    paths.Add(new string(buffer, 0, (int)length));
                }
                shelf.Add(paths);
                effect = DROPEFFECT_COPY;
            }
            finally
            {
                Native.ReleaseStgMedium(ref medium);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Файлы не приняты: " + ex.Message);
        }
        return 0;
    }
}

/// <summary>Перетаскивание файлов с полки в другое окно.</summary>
internal static class ShelfDragSource
{
    [ComImport, Guid("00000121-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(bool escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    private sealed class Source : IDropSource
    {
        private const int DRAGDROP_S_DROP = 0x00040100;
        private const int DRAGDROP_S_CANCEL = 0x00040101;
        private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
        private const int MK_LBUTTON = 1;

        public int QueryContinueDrag(bool escapePressed, int keyState)
        {
            if (escapePressed) return DRAGDROP_S_CANCEL;
            return (keyState & MK_LBUTTON) == 0 ? DRAGDROP_S_DROP : 0;
        }

        public int GiveFeedback(int effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(IDataObject data, IDropSource source, int okEffects, out int effect);

    /// <summary>Запускает системное перетаскивание. Возвращается, когда кнопку отпустили.</summary>
    public static void Start(IEnumerable<string> paths)
    {
        try
        {
            var data = new System.Windows.DataObject();
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths.ToArray());
            data.SetFileDropList(list);
            DoDragDrop((IDataObject)data, new Source(), 1 | 2, out _);
        }
        catch (Exception ex)
        {
            Log.Write("Перетаскивание не удалось: " + ex.Message);
        }
    }
}
