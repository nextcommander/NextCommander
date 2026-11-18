using System.Runtime.InteropServices;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Platform.Windows;

/// <summary>
/// Windows' Remote Desktop ActiveX control (MsTscAx, the engine of mstsc) in a child window: ATL's control host
/// window (AtlAxWin of atl.dll) creates it, and it is driven through its IDispatch (late bound, without the .NET COM
/// interop the app leaves out): the connection's settings, the password through IMsTscNonScriptable, Connect,
/// Disconnect, the state and resizing the session. Windows only (Create refuses elsewhere); used on the UI (STA) thread.
/// </summary>
public sealed partial class RemoteDesktopControl : IDisposable
{
    private nint _window;
    private Dispatch? _client;

    private RemoteDesktopControl(nint window, Dispatch client)
    {
        _window = window;
        _client = client;
    }

    /// <summary>The child window holding the control.</summary>
    public nint Window => _window;

    /// <summary>Whether the control can be made here (atl.dll and a 64-bit process: the VARIANT layout used).</summary>
    public static bool IsAvailable =>
        OperatingSystem.IsWindows() && Environment.Is64BitProcess && File.Exists(Path.Combine(Environment.SystemDirectory, "atl.dll"))
        && File.Exists(Path.Combine(Environment.SystemDirectory, "mstscax.dll"));

    /// <summary>Makes the control in a child window of <paramref name="parent"/>.</summary>
    public static RemoteDesktopControl Create(nint parent, int width, int height)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The remote desktop control is not available here.");
        }
        if (!AtlAxWinInit())
        {
            throw new InvalidOperationException("The ActiveX host (atl.dll) could not be started.");
        }
        var window = CreateWindowEx(0, "AtlAxWin", "MsTscAx.MsTscAx", WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0, 0, Math.Max(1, width), Math.Max(1, height), parent, 0, GetModuleHandle(null), 0);
        if (window == 0)
        {
            throw new InvalidOperationException("The remote desktop control could not be made (error " + Marshal.GetLastPInvokeError() + ").");
        }
        var hr = AtlAxGetControl(window, out var unknown);
        if (hr < 0 || unknown == 0)
        {
            DestroyWindow(window);
            throw new InvalidOperationException("The remote desktop control is not installed (MsTscAx, error 0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").");
        }
        try
        {
            return new RemoteDesktopControl(window, Dispatch.From(unknown, IidDispatch));
        }
        finally
        {
            Release(unknown);
        }
    }

    /// <summary>
    /// Sets the connection's settings (<see cref="RemoteDesktop.ControlSettings"/>; a setting the installed control
    /// does not know is left out) and the password, then connects.
    /// </summary>
    /// <returns>The settings the control did not take ("Advanced.RedirectClipboard"), for the log.</returns>
    public IReadOnlyList<string> Connect(RemoteConnection connection, string? password, int width, int height)
    {
        var client = _client ?? throw new ObjectDisposedException(nameof(RemoteDesktopControl));
        var parts = new Dictionary<RdpControlPart, Dispatch?>();
        var refused = new List<string>();
        try
        {
            foreach (var (part, property, value) in RemoteDesktop.ControlSettings(connection, width, height))
            {
                if (!parts.TryGetValue(part, out var target))
                {
                    target = part switch
                    {
                        RdpControlPart.Client => client,
                        RdpControlPart.Advanced => client.Newest("AdvancedSettings", 9, 2),
                        RdpControlPart.Secured => client.Newest("SecuredSettings", 3, 2),
                        _ => client.Newest("TransportSettings", 4, 1),
                    };
                    parts[part] = target;
                }
                if (target?.TrySet(property, value) != true)
                {
                    refused.Add(part + "." + property);
                }
            }
            if (!string.IsNullOrEmpty(password))
            {
                SetPassword(client, password);
            }
            client.Call("Connect");
            return refused;
        }
        finally
        {
            foreach (var part in parts.Values)
            {
                if (!ReferenceEquals(part, client))
                {
                    part?.Dispose();
                }
            }
        }
    }

    /// <summary>The state: 0 not connected, 1 connected, 2 connecting.</summary>
    public int State => _client?.TryGet("Connected") is { } value ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    /// <summary>Why the last session ended (the control's extended reason), or 0.</summary>
    public int ExtendedDisconnectReason => _client?.TryGet("ExtendedDisconnectReason") is { } value ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    /// <summary>The window follows the tab; a session that follows the window is told its new size (Windows 8.1+ servers).</summary>
    public void Resize(int width, int height, bool session)
    {
        if (_window != 0)
        {
            MoveWindow(_window, 0, 0, Math.Max(1, width), Math.Max(1, height), true);
        }
        if (session && State == 1)
        {
            _client?.TryCall("UpdateSessionDisplaySettings", width, height, width, height, 0, 100, 100);
        }
    }

    public void Disconnect()
    {
        if (State != 0)
        {
            _client?.TryCall("Disconnect");
        }
    }

    public void Dispose()
    {
        Disconnect();
        _client?.Dispose();
        _client = null;
        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }
    }

    // ---------------------------------------------------------------- the password

    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid IidMsTscNonScriptable = new("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E");

    /// <summary>IMsTscNonScriptable::put_ClearTextPassword (the 4th method: after IUnknown's three).</summary>
    private static unsafe void SetPassword(Dispatch client, string password)
    {
        var iid = IidMsTscNonScriptable;
        var vtable = *(nint**)client.Pointer;
        nint nonScriptable;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0])(client.Pointer, &iid, &nonScriptable);
        if (hr < 0 || nonScriptable == 0)
        {
            throw new InvalidOperationException("The remote desktop control does not take a password.");
        }
        var bstr = Marshal.StringToBSTR(password);
        try
        {
            var methods = *(nint**)nonScriptable;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)methods[3])(nonScriptable, bstr));
        }
        finally
        {
            Marshal.ZeroFreeBSTR(bstr);
            Release(nonScriptable);
        }
    }

    // ---------------------------------------------------------------- IDispatch, late bound

    /// <summary>An IDispatch pointer: properties got and set, methods called by name.</summary>
    private sealed unsafe class Dispatch(nint pointer) : IDisposable
    {
        private const ushort VtEmpty = 0, VtI2 = 2, VtI4 = 3, VtBstr = 8, VtDispatch = 9, VtBool = 11, VtUi4 = 19, VtInt = 22;
        private const ushort MethodFlag = 1, PropertyGet = 2, PropertyPut = 4;
        private const int DispidPropertyPut = -3;

        public nint Pointer { get; private set; } = pointer;

        /// <summary>The interface <paramref name="iid"/> of an object (its own reference).</summary>
        public static Dispatch From(nint unknown, Guid iid)
        {
            var vtable = *(nint**)unknown;
            nint result;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0])(unknown, &iid, &result));
            return new Dispatch(result);
        }

        /// <summary>The newest of numbered sub-objects ("AdvancedSettings9" down to "AdvancedSettings2"), or null.</summary>
        public Dispatch? Newest(string name, int newest, int oldest)
        {
            for (var n = newest; n >= oldest; n--)
            {
                if (TryGet(n == 1 ? name : name + n.ToString(System.Globalization.CultureInfo.InvariantCulture)) is Dispatch found)
                {
                    return found;
                }
            }
            return TryGet(name) as Dispatch;
        }

        public object? TryGet(string name)
        {
            try
            {
                return Invoke(name, PropertyGet, []);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                return null;
            }
        }

        public bool TrySet(string name, object value)
        {
            try
            {
                Invoke(name, PropertyPut, [value]);
                return true;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                return false;
            }
        }

        public object? Call(string name, params object[] args) => Invoke(name, MethodFlag, args);

        public object? TryCall(string name, params object[] args)
        {
            try
            {
                return Call(name, args);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                return null;
            }
        }

        private int IdOf(string name)
        {
            var vtable = *(nint**)Pointer;
            var iid = Guid.Empty;
            var text = Marshal.StringToCoTaskMemUni(name);
            try
            {
                int id;
                var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint, uint, int*, int>)vtable[5])(Pointer, &iid, &text, 1, 0x0400, &id);
                if (hr < 0)
                {
                    throw new InvalidOperationException("The remote desktop control has no " + name + ".");
                }
                return id;
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }

        private object? Invoke(string name, ushort flags, object[] args)
        {
            ObjectDisposedException.ThrowIf(Pointer == 0, this);
            var id = IdOf(name);
            var variants = stackalloc Variant[Math.Max(1, args.Length)];
            for (var i = 0; i < args.Length; i++)
            {
                variants[args.Length - 1 - i] = ToVariant(args[i]); // the arguments go in reverse order
            }
            var named = DispidPropertyPut;
            var parameters = new DispParams
            {
                Arguments = args.Length > 0 ? variants : null,
                NamedArguments = flags == PropertyPut ? &named : null,
                Count = (uint)args.Length,
                NamedCount = flags == PropertyPut ? 1u : 0u,
            };
            Variant result = default;
            var exception = stackalloc byte[64];
            new Span<byte>(exception, 64).Clear();
            uint argumentError = 0;
            var iid = Guid.Empty;
            var vtable = *(nint**)Pointer;
            try
            {
                var hr = ((delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, DispParams*, Variant*, byte*, uint*, int>)vtable[6])(
                    Pointer, id, &iid, 0x0400, flags, &parameters, flags == PropertyPut ? null : &result, exception, &argumentError);
                if (hr < 0)
                {
                    var description = *(nint*)(exception + 16);
                    var message = description != 0 ? Marshal.PtrToStringBSTR(description) : null;
                    throw new InvalidOperationException((message ?? name) + " (0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")");
                }
                return FromVariant(ref result);
            }
            finally
            {
                for (var i = 0; i < args.Length; i++)
                {
                    _ = VariantClear(&variants[i]);
                }
                _ = VariantClear(&result);
                foreach (var offset in new[] { 8, 16, 24 })
                {
                    var bstr = *(nint*)(exception + offset);
                    if (bstr != 0)
                    {
                        Marshal.FreeBSTR(bstr);
                    }
                }
            }
        }

        private static Variant ToVariant(object value) => value switch
        {
            string s => new Variant { Type = VtBstr, Value = Marshal.StringToBSTR(s) },
            bool b => new Variant { Type = VtBool, Value = b ? -1 : 0 },
            int i => new Variant { Type = VtI4, Value = i },
            _ => throw new ArgumentException("Not a value the control takes: " + value.GetType().Name),
        };

        private static object? FromVariant(ref Variant v) => v.Type switch
        {
            VtEmpty => null,
            VtI2 => (int)(short)v.Value,
            VtI4 or VtInt => (int)v.Value,
            VtUi4 => (int)(uint)v.Value,
            VtBool => (short)v.Value != 0,
            VtBstr => v.Value == 0 ? string.Empty : Marshal.PtrToStringBSTR((nint)v.Value),
            VtDispatch when v.Value != 0 => TakeDispatch(ref v),
            _ => null,
        };

        /// <summary>A dispatch result kept: the variant's reference becomes the wrapper's (VariantClear then sees none).</summary>
        private static Dispatch TakeDispatch(ref Variant v)
        {
            var dispatch = new Dispatch((nint)v.Value);
            v = default;
            return dispatch;
        }

        public void Dispose()
        {
            if (Pointer != 0)
            {
                Release(Pointer);
                Pointer = 0;
            }
        }
    }

    /// <summary>A VARIANT of a 64-bit process: the type, three reserved words, the value (24 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public long Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DispParams
    {
        public Variant* Arguments;
        public int* NamedArguments;
        public uint Count;
        public uint NamedCount;
    }

    private static unsafe void Release(nint unknown)
    {
        var vtable = *(nint**)unknown;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtable[2])(unknown);
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException("The remote desktop control failed (0x" + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").");
        }
    }

    // ---------------------------------------------------------------- Win32

    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000;

    [LibraryImport("atl.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AtlAxWinInit();

    [LibraryImport("atl.dll")]
    private static partial int AtlAxGetControl(nint window, out nint unknown);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(nint window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);

    [LibraryImport("oleaut32.dll")]
    private static unsafe partial int VariantClear(Variant* variant);
}
