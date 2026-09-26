using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteelPans.WebApp.Services;

public sealed class DebugBrowserLauncher : IDisposable
{
    private const string FirefoxPath =
        @"C:\Program Files\Mozilla Firefox\firefox.exe";

    private const byte VkControl = 0x11;
    private const byte VkShift = 0x10;
    private const byte VkM = 0x4D;

    private const uint KeyEventKeyUp = 0x0002;
    private const uint WmClose = 0x0010;

    private readonly Lock lock_ = new();
    private readonly CancellationTokenSource cts_ = new();

    private IntPtr firefoxWindow_;
    private bool disposed_;

    public void Configure(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = LaunchAsync(app, cts_.Token);
        });

        app.Lifetime.ApplicationStopping.Register(Close);
    }

    private async Task LaunchAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = app.Configuration["ASPNETCORE_URLS"]?
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(x => x.StartsWith(
                    "https://",
                    StringComparison.OrdinalIgnoreCase));

            if (url is null)
                return;

            var existingWindows = GetFirefoxWindows();

            Process.Start(new ProcessStartInfo
            {
                FileName = FirefoxPath,
                Arguments = $"-new-window \"{url}\"",
                UseShellExecute = true
            });

            var window = await FindNewFirefoxWindowAsync(
                existingWindows,
                cancellationToken);

            if (window == IntPtr.Zero)
                return;

            lock (lock_)
            {
                if (disposed_)
                {
                    PostMessage(
                        window,
                        WmClose,
                        IntPtr.Zero,
                        IntPtr.Zero);

                    return;
                }

                firefoxWindow_ = window;
            }

            StartWatchdog(app, window);

            _ = MonitorWindowAsync(
                app,
                window,
                cancellationToken);

            // Give Firefox time to finish initializing before sending
            // the Responsive Design Mode shortcut.
            await Task.Delay(500, cancellationToken);

            SendResponsiveDesignShortcut(window);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<IntPtr> FindNewFirefoxWindowAsync(
        HashSet<IntPtr> existingWindows,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 50; ++i)
        {
            var windows = GetFirefoxWindows();

            var window = windows.FirstOrDefault(x =>
                !existingWindows.Contains(x));

            if (window != IntPtr.Zero)
                return window;

            await Task.Delay(100, cancellationToken);
        }

        return IntPtr.Zero;
    }

    private static async Task MonitorWindowAsync(
        WebApplication app,
        IntPtr window,
        CancellationToken cancellationToken)
    {
        try
        {
            while (IsWindow(window))
            {
                await Task.Delay(250, cancellationToken);
            }

            if (!cancellationToken.IsCancellationRequested)
                app.Lifetime.StopApplication();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static HashSet<IntPtr> GetFirefoxWindows()
    {
        var windows = new HashSet<IntPtr>();

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
                return true;

            var className = GetWindowClassName(window);

            if (string.Equals(
                    className,
                    "MozillaWindowClass",
                    StringComparison.Ordinal))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var builder = new StringBuilder(256);

        var length = GetClassName(
            window,
            builder,
            builder.Capacity);

        return length > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static void SendResponsiveDesignShortcut(IntPtr window)
    {
        if (!IsWindow(window))
            return;

        SetForegroundWindow(window);

        Thread.Sleep(100);

        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(VkShift, 0, 0, UIntPtr.Zero);
        keybd_event(VkM, 0, 0, UIntPtr.Zero);

        keybd_event(VkM, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkShift, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static void StartWatchdog(
        WebApplication app,
        IntPtr window)
    {
        var script = Path.Combine(
            app.Environment.ContentRootPath,
            "Properties",
            "DebugBrowserWatcher.ps1");

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass " +
                $"-File \"{script}\" " +
                $"-ParentProcessId {Environment.ProcessId} " +
                $"-WindowHandle {window.ToInt64()}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public void Close()
    {
        IntPtr window;

        lock (lock_)
        {
            window = firefoxWindow_;
            firefoxWindow_ = IntPtr.Zero;
        }

        if (window == IntPtr.Zero)
            return;

        if (!IsWindow(window))
            return;

        PostMessage(
            window,
            WmClose,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    public void Dispose()
    {
        lock (lock_)
        {
            if (disposed_)
                return;

            disposed_ = true;
        }

        cts_.Cancel();

        Close();

        cts_.Dispose();
    }

    private delegate bool EnumWindowsProc(
        IntPtr hWnd,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProc lpEnumFunc,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(
        IntPtr hWnd);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetClassName(
        IntPtr hWnd,
        StringBuilder lpClassName,
        int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte bVk,
        byte bScan,
        uint dwFlags,
        UIntPtr dwExtraInfo);
}