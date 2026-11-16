using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using _1RM.Service;
using _1RM.Utils;
using _1RM.Utils.Tracing;
using _1RM.View.Launcher;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace _1RM.View
{
    public class LauncherWindowViewModel : NotifyPropertyChangedBaseScreen
    {
        public const double LAUNCHER_LIST_AREA_WIDTH = 400;
        public const double LAUNCHER_GRID_KEYWORD_HEIGHT = 46;
        public const double LAUNCHER_SERVER_LIST_ITEM_HEIGHT = 40;
        public const double LAUNCHER_ACTION_LIST_ITEM_HEIGHT = 34;
        public const double LAUNCHER_OUTLINE_CORNER_RADIUS = 8;
        public static readonly CornerRadius LauncherOutlineCornerRadiusObj = new CornerRadius(LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS);

        public const int MAX_SERVER_COUNT = 8;
        public const double MAX_SELECTION_HEIGHT = LAUNCHER_SERVER_LIST_ITEM_HEIGHT * MAX_SERVER_COUNT;
        public const double MAX_WINDOW_HEIGHT = LAUNCHER_GRID_KEYWORD_HEIGHT + MAX_SELECTION_HEIGHT;

        private static long _lastShowTimestampTicks = 0;
        private static volatile bool _isLauncherVisibleOrOpening = false;

        /// <summary>
        /// True while the launcher popup is visible or within the initial focus handoff grace period,
        /// preventing TabWindowView timers/WinEvent hooks from stealing foreground focus back.
        /// </summary>
        public static bool IsLauncherOpenOrOpening =>
            _isLauncherVisibleOrOpening ||
            (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastShowTimestampTicks)) < Stopwatch.Frequency / 2;

        public static void MarkOpeningGracePeriod()
        {
            Interlocked.Exchange(ref _lastShowTimestampTicks, Stopwatch.GetTimestamp());
            _isLauncherVisibleOrOpening = true;
        }

        public static void ResetOpeningStateForTest()
        {
            _isLauncherVisibleOrOpening = false;
            Interlocked.Exchange(ref _lastShowTimestampTicks, 0);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        private static void ForceForegroundWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            var foreHwnd = GetForegroundWindow();
            uint foreThread = foreHwnd != IntPtr.Zero ? GetWindowThreadProcessId(foreHwnd, IntPtr.Zero) : 0;
            uint appThread = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (foreThread != 0 && foreThread != appThread)
                {
                    attached = AttachThreadInput(foreThread, appThread, true);
                }
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            catch
            {
                // ignore Win32 foreground handoff errors
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(foreThread, appThread, false);
                }
            }
        }

        private Visibility _serverSelectionsViewVisibility = Visibility.Visible;
        public Visibility ServerSelectionsViewVisibility
        {
            get => _serverSelectionsViewVisibility;
            set => SetAndNotifyIfChanged(ref _serverSelectionsViewVisibility, value);
        }

        public ServerSelectionsViewModel ServerSelectionsViewModel { get; }
        public QuickConnectionViewModel QuickConnectionViewModel { get; }
        private readonly ConfigurationService _configurationService;

        private EventHandler? _deactivatedHandler;
        private KeyEventHandler? _keyDownHandler;

        private double _gridMainHeight;
        public double GridMainHeight
        {
            get => _gridMainHeight;
            set
            {
                if (SetAndNotifyIfChanged(ref _gridMainHeight, value))
                {
                    GridMainClip = new RectangleGeometry(new Rect(new Size(LAUNCHER_LIST_AREA_WIDTH, GridMainHeight)), LAUNCHER_OUTLINE_CORNER_RADIUS, LAUNCHER_OUTLINE_CORNER_RADIUS);
                }
            }
        }

        private RectangleGeometry? _gridMainClip = null;
        public RectangleGeometry? GridMainClip
        {
            get => _gridMainClip;
            set => SetAndNotifyIfChanged(ref _gridMainClip, value);
        }

        public double GridNoteHeight { get; }
        private double _noteWidth = 500;
        public double NoteWidth
        {
            get => _noteWidth;
            private set => SetAndNotifyIfChanged(ref _noteWidth, value);
        }

        public LauncherWindowViewModel(ServerSelectionsViewModel serverSelectionsViewModel, QuickConnectionViewModel quickConnectionViewModel, ConfigurationService configurationService)
        {
            ServerSelectionsViewModel = serverSelectionsViewModel;
            QuickConnectionViewModel = quickConnectionViewModel;
            _configurationService = configurationService;
            GridNoteHeight = MAX_WINDOW_HEIGHT;
        }

        protected override void OnViewLoaded()
        {
            HideMe();
            if (this.View is not LauncherWindowView { IsClosing: false }) return;
            ServerSelectionsViewVisibility = Visibility.Visible;
            ReSetWindowHeight();
        }

        public void SetHotKey()
        {
            if (this.View is LauncherWindowView { IsClosing: false } window)
            {
                Execute.OnUIThreadSync(() =>
                {
                    if (_deactivatedHandler != null)
                        window.Deactivated -= _deactivatedHandler;
                    if (_keyDownHandler != null)
                        window.KeyDown -= _keyDownHandler;

                    SetHotKey(_configurationService.Launcher.LauncherEnabled,
                        _configurationService.Launcher.HotKeyModifiers, _configurationService.Launcher.HotKeyKey);

                    _deactivatedHandler = (s, a) =>
                    {
                        var elapsedMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastShowTimestampTicks)) * 1000.0 / Stopwatch.Frequency;
                        if (elapsedMs < 350 && System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.None)
                        {
                            Execute.OnUIThread(() =>
                            {
                                if (_isLauncherVisibleOrOpening && window is { IsClosing: false, Visibility: Visibility.Visible })
                                {
                                    ForceForegroundWindow(new WindowInteropHelper(window).Handle);
                                    window.Activate();
                                    ServerSelectionsViewModel.Show();
                                }
                            });
                            return;
                        }
                        HideMe();
                    };
                    _keyDownHandler = (s, a) => { if (a.Key == Key.Escape) HideMe(); };

                    window.Deactivated += _deactivatedHandler;
                    window.KeyDown += _keyDownHandler;

                    ServerSelectionsViewModel.CalcNoteFieldVisibility();
                });
            }
        }

        public void ReSetWindowHeight()
        {
            if (IoC.TryGet<LauncherWindowView>()?.IsClosing != false) return;
            Execute.OnUIThread(() =>
            {
                var height = ServerSelectionsViewVisibility == Visibility.Visible ? ServerSelectionsViewModel.ReCalcGridMainHeight() : QuickConnectionViewModel.ReCalcGridMainHeight();
                GridMainHeight = height;
            });
        }

        public void ShowMe()
        {
            if (this.View is LauncherWindowView { IsClosing: false } window)
            {
                if (IoC.Get<MainWindowViewModel>().TopLevelViewModel != null) return;
                if (IoC.Get<ConfigurationService>().Launcher.LauncherEnabled == false) return;

                MarkOpeningGracePeriod();
                Execute.OnUIThreadSync(() =>
                {
                    lock (this)
                    {
                        window.WindowState = WindowState.Normal;
                        QuickConnectionViewModel.SelectedProtocol = QuickConnectionViewModel.Protocols.First();
                        GridMainHeight = ServerSelectionsViewVisibility == Visibility.Visible
                            ? ServerSelectionsViewModel.ReCalcGridMainHeight()
                            : QuickConnectionViewModel.ReCalcGridMainHeight();

                        var p = ScreenInfoEx.GetMouseSystemPosition();
                        var screenEx = ScreenInfoEx.GetCurrentScreenBySystemPosition(p);
                        var contentWidth = window.BorderMainContent.ActualWidth > 1
                            ? window.BorderMainContent.ActualWidth
                            : LAUNCHER_LIST_AREA_WIDTH + 20;
                        window.Top = screenEx.VirtualWorkingAreaCenter.Y - GridMainHeight / 2 - 40;
                        window.Left = screenEx.VirtualWorkingAreaCenter.X - contentWidth / 2;

                        var noteWidth = (screenEx.VirtualWorkingArea.Width - contentWidth - 100) / 2;
                        if (noteWidth < 100)
                            noteWidth = 100;
                        NoteWidth = Math.Min(noteWidth, NoteWidth);

                        window.Topmost = true;
                        window.Show();
                        window.Visibility = Visibility.Visible;
                        ForceForegroundWindow(new WindowInteropHelper(window).Handle);
                        window.Activate();
                        window.Focus();
                        ServerSelectionsViewModel.Show();

                        window.Dispatcher.InvokeAsync(() =>
                        {
                            if (_isLauncherVisibleOrOpening && window is { IsClosing: false, Visibility: Visibility.Visible })
                            {
                                ForceForegroundWindow(new WindowInteropHelper(window).Handle);
                                window.Activate();
                                if (ServerSelectionsViewModel.View is ServerSelectionsView ssv)
                                    ssv.TbKeyWord.Focus();
                            }
                        }, DispatcherPriority.Input);
                    }
                });
            }
            else if (this.View is not LauncherWindowView)
            {
                IoC.Get<IWindowManager>().ShowWindow(this);
            }
        }

        public void HideMe()
        {
            _isLauncherVisibleOrOpening = false;
            Interlocked.Exchange(ref _lastShowTimestampTicks, 0);
            if (this.View is not LauncherWindowView { IsClosing: false } window) return;
            lock (this)
            {
                Execute.OnUIThread(() =>
                {
                    _isLauncherVisibleOrOpening = false;
                    window.Hide();
                    QuickConnectionViewModel.Filter = "";
                    QuickConnectionViewModel.Show();
                    ServerSelectionsViewModel.Show();
                    System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet = System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet;
                });
            }
        }

        public bool SetHotKey(bool launcherEnabled, HotkeyModifierKeys hotKeyModifierKeys, Key hotKeyKey)
        {
            if (this.View is not LauncherWindowView window) return false;
            GlobalHotkeyHooker.Instance.Unregist(window);
            if (launcherEnabled == false)
                return false;
            var r = GlobalHotkeyHooker.Instance.Register(window, (uint)hotKeyModifierKeys, hotKeyKey, this.ShowMe);
            switch (r.Item1)
            {
                case GlobalHotkeyHooker.RetCode.Success:
                    return true;
                case GlobalHotkeyHooker.RetCode.ERROR_HOTKEY_NOT_REGISTERED:
                    {
                        var msg = $"{IoC.Translate("hotkey_registered_fail")}: {r.Item2}";
                        SimpleLogHelper.Warning(msg);
                        MessageBoxHelper.Warning(msg);
                        break;
                    }
                case GlobalHotkeyHooker.RetCode.ERROR_HOTKEY_ALREADY_REGISTERED:
                    {
                        var msg = $"{IoC.Translate("hotkey_already_registered")}: {r.Item2}";
                        SimpleLogHelper.Warning(msg);
                        MessageBoxHelper.Warning(msg);
                        break;
                    }
                default:
                    {
                        var msg = $"Failed to register hotkey {hotKeyModifierKeys} + {hotKeyKey}, error code: {r.Item1}";
                        SimpleLogHelper.Warning(msg);
                        MessageBoxHelper.Warning(msg);
                        UnifyTracing.Error(new Exception(msg));
                        break;
                    }
            }
            return false;
        }

        public void ToggleQuickConnection()
        {
            if (ServerSelectionsViewVisibility == Visibility.Collapsed)
                ServerSelectionsViewModel.Show();
            else
                QuickConnectionViewModel.Show();
            ServerSelectionsViewModel.CalcNoteFieldVisibility();
            ReSetWindowHeight();
        }
    }
}
