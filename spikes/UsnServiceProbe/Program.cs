using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PeerSync");
builder.Services.AddHostedService<UsnProbeWorker>();
await builder.Build().RunAsync();

internal sealed class UsnProbeWorker(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        const string output = @"C:\ProgramData\PeerSync\logs\service\usn-scm-probe.json";
        try
        {
            var result = new
            {
                identity = WindowsIdentity.GetCurrent().Name,
                volume = "C:",
                genericRead = Probe(0x80000000),
                fileDataRead = Probe(1),
                noAccess = Probe(0)
            };
            File.WriteAllText(output, JsonSerializer.Serialize(result));
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { error = error.GetType().Name }));
        }
        finally { lifetime.StopApplication(); }
    }

    private static object Probe(uint access)
    {
        IntPtr handle = Native.CreateFile(@"\\.\C:", access, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
            return new { open = false, error = Marshal.GetLastPInvokeError(), query = false, read = false };
        try
        {
            byte[] query = new byte[80];
            bool queried = Native.DeviceIoControl(handle, 0x000900F4, [], 0, query, (uint)query.Length,
                out uint returned, IntPtr.Zero);
            int queryError = queried ? 0 : Marshal.GetLastPInvokeError();
            if (!queried || returned < 56)
                return new { open = true, error = queryError, query = false, read = false };
            byte[] input = new byte[40];
            BitConverter.GetBytes(BitConverter.ToInt64(query, 8)).CopyTo(input, 0);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(input, 8);
            BitConverter.GetBytes(BitConverter.ToInt64(query, 0)).CopyTo(input, 32);
            byte[] records = new byte[64 * 1024];
            bool read = Native.DeviceIoControl(handle, 0x000900BB, input, (uint)input.Length,
                records, (uint)records.Length, out _, IntPtr.Zero);
            return new { open = true, error = read ? 0 : Marshal.GetLastPInvokeError(), query = true, read };
        }
        finally { Native.CloseHandle(handle); }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(IntPtr handle, uint control,
            byte[] input, uint inputLength, byte[] output, uint outputLength,
            out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
