using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NextCommander.Core.Config;
using NextCommander.Core.Net;

namespace NextCommander.Platform.Windows;

/// <summary>
/// Windows' Remote Desktop ActiveX control (MsTscAx, the engine of mstsc) in a child window: ATL's control host
/// window (AtlAxWin of atl.dll) creates it, and it is driven through its IDispatch (late bound, without the .NET COM
/// interop the app leaves out): the connection's settings, the password through IMsTscNonScriptable, one drive shared
/// through IMsRdpClientNonScriptable3's drive list, Connect, Disconnect, the state and resizing the session. Its events
/// (IMsTscAxEvents: connected, disconnected with the reason) come to a small COM object of its own. The vtable slots
/// are those of the type library in mstscax.dll. Windows only (Create refuses elsewhere); used on the UI (STA) thread.
/// </summary>
public sealed partial class RemoteDesktopControl : IDisposable
{
    private nint _window;
    private Dispatch? _client;
    private nint _sink;
    private nint _connectionPoint;
    private uint _cookie;

    private RemoteDesktopControl(nint window, Dispatch client)
    {
        _window = window;
        _client = client;
        try
        {
            Advise();
        }
        catch (InvalidOperationException)
        {
            ReleaseEvents(); // no events: the owner polls the state
        }
    }

    /// <summary>Whether the control's events arrive (else <see cref="State"/> is to be polled).</summary>
    public bool HasEvents => _cookie != 0;

    /// <summary>The session is connected (OnConnected / OnLoginComplete).</summary>
    public event EventHandler? Connected;

    /// <summary>The session ended or could not be made (OnDisconnected): its reason and the control's words for it.</summary>
    public event EventHandler<RemoteDesktopDisconnect>? Disconnected;

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
    /// <param name="drive">The one drive to share ("D:\\", <see cref="RemoteDesktop.ControlDrive"/>), or null.</param>
    public IReadOnlyList<string> Connect(RemoteConnection connection, string? password, int width, int height, string? drive = null)
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
            if (drive is not null && !ShareDrive(client, drive))
            {
                refused.Add("Drive " + drive);
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

    /// <summary>
    /// Full screen: the control shows the session on the whole monitor with its connection bar (its restore button
    /// comes back to the tab; <see cref="FullScreenChanged"/> says both). False when the control refused.
    /// </summary>
    public bool EnterFullScreen() => State == 1 && _client?.TrySet("FullScreen", true) == true;

    /// <summary>Whether the remote desktop is scaled into the window (smart sizing), switched while connected.</summary>
    public bool SetFitToWindow(bool on)
    {
        using var advanced = _client?.Newest("AdvancedSettings", 9, 2);
        return advanced?.TrySet("SmartSizing", on) == true;
    }

    /// <summary>Tells the session a new desktop size (Windows 8.1 / Server 2012 R2 and later servers), the window as it is.</summary>
    public void UpdateSessionSize(int width, int height)
    {
        if (State == 1 && width > 0 && height > 0)
        {
            _client?.TryCall("UpdateSessionDisplaySettings", width, height, width, height, 0, 100, 100);
        }
    }

    /// <summary>The session went to full screen (true) or came back into its window (false).</summary>
    public event EventHandler<bool>? FullScreenChanged;

    /// <summary>The connection bar's minimize button in full screen: the container is to be minimized.</summary>
    public event EventHandler? MinimizeRequested;

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
        ReleaseEvents();
        _client?.Dispose();
        _client = null;
        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }
    }

    // ---------------------------------------------------------------- the password, the drive

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid IidMsTscNonScriptable = new("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E");
    private static readonly Guid IidMsRdpClientNonScriptable3 = new("B3378D90-0728-45C7-8ED7-B6159FB92219");
    private static readonly Guid IidConnectionPointContainer = new("B196B284-BAB4-101A-B69C-00AA00341D07");
    private static readonly Guid DiidMsTscAxEvents = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");

    /// <summary>Another interface of a COM object (its own reference), or 0.</summary>
    private static unsafe nint QueryInterface(nint unknown, Guid iid)
    {
        var vtable = *(nint**)unknown;
        nint result;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0])(unknown, &iid, &result);
        return hr < 0 ? 0 : result;
    }

    /// <summary>
    /// Shares one drive of this computer: IMsRdpClientNonScriptable3's DriveCollection (slot 30), its drives
    /// (DriveCount slot 5, DriveByIndex slot 4) by name (IMsRdpDrive Name slot 3, "Data (D:)"), RedirectionState (slot 4).
    /// </summary>
    private static unsafe bool ShareDrive(Dispatch client, string drive)
    {
        var letter = char.ToUpperInvariant(drive[0]) + ":";
        var settings = QueryInterface(client.Pointer, IidMsRdpClientNonScriptable3);
        if (settings == 0)
        {
            return false;
        }
        nint collection = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)settings)[30])(settings, &collection) < 0 || collection == 0)
            {
                return false;
            }
            var methods = *(nint**)collection;
            uint count;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)methods[5])(collection, &count) < 0)
            {
                return false;
            }
            for (uint i = 0; i < count; i++)
            {
                nint item;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)methods[4])(collection, i, &item) < 0 || item == 0)
                {
                    continue;
                }
                try
                {
                    var itemMethods = *(nint**)item;
                    nint name;
                    if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)itemMethods[3])(item, &name) < 0)
                    {
                        continue;
                    }
                    var text = name == 0 ? string.Empty : Marshal.PtrToStringBSTR(name);
                    Marshal.FreeBSTR(name);
                    if (text.Contains(letter, StringComparison.OrdinalIgnoreCase))
                    {
                        return ((delegate* unmanaged[Stdcall]<nint, short, int>)itemMethods[4])(item, -1) >= 0;
                    }
                }
                finally
                {
                    Release(item);
                }
            }
            return false;
        }
        finally
        {
            if (collection != 0)
            {
                Release(collection);
            }
            Release(settings);
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>Connects the event object to the control's IMsTscAxEvents connection point.</summary>
    private unsafe void Advise()
    {
        var client = _client!;
        var container = QueryInterface(client.Pointer, IidConnectionPointContainer);
        if (container == 0)
        {
            throw new InvalidOperationException("The remote desktop control has no events.");
        }
        try
        {
            var iid = DiidMsTscAxEvents;
            nint point;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)container)[4])(container, &iid, &point)); // FindConnectionPoint
            _connectionPoint = point;
            _sink = EventSink.Create(this);
            uint cookie;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)(*(nint**)point)[5])(point, _sink, &cookie)); // Advise
            _cookie = cookie;
        }
        finally
        {
            Release(container);
        }
    }

    private unsafe void ReleaseEvents()
    {
        if (_connectionPoint != 0)
        {
            if (_cookie != 0)
            {
                _ = ((delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)_connectionPoint)[6])(_connectionPoint, _cookie); // Unadvise
                _cookie = 0;
            }
            Release(_connectionPoint);
            _connectionPoint = 0;
        }
        if (_sink != 0)
        {
            EventSink.Detach(_sink);
            Release(_sink);
            _sink = 0;
        }
    }

    /// <summary>
    /// An event of the control (IMsTscAxEvents' DISPIDs: 2 OnConnected, 3 OnLoginComplete, 4 OnDisconnected, 5
    /// OnEnterFullScreenMode, 6 OnLeaveFullScreenMode, 14 OnRequestContainerMinimize).
    /// </summary>
    private void OnEvent(int id, int argument)
    {
        switch (id)
        {
            case 2 or 3:
                Connected?.Invoke(this, EventArgs.Empty);
                break;
            case 5 or 6:
                FullScreenChanged?.Invoke(this, id == 5);
                break;
            case 14:
                MinimizeRequested?.Invoke(this, EventArgs.Empty);
                break;
            case 4:
                var extended = ExtendedDisconnectReason;
                var description = _client?.TryCall("GetErrorDescription", argument, extended) as string;
                Disconnected?.Invoke(this, new RemoteDesktopDisconnect(argument, extended, string.IsNullOrWhiteSpace(description) ? null : description.Trim()));
                break;
        }
    }

    /// <summary>
    /// The COM object that receives the control's events: a dispinterface (IDispatch) made by hand, as the app has no
    /// built-in COM interop: a vtable of seven functions, a reference count and a handle of its control.
    /// </summary>
    private static unsafe class EventSink
    {
        private const int NoInterface = unchecked((int)0x80004002), NotImplemented = unchecked((int)0x80004001);

        [StructLayout(LayoutKind.Sequential)]
        private struct Native
        {
            public nint* Vtable;
            public int References;
            public nint Owner; // a GCHandle of the control; 0 once detached
        }

        private static readonly nint* s_vtable = CreateVtable();

        private static nint* CreateVtable()
        {
            var vtable = (nint*)NativeMemory.Alloc(7, (nuint)sizeof(nint));
            vtable[0] = (nint)(delegate* unmanaged[Stdcall]<Native*, Guid*, nint*, int>)&QueryInterface;
            vtable[1] = (nint)(delegate* unmanaged[Stdcall]<Native*, uint>)&AddRef;
            vtable[2] = (nint)(delegate* unmanaged[Stdcall]<Native*, uint>)&Release;
            vtable[3] = (nint)(delegate* unmanaged[Stdcall]<Native*, uint*, int>)&GetTypeInfoCount;
            vtable[4] = (nint)(delegate* unmanaged[Stdcall]<Native*, uint, uint, nint*, int>)&GetTypeInfo;
            vtable[5] = (nint)(delegate* unmanaged[Stdcall]<Native*, Guid*, nint*, uint, uint, int*, int>)&GetIDsOfNames;
            vtable[6] = (nint)(delegate* unmanaged[Stdcall]<Native*, int, Guid*, uint, ushort, DispParams*, Variant*, byte*, uint*, int>)&Invoke;
            return vtable;
        }

        public static nint Create(RemoteDesktopControl owner)
        {
            var native = (Native*)NativeMemory.AllocZeroed((nuint)sizeof(Native));
            native->Vtable = s_vtable;
            native->References = 1;
            native->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner));
            return (nint)native;
        }

        /// <summary>The control is gone: later events (and the last Release) find no owner.</summary>
        public static void Detach(nint sink)
        {
            var native = (Native*)sink;
            if (native->Owner != 0)
            {
                GCHandle.FromIntPtr(native->Owner).Free();
                native->Owner = 0;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(Native* self, Guid* iid, nint* result)
        {
            if (*iid == IidUnknown || *iid == IidDispatch || *iid == DiidMsTscAxEvents)
            {
                Interlocked.Increment(ref self->References);
                *result = (nint)self;
                return 0;
            }
            *result = 0;
            return NoInterface;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Native* self) => (uint)Interlocked.Increment(ref self->References);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(Native* self)
        {
            var left = Interlocked.Decrement(ref self->References);
            if (left == 0)
            {
                if (self->Owner != 0)
                {
                    GCHandle.FromIntPtr(self->Owner).Free();
                }
                NativeMemory.Free(self);
            }
            return (uint)left;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetTypeInfoCount(Native* self, uint* count)
        {
            *count = 0;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetTypeInfo(Native* self, uint index, uint locale, nint* info)
        {
            *info = 0;
            return NotImplemented;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetIDsOfNames(Native* self, Guid* iid, nint* names, uint count, uint locale, int* ids) => NotImplemented;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(Native* self, int id, Guid* iid, uint locale, ushort flags, DispParams* parameters, Variant* result, byte* exception, uint* argumentError)
        {
            if (self->Owner == 0 || GCHandle.FromIntPtr(self->Owner).Target is not RemoteDesktopControl owner)
            {
                return 0;
            }
            // the first argument (the last in the array); OnDisconnected's reason is a VT_I4
            var argument = parameters is not null && parameters->Count > 0 ? (int)parameters->Arguments[parameters->Count - 1].Value : 0;
            try
            {
                owner.OnEvent(id, argument);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or ArgumentException)
            {
                // an event handler's failure must not go back into the control
            }
            return 0;
        }
    }

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

/// <summary>Why a remote desktop session ended: the control's reason, its extended reason and its description (or null).</summary>
public sealed record RemoteDesktopDisconnect(int Reason, int ExtendedReason, string? Description);
