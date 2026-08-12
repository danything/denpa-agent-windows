using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Denpa.Agent;

/// <summary>
/// 通知領域 (タスクトレイ) に常駐して「起動中」を示す。BonDriver のツールと同じ流儀。
///
/// <para>
/// **WinForms も WPF も使わない。** Native AOT・依存ゼロ・単一 exe を崩さないため、
/// Win32 を直に叩く — メッセージ専用ウィンドウ (<c>HWND_MESSAGE</c>) を1枚作り、
/// <c>Shell_NotifyIcon</c> でアイコンを足して、そのスレッドでメッセージループを回す。
/// 右クリックで「状態を開く / 終了」。
/// </para>
///
/// <para>
/// アイコンとループは**同じスレッド**でないといけないので、専用スレッドを1本立てる。
/// 外から畳むときは <see cref="Stop"/> が終了メッセージを投げる。
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static unsafe partial class Tray
{
    private const uint WmDestroy = 0x0002;
    private const uint WmCommand = 0x0111;
    private const uint WmApp = 0x8000;
    private const uint TrayCallback = WmApp + 1;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonDblClk = 0x0203;

    private const uint NimAdd = 0x0000;
    private const uint NimDelete = 0x0002;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const int IdOpen = 1;
    private const int IdQuit = 2;

    private static nint _hwnd;
    private static int _port;
    private static Action? _onQuit;
    private static readonly nint TrayIconId = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    // blittable にするため szTip は fixed バッファ (managed string を混ぜると
    // source-generated P/Invoke が扱えず sizeof も取れない)
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassEx(ref WndClassEx wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nint id, string text);

    [LibraryImport("user32.dll", EntryPoint = "DestroyMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);

    // IDI_APPLICATION = 32512
    private static readonly nint IdiApplication = 32512;

    /// <summary>常駐を始める。**別 PC でも「動いているか」がひと目で分かる**ように</summary>
    public static void Start(int port, Action onQuit)
    {
        _port = port;
        _onQuit = onQuit;
        var thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>外から畳む (止まれの合図を受けたとき)。アイコンを消してループを終える</summary>
    public static void Stop()
    {
        if (_hwnd != 0) PostMessage(_hwnd, WmDestroy, 0, 0);
    }

    private static void Run()
    {
        try
        {
            var instance = GetModuleHandle(null);
            var className = "DenpaAgentTray";

            fixed (char* namePtr = className)
            {
                var wc = new WndClassEx
                {
                    cbSize = (uint)sizeof(WndClassEx),
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
                    hInstance = instance,
                    lpszClassName = (nint)namePtr,
                };
                RegisterClassEx(ref wc);
            }

            // HWND_MESSAGE (= -3) の子として、見えないウィンドウを作る
            _hwnd = CreateWindowEx(0, className, "denpa-agent", 0, 0, 0, 0, 0, -3, 0, instance, 0);
            if (_hwnd == 0)
            {
                Log.Write($"トレイを出せませんでした ({Marshal.GetLastPInvokeErrorMessage()})。常駐は続けます");
                return;
            }

            var data = new NotifyIconData
            {
                cbSize = (uint)sizeof(NotifyIconData),
                hWnd = _hwnd,
                uID = (uint)TrayIconId,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = TrayCallback,
                hIcon = LoadIcon(0, IdiApplication),
            };
            var tip = $"denpa-agent 起動中 (:{_port})";
            for (var i = 0; i < tip.Length && i < 127; i++) data.szTip[i] = tip[i];
            ShellNotifyIcon(NimAdd, ref data);

            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            ShellNotifyIcon(NimDelete, ref data);
        }
        catch (Exception error)
        {
            Log.Write($"トレイでエラー: {error.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case TrayCallback:
                // 右クリック・ダブルクリックの判定は lParam に入っている
                var evt = (uint)(lParam & 0xFFFF);
                if (evt == WmRButtonUp) ShowMenu(hwnd);
                else if (evt == WmLButtonDblClk) OpenStatus();
                return 0;

            case WmCommand:
                var id = (int)(wParam & 0xFFFF);
                if (id == IdOpen) OpenStatus();
                else if (id == IdQuit)
                {
                    _onQuit?.Invoke();
                    PostQuitMessage(0);
                }
                return 0;

            case WmDestroy:
                DestroyWindow(hwnd);
                PostQuitMessage(0);
                return 0;

            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static void ShowMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, IdOpen, "状態を開く");
        AppendMenu(menu, 0, IdQuit, "終了");
        GetCursorPos(out var point);
        // メニューの外を押しても閉じるように、いったん前面へ (Win32 の作法)
        SetForegroundWindow(hwnd);
        var chosen = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, hwnd, 0);
        DestroyMenu(menu);
        if (chosen != 0) PostMessage(hwnd, WmCommand, chosen, 0);
    }

    private static void OpenStatus()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://localhost:{_port}/denpa/card") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Log.Write($"状態ページを開けませんでした: {error.Message}");
        }
    }
}
