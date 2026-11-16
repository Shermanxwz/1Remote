using System;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Forms;
using _1RM.Model.Protocol;
using _1RM.Service;
using _1RM.View.Host.ProtocolHosts;
using Shawn.Utils;
using Stylet;
using ProtocolHostType = _1RM.View.Host.ProtocolHosts.ProtocolHostType;
using Timer = System.Timers.Timer;

namespace _1RM.View.Host
{
    public partial class TabWindowView
    {
        private readonly Timer _timer4CheckForegroundWindow = new Timer();

        private void TimerInitOnLoaded()
        {
            _timer4CheckForegroundWindow.Interval = 100;
            _timer4CheckForegroundWindow.AutoReset = false;
            _timer4CheckForegroundWindow.Elapsed += Timer4CheckForegroundWindowOnElapsed;
            _timer4CheckForegroundWindow.Start();
        }

        private void TimerDispose()
        {
            try
            {
                _timer4CheckForegroundWindow?.Dispose();
            }
            finally
            {
            }
        }

        private void Timer4CheckForegroundWindowOnElapsed(object? sender, ElapsedEventArgs e)
        {
            _timer4CheckForegroundWindow.Stop();
            try
            {
                RunForRdpV2();
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
            }
            finally
            {
                _timer4CheckForegroundWindow.Start();
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void FocusOnTabContent()
        {
            if (LauncherWindowViewModel.IsLauncherOpenOrOpening)
                return;

            try
            {
                var content = Vm?.SelectedItem?.Content;
                if (content == null)
                    return;
                if (content.GetProtocolHostType() == ProtocolHostType.Integrate)
                {
                    var exeHandle = GetHostHwndSafe(content);
                    var root = GetAncestor(exeHandle, GaRoot);
                    if (root != _tabWindowHandle)
                    {
                        SimpleLogHelper.Debug($"TabWindowView.FocusOnTabContent: skip focus, exe={exeHandle} still roots at {root} instead of tab={_tabWindowHandle}");
                        return;
                    }
                }
                content.FocusOnMe();
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
            }
        }

        private void FocusOnTabContentWhenMouseReleased()
        {
            if (LauncherWindowViewModel.IsLauncherOpenOrOpening)
                return;

            Dispatcher.InvokeAsync(async () =>
            {
                while (IsLoaded && System.Windows.Forms.Control.MouseButtons != MouseButtons.None)
                {
                    await System.Threading.Tasks.Task.Delay(50);
                }
                if (LauncherWindowViewModel.IsLauncherOpenOrOpening)
                    return;
                if (IsLoaded && GetForegroundWindow() == _tabWindowHandle)
                {
                    FocusOnTabContent();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        #region Foreground window change notification

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint OBJID_WINDOW = 0x0000;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private WinEventDelegate? _foregroundWinEventProc;
        private IntPtr _hForegroundWinEventHook;

        private void ForegroundWinEventInit()
        {
            if (_hForegroundWinEventHook != IntPtr.Zero)
                return;
            _foregroundWinEventProc = OnForegroundWinEvent;
            _hForegroundWinEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundWinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            HandleForegroundWindowChanged(GetForegroundWindow());
        }

        private void ForegroundWinEventDispose()
        {
            if (_hForegroundWinEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_hForegroundWinEventHook);
                _hForegroundWinEventHook = IntPtr.Zero;
            }
            _foregroundWinEventProc = null;
        }

        private void OnForegroundWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero)
                return;
            HandleForegroundWindowChanged(hwnd);
        }

        private void HandleForegroundWindowChanged(IntPtr foregroundHwnd)
        {
            if (_tabWindowHandle == IntPtr.Zero || foregroundHwnd == IntPtr.Zero)
                return;
            if (LauncherWindowViewModel.IsLauncherOpenOrOpening)
                return;

            try
            {
                var content = Vm?.SelectedItem?.Content;
                if (content == null)
                    return;
                var isIntegrate = content.GetProtocolHostType() == ProtocolHostType.Integrate;
                var exeHandle = GetHostHwndSafe(content);

                if (isIntegrate && foregroundHwnd == exeHandle)
                {
                    BringWindowToTop(_tabWindowHandle);
                    return;
                }

                if (foregroundHwnd == _tabWindowHandle)
                {
                    FocusOnTabContentWhenMouseReleased();
                }
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
            }
        }

        private static IntPtr GetHostHwndSafe(HostBase? host)
        {
            try
            {
                return host?.GetHostHwnd() ?? IntPtr.Zero;
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning($"Failed to get host hwnd: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        #endregion

        #region RunForRdp

        [StructLayout(LayoutKind.Sequential)]
        internal struct Win32Point
        {
            public Int32 X;
            public Int32 Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(ref Win32Point pt);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Win32Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        private const uint GaRoot = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private static bool IsMouseInside(Window window)
        {
            var w32Mouse = new Win32Point();
            GetCursorPos(ref w32Mouse);
            Point mousePos = new Point(w32Mouse.X, w32Mouse.Y);
            Point windowPos = new Point(-1, -1);
            Point windowBottomRight = new Point(-1, -1);
            IntPtr myHandle = IntPtr.Zero;
            Execute.OnUIThreadSync(() =>
            {
                if (!window.IsLoaded || window.WindowState == WindowState.Minimized) return;
                var w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
                var h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
                windowPos = window.PointToScreen(new Point(0, 0));
                windowBottomRight = window.PointToScreen(new Point(w, h));
                var helper = new System.Windows.Interop.WindowInteropHelper(window);
                myHandle = helper.Handle;
            });

            if (myHandle == IntPtr.Zero)
                return false;

            bool inRect = mousePos.X >= windowPos.X && mousePos.X <= windowBottomRight.X && mousePos.Y >= windowPos.Y && mousePos.Y <= windowBottomRight.Y;
            if (!inRect)
                return false;

            var hitWindow = WindowFromPoint(w32Mouse);
            if (hitWindow == IntPtr.Zero)
                return false;

            var hitRoot = GetAncestor(hitWindow, GaRoot);
            if (hitRoot != IntPtr.Zero && hitRoot != myHandle)
                return false;

            return true;
        }

        private void RunForRdpV2()
        {
            if (LauncherWindowViewModel.IsLauncherOpenOrOpening)
                return;
            if (Vm?.SelectedItem?.Content?.ProtocolServer.Protocol != RDP.ProtocolName)
                return;
            if (Vm?.SelectedItem?.Content?.Status != ProtocolHosts.ProtocolHostStatus.Connected)
                return;
            if (IoC.Get<ConfigurationService>().General.TabWindowSetFocusToLocalDesktopOnMouseLeaveRdpWindow)
            {
                bool isMousePressed = System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.Control.MouseButtons.Left
                                      || System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.Right
                                      || System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.Middle;
                var nowActivatedWindowHandle = GetForegroundWindow();
                IntPtr rdpHandle;
                if (Vm?.SelectedItem?.Content is AxMsRdpClient09Host)
                {
                    rdpHandle = _tabWindowHandle;
                }
                else
                {
                    return;
                }

                if (IsMouseInside(this))
                {
                    if (nowActivatedWindowHandle != rdpHandle && !LauncherWindowViewModel.IsLauncherOpenOrOpening)
                    {
                        SetForegroundWindow(rdpHandle);
                    }
                }
                else if (nowActivatedWindowHandle == rdpHandle && !isMousePressed)
                {
                    SetForegroundWindow(GetDesktopWindow());
                }
            }
        }

        #endregion
    }
}
