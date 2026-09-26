param(
    [Parameter(Mandatory = $true)]
    [int]$ParentProcessId,

    [Parameter(Mandatory = $true)]
    [long]$WindowHandle
)

while (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 250
}

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class DebugBrowserNativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam);
}
"@

$window = [IntPtr]::new($WindowHandle)

if ([DebugBrowserNativeMethods]::IsWindow($window)) {
    [DebugBrowserNativeMethods]::PostMessage(
        $window,
        0x0010,
        [IntPtr]::Zero,
        [IntPtr]::Zero
    )
}