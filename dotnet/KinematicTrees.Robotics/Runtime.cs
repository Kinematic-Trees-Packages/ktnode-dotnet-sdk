using System.Runtime.InteropServices;
using System.Text;

namespace KinematicTrees.Robotics;

public enum NextStep : uint { Continue = 0, Stop = 1, Recoverable = 2, Fatal = 3 }
public enum ReadMode : uint { One = 0, AllAvailable = 1, Count = 2 }

public sealed class KtException : Exception
{
    public uint Status { get; }
    public KtException(uint status, string message) : base(string.IsNullOrEmpty(message) ? $"KT status {status}" : message) => Status = status;
}

public readonly record struct Message(byte[] Payload, string? SourceId = null, long? RemoteTimeNs = null);

public interface INode
{
    NextStep Setup(Context ctx) => NextStep.Continue;
    NextStep Step(Context ctx) => NextStep.Stop;
    NextStep Close(Context ctx) => NextStep.Stop;
}

public sealed class Context
{
    private readonly IntPtr _ptr;
    internal Context(IntPtr ptr) => _ptr = ptr;

    public bool IsClosing()
    {
        uint value = 0;
        Check(Native.kt_context_is_closing(_ptr, ref value));
        return value != 0;
    }

    public void RequestClose() => Check(Native.kt_context_request_close(_ptr));
    public void ReportError(string message)
    {
        using var view = PinnedStringView.Pin(message);
        Check(Native.kt_context_report_error(_ptr, view.View));
    }

    public void Set(string channel, ReadOnlySpan<byte> payload)
    {
        using var channelView = PinnedStringView.Pin(channel);
        using var bytes = PinnedBytesView.Pin(payload);
        IntPtr error = IntPtr.Zero;
        Check(Native.kt_context_set(_ptr, channelView.View, bytes.View, ref error), error);
    }

    public void SetFrom(string channel, string sourceId, ReadOnlySpan<byte> payload)
    {
        using var channelView = PinnedStringView.Pin(channel);
        using var sourceView = PinnedStringView.Pin(sourceId);
        using var bytes = PinnedBytesView.Pin(payload);
        IntPtr error = IntPtr.Zero;
        Check(Native.kt_context_set_source(_ptr, channelView.View, sourceView.View, bytes.View, ref error), error);
    }


    public string MetricsJson()
    {
        IntPtr output = IntPtr.Zero;
        IntPtr error = IntPtr.Zero;
        Check(Native.kt_context_metrics_json(_ptr, ref output, ref error), error);
        if (output == IntPtr.Zero) return string.Empty;
        try
        {
            return Native.kt_owned_bytes_view(output).ToUtf8String();
        }
        finally
        {
            Native.kt_owned_bytes_destroy(ref output);
        }
    }

    public IReadOnlyList<Message> Get(string channel, ReadMode mode = ReadMode.One, ulong count = 0)
    {
        using var channelView = PinnedStringView.Pin(channel);
        var options = new ReadOptions { StructSize = (uint)Marshal.SizeOf<ReadOptions>(), AbiVersion = Native.AbiMajor, Mode = (uint)mode, Count = count };
        IntPtr batch = IntPtr.Zero;
        IntPtr error = IntPtr.Zero;
        Check(Native.kt_context_read(_ptr, channelView.View, ref options, ref batch, ref error), error);
        if (batch == IntPtr.Zero) return Array.Empty<Message>();
        try
        {
            ulong total = Native.kt_message_batch_count(batch);
            var messages = new List<Message>((int)total);
            for (ulong i = 0; i < total; i++)
            {
                var item = new MessageView { StructSize = (uint)Marshal.SizeOf<MessageView>(), AbiVersion = Native.AbiMajor };
                IntPtr itemError = IntPtr.Zero;
                Check(Native.kt_message_batch_item(batch, i, ref item, ref itemError), itemError);
                byte[] payload = item.Payload.ToArray();
                string? source = item.HasSource != 0 ? item.SourceId.ToManagedString() : null;
                long? remote = item.HasRemoteTime != 0 ? item.RemoteTimeNs : null;
                messages.Add(new Message(payload, source, remote));
            }
            return messages;
        }
        finally
        {
            Native.kt_message_batch_destroy(ref batch);
        }
    }

    internal static void Check(uint status, IntPtr error = default)
    {
        if (status == 0) return;
        string message = string.Empty;
        if (error != IntPtr.Zero)
        {
            try { message = Native.kt_error_message(error).ToManagedString(); }
            finally { Native.kt_error_destroy(ref error); }
        }
        if (string.IsNullOrEmpty(message)) message = Native.kt_status_name(status).ToManagedString();
        throw new KtException(status, message);
    }
}

public sealed class Runtime : IDisposable
{
    private readonly INode _node;
    private readonly GCHandle _handle;
    private readonly Native.AlgorithmSetupFn _setup;
    private readonly Native.AlgorithmStepFn _step;
    private readonly Native.AlgorithmCloseFn _close;
    private IntPtr _runtime;
    private bool _disposed;

    public Runtime(string packagePath, string runtimePath, INode node)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        if (Native.kt_abi_version_major() != Native.AbiMajor) throw new KtException(3, $"unsupported KT Robotics ABI major {Native.kt_abi_version_major()}");
        _setup = SetupThunk;
        _step = StepThunk;
        _close = CloseThunk;
        _handle = GCHandle.Alloc(this);
        using var packageView = PinnedStringView.Pin(packagePath);
        using var runtimeView = PinnedStringView.Pin(runtimePath);
        var callbacks = new AlgorithmCallbacks
        {
            StructSize = (uint)Marshal.SizeOf<AlgorithmCallbacks>(),
            AbiVersion = Native.AbiMajor,
            Setup = Marshal.GetFunctionPointerForDelegate(_setup),
            Step = Marshal.GetFunctionPointerForDelegate(_step),
            Close = Marshal.GetFunctionPointerForDelegate(_close),
        };
        var options = new RuntimeOptions
        {
            StructSize = (uint)Marshal.SizeOf<RuntimeOptions>(),
            AbiVersion = Native.AbiMajor,
            PackagePath = packageView.View,
            RuntimePath = runtimeView.View,
            Callbacks = Marshal.AllocHGlobal(Marshal.SizeOf<AlgorithmCallbacks>()),
            UserData = GCHandle.ToIntPtr(_handle),
        };
        try
        {
            Marshal.StructureToPtr(callbacks, options.Callbacks, false);
            IntPtr error = IntPtr.Zero;
            Context.Check(Native.kt_runtime_create_v1(ref options, ref _runtime, ref error), error);
        }
        finally
        {
            Marshal.FreeHGlobal(options.Callbacks);
        }
    }

    public static (uint Major, uint Minor) AbiVersion => (Native.kt_abi_version_major(), Native.kt_abi_version_minor());
    public static string BuildId => Native.kt_runtime_build_id().ToManagedString();
    public static (uint Major, uint Minor, uint Patch) RuntimeVersion
    {
        get
        {
            var version = new VersionInfo { StructSize = (uint)Marshal.SizeOf<VersionInfo>(), AbiVersion = Native.AbiMajor };
            Context.Check(Native.kt_runtime_version(ref version));
            return (version.Major, version.Minor, version.Patch);
        }
    }

    public void Run()
    {
        IntPtr error = IntPtr.Zero;
        Context.Check(Native.kt_runtime_run(_runtime, ref error), error);
    }

    public void RequestClose() => Context.Check(Native.kt_runtime_request_close(_runtime));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime != IntPtr.Zero)
        {
            IntPtr error = IntPtr.Zero;
            Context.Check(Native.kt_runtime_destroy(ref _runtime, ref error), error);
        }
        if (_handle.IsAllocated) _handle.Free();
    }

    public static void Run(string packagePath, string runtimePath, INode node)
    {
        using var runtime = new Runtime(packagePath, runtimePath, node);
        runtime.Run();
    }

    private static NextStep SetupThunk(IntPtr userData, IntPtr context) => Invoke(userData, context, static (node, ctx) => node.Setup(ctx));
    private static NextStep StepThunk(IntPtr userData, IntPtr context) => Invoke(userData, context, static (node, ctx) => node.Step(ctx));
    private static NextStep CloseThunk(IntPtr userData, IntPtr context) => Invoke(userData, context, static (node, ctx) => node.Close(ctx));

    private static NextStep Invoke(IntPtr userData, IntPtr context, Func<INode, Context, NextStep> call)
    {
        try
        {
            var runtime = (Runtime)GCHandle.FromIntPtr(userData).Target!;
            return call(runtime._node, new Context(context));
        }
        catch
        {
            return NextStep.Fatal;
        }
    }
}

internal static class Native
{
    public const uint AbiMajor = 1;
    public const uint AbiMinor = 1;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate NextStep AlgorithmSetupFn(IntPtr userData, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate NextStep AlgorithmStepFn(IntPtr userData, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate NextStep AlgorithmCloseFn(IntPtr userData, IntPtr context);

    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_abi_version_major();
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_abi_version_minor();
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern StringView kt_runtime_build_id();
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_runtime_version(ref VersionInfo version);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_runtime_create_v1(ref RuntimeOptions options, ref IntPtr runtime, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_runtime_run(IntPtr runtime, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_runtime_request_close(IntPtr runtime);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_runtime_destroy(ref IntPtr runtime, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_context_is_closing(IntPtr context, ref uint value);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_context_request_close(IntPtr context);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_context_report_error(IntPtr context, StringView message);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_context_set(IntPtr context, StringView channel, BytesView payload, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_context_set_source(IntPtr context, StringView channel, StringView source, BytesView payload, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_context_read(IntPtr context, StringView channel, ref ReadOptions options, ref IntPtr batch, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_context_metrics_json(IntPtr context, ref IntPtr output, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern ulong kt_message_batch_count(IntPtr batch);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern uint kt_message_batch_item(IntPtr batch, ulong index, ref MessageView item, ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern void kt_message_batch_destroy(ref IntPtr batch);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern BytesView kt_owned_bytes_view(IntPtr bytes);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern void kt_owned_bytes_destroy(ref IntPtr bytes);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern StringView kt_error_message(IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern void kt_error_destroy(ref IntPtr error);
    [DllImport("libkt_node.so", CallingConvention = CallingConvention.Cdecl)] public static extern StringView kt_status_name(uint status);
}

[StructLayout(LayoutKind.Sequential)] internal struct StringView { public IntPtr Data; public ulong Length; public string ToManagedString() => Data == IntPtr.Zero || Length == 0 ? string.Empty : Marshal.PtrToStringUTF8(Data, checked((int)Length)) ?? string.Empty; }
[StructLayout(LayoutKind.Sequential)] internal struct BytesView { public IntPtr Data; public ulong Length; public byte[] ToArray() { if (Data == IntPtr.Zero || Length == 0) return Array.Empty<byte>(); var result = new byte[checked((int)Length)]; Marshal.Copy(Data, result, 0, result.Length); return result; } public string ToUtf8String() => Data == IntPtr.Zero || Length == 0 ? string.Empty : Marshal.PtrToStringUTF8(Data, checked((int)Length)) ?? string.Empty; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct ReadOptions { public uint StructSize; public uint AbiVersion; public uint Mode; public uint Reserved0; public ulong Count; public fixed ulong Reserved[4]; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct MessageView { public uint StructSize; public uint AbiVersion; public BytesView Payload; public StringView SourceId; public uint HasSource; public uint Reserved0; public long RemoteTimeNs; public uint HasRemoteTime; public uint Flags; public fixed ulong Reserved[4]; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct AlgorithmCallbacks { public uint StructSize; public uint AbiVersion; public IntPtr Setup; public IntPtr Step; public IntPtr Close; public fixed ulong Reserved[4]; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct RuntimeOptions { public uint StructSize; public uint AbiVersion; public StringView PackagePath; public StringView RuntimePath; public IntPtr Callbacks; public IntPtr UserData; public fixed ulong Reserved[4]; }
[StructLayout(LayoutKind.Sequential)] internal unsafe struct VersionInfo { public uint StructSize; public uint AbiVersion; public uint Major; public uint Minor; public uint Patch; public fixed uint Reserved[3]; }

internal sealed class PinnedStringView : IDisposable
{
    private GCHandle _handle;
    public StringView View { get; }
    private PinnedStringView(byte[] bytes) { _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned); View = new StringView { Data = _handle.AddrOfPinnedObject(), Length = (ulong)bytes.Length }; }
    public static PinnedStringView Pin(string value) => new(Encoding.UTF8.GetBytes(value));
    public void Dispose() { if (_handle.IsAllocated) _handle.Free(); }
}

internal sealed class PinnedBytesView : IDisposable
{
    private GCHandle _handle;
    public BytesView View { get; }
    private PinnedBytesView(byte[] bytes) { View = default; if (bytes.Length > 0) { _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned); View = new BytesView { Data = _handle.AddrOfPinnedObject(), Length = (ulong)bytes.Length }; } }
    public static PinnedBytesView Pin(ReadOnlySpan<byte> payload) => new(payload.ToArray());
    public void Dispose() { if (_handle.IsAllocated) _handle.Free(); }
}
