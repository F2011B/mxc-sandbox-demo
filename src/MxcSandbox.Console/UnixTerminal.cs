using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Mxc.Sdk.V1;

namespace MxcSandbox.ConsoleHost;

internal sealed class UnixTerminal : IDisposable
{
    private readonly IntPtr saved = Marshal.AllocHGlobal(256);
    private bool changed;
    public UnixTerminal()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (isatty(0) != 1 || isatty(1) != 1) throw new IOException("Die Sandbox-Konsole benötigt ein echtes Terminal.");
        // Oversized opaque storage; libc owns the platform-specific termios layout.
        Marshal.Copy(new byte[256], 0, saved, 256);
        if (tcgetattr(0, saved) != 0) throw new IOException("Terminalmodus konnte nicht gelesen werden.");
        var raw = Marshal.AllocHGlobal(256);
        try
        {
            var bytes = new byte[256]; Marshal.Copy(saved, bytes, 0, bytes.Length); Marshal.Copy(bytes, 0, raw, bytes.Length);
            cfmakeraw(raw);
            if (tcsetattr(0, 0, raw) != 0) throw new IOException("Terminalmodus konnte nicht gesetzt werden.");
            changed = true;
        }
        finally { Marshal.FreeHGlobal(raw); }
    }
    public static Stream OpenInput(CancellationToken stop) => new DescriptorStream(0, true, stop);
    public static Stream OpenOutput() => new DescriptorStream(1, false, CancellationToken.None);
    public static MxcPtySize Size()
    {
        try
        {
            var rows = Console.WindowHeight; var columns = Console.WindowWidth;
            return rows < 2 || columns < 10 ? MxcPtySize.Default : new MxcPtySize((ushort)Math.Min(rows, 500), (ushort)Math.Min(columns, 1000));
        }
        catch (IOException) { return MxcPtySize.Default; }
    }
    public void Dispose()
    {
        if (changed) { tcsetattr(0, 0, saved); changed = false; }
        Marshal.FreeHGlobal(saved);
    }
    private sealed class DescriptorStream(int fd, bool input, CancellationToken stop) : Stream
    {
        private volatile bool disposed;
        public override bool CanRead => input; public override bool CanWrite => !input; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!input) throw new NotSupportedException();
            while (!disposed)
            {
                stop.ThrowIfCancellationRequested();
                var descriptor = new PollDescriptor { Fd = fd, Events = 1 };
                var ready = poll(ref descriptor, 1, 100);
                if (ready < 0) { if (Marshal.GetLastWin32Error() == 4) continue; throw new IOException("Terminal-poll fehlgeschlagen."); }
                if (ready == 0 || disposed) continue;
                var bytes = new byte[count]; var received = read(fd, bytes, (nuint)count);
                if (received < 0) { if (Marshal.GetLastWin32Error() == 4) continue; throw new IOException("Terminal-read fehlgeschlagen."); }
                Array.Copy(bytes, 0, buffer, offset, (int)received); return (int)received;
            }
            return 0;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (input) throw new NotSupportedException();
            while (count > 0)
            {
                var bytes = buffer.AsSpan(offset, count).ToArray(); var sent = write(fd, bytes, (nuint)count);
                if (sent <= 0) { if (Marshal.GetLastWin32Error() == 4) continue; throw new IOException("Terminal-write fehlgeschlagen."); }
                offset += (int)sent; count -= (int)sent;
            }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { public int Fd; public short Events, Returned; }
    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, [Out] byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int isatty(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int fd, IntPtr data);
    [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int fd, int action, IntPtr data);
    [DllImport("libc")] private static extern void cfmakeraw(IntPtr data);
}
