using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Timers;
using System.Windows;
using System.Windows.Interop;
using Dragablz;
using _1RM.Service;
using _1RM.Utils;
using _1RM.Utils.WindowsApi;
using _1RM.View.Host.ProtocolHosts;
using _1RM.View.Settings;
using _1RM.View.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace _1RM.View.Host
{
    public class TabWindowViewModel : MaskLayerContainerScreenBase, IDisposable
    {
        public readonly string Token;
        public new TabWindowView View { get; private set; }
        public SettingsPageViewModel SettingsPage => IoC.Get<SettingsPageViewModel>();

        private IntPtr _hWndTabContent = IntPtr.Zero;
        private readonly Timer _timer_ObserveTabSwitching = new Timer(5);
        private ElapsedEventHandler _awaitTabSwitchingHandler = null!;
        private int _timer_Count = 0;

        private void InitTabSwitchingTimer()
        {
            _timer_ObserveTabSwitching.AutoReset = false;
            _awaitTabSwitchingHandler = (sender, args) => AwaitTabSwitching();
            _timer_ObserveTabSwitching.Elapsed += _awaitTabSwitchingHandler;
        }

        public TabWindowViewModel(TabWindowView windowView)
        {
            View = windowView;
            Token = DateTime.Now.Ticks.ToString();
            Items.CollectionChanged += ItemsOnCollectionChanged;
            InitTabSwitchingTimer();
        }

        private void ItemsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RaisePropertyChanged(nameof(BtnCloseAllVisibility));
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<TabItemViewModel>())
                {
                    item.Content.SetParentWindow(View);
                }
            }
            if (Items.Count == 0)
            {
                View.Hide();
            }
        }

        public void Dispose()
        {
            _timer_ObserveTabSwitching.Elapsed -= _awaitTabSwitchingHandler;
            _timer_ObserveTabSwitching.Stop();
            _timer_ObserveTabSwitching.Dispose();
            Execute.OnUIThread(() =>
            {
                SelectedItem = null;
                foreach (var item in Items.ToArray())
                {
                    if (item.Content is IDisposable dp)
                    {
                        dp.Dispose();
                    }
                }
                Items.CollectionChanged -= ItemsOnCollectionChanged;
                Items.Clear();
            });
        }

        private string _title = "";
        public string Title
        {
            get => _title;
            set => SetAndNotifyIfChanged(ref _title, value);
        }

        public ResizeMode WindowResizeMode
        {
            get
            {
                if (SelectedItem?.Content == null || SelectedItem.Content.CanResizeNow() == false)
                    return ResizeMode.NoResize;
                return ResizeMode.CanResize;
            }
        }

        public ObservableCollection<TabItemViewModel> Items { get; } = new ObservableCollection<TabItemViewModel>();
        public Visibility BtnCloseAllVisibility => Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        private TabItemViewModel? _selectedItem = null;
        public TabItemViewModel? SelectedItem
        {
            get => _selectedItem;
            set
            {
                _timer_ObserveTabSwitching.Stop();
                _hWndTabContent = GetCurrentTabContentWindow();

                if (_selectedItem != null)
                {
                    _selectedItem.Content.OnCanResizeNowChanged -= OnCanResizeNowChanged;
                }

                var old = _selectedItem;
                if (SetAndNotifyIfChanged(ref _selectedItem, value))
                {
                    if (old != null)
                        old.PropertyChanged -= SelectedItemOnPropertyChanged;
                    if (_selectedItem != null)
                        _selectedItem.PropertyChanged += SelectedItemOnPropertyChanged;
                    if (_selectedItem != null)
                    {
                        SetTitle();
                        _selectedItem.Content.OnCanResizeNowChanged += OnCanResizeNowChanged;
                        _timer_Count = 40;
                        _timer_ObserveTabSwitching.Start();
                    }
                    foreach (var item in Items)
                    {
                        if (item is TabItemViewModel { Content: IntegrateHost ih })
                        {
                            ih.ShowWindow(item == value);
                        }
                    }
                    RaisePropertyChanged(nameof(WindowResizeMode));
                }
            }
        }

        private IntPtr GetCurrentTabContentWindow()
        {
            IntPtr hWndParent = new WindowInteropHelper(View).Handle;
            if (hWndParent == IntPtr.Zero)
                return IntPtr.Zero;
            IntPtr last = IntPtr.Zero;
            do
            {
                IntPtr hWnd = Win32Api.FindWindowEx(hWndParent, last, null, "");
                if (hWnd != IntPtr.Zero && Win32Api.IsWindowVisible(hWnd))
                {
                    return hWnd;
                }
                last = hWnd;
            } while (last != IntPtr.Zero);
            return IntPtr.Zero;
        }

        private void AwaitTabSwitching()
        {
            if (_timer_Count <= 0 || LauncherWindowViewModel.IsLauncherOpenOrOpening) return;
            _timer_Count--;
            Execute.OnUIThreadSync(() =>
            {
                if (LauncherWindowViewModel.IsLauncherOpenOrOpening) return;
                var hWnd = GetCurrentTabContentWindow();
                if (hWnd == IntPtr.Zero || hWnd == _hWndTabContent) _timer_ObserveTabSwitching.Start();
                else Win32Api.SetForegroundWindow(hWnd);
            });
        }

        private void SelectedItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabItemViewModel.DisplayName))
                SetTitle();
        }

        private void OnCanResizeNowChanged()
        {
            RaisePropertyChanged(nameof(WindowResizeMode));
        }

        public IInterTabClient InterTabClient { get; } = new InterTabClient();

        private void SetTitle()
        {
            if (SelectedItem != null)
            {
                this.Title = SelectedItem.DisplayName + " - " + Assert.APP_DISPLAY_NAME;
            }
        }

        public bool TryRemoveItem(string connectionId)
        {
            var item = Items.FirstOrDefault(x => x.Content.ConnectionId == connectionId);
            if (item != null)
            {
                Execute.OnUIThreadSync(() =>
                {
                    Items.Remove(item);
                    if (item is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    SelectedItem = Items.FirstOrDefault();
                });
                return true;
            }
            return false;
        }

        public void AddItem(TabItemViewModel newItem)
        {
            if (Items.Any(x => x.Content?.ConnectionId == newItem.Content.ConnectionId))
            {
                SelectedItem = Items.First(x => x.Content.ConnectionId == newItem.Content.ConnectionId);
                return;
            }
            Items.Add(newItem);
            newItem.Content.SetParentWindow(View);
            SelectedItem = newItem;
        }

        public TabItemViewModel? GetItem(string connectionId)
        {
            return Items.FirstOrDefault(x => x.Content.ConnectionId == connectionId);
        }

        private RelayCommand? _cmdHostGoFullScreen;
        public RelayCommand CmdHostGoFullScreen => _cmdHostGoFullScreen ??= new RelayCommand((o) =>
        {
            if (this.SelectedItem?.Content?.CanResizeNow() ?? false)
                IoC.Get<SessionControlService>().MoveSessionToFullScreen(SelectedItem.Content.ConnectionId);
        }, o => this.SelectedItem != null && (this.SelectedItem.Content?.CanFullScreen ?? false));

        private RelayCommand? _cmdInvokeLauncher;
        public RelayCommand CmdInvokeLauncher => _cmdInvokeLauncher ??= new RelayCommand((o) =>
        {
            _timer_ObserveTabSwitching.Stop();
            _timer_Count = 0;
            IoC.Get<SessionControlService>().MarkTabWindowActive(Token);
            IoC.Get<LauncherWindowViewModel>().ShowMe();
        }, o => this.SelectedItem != null);

        private RelayCommand? _cmdShowTabByIndex;
        public RelayCommand CmdShowTabByIndex => _cmdShowTabByIndex ??= new RelayCommand((o) =>
        {
            if (int.TryParse(o?.ToString() ?? "0", out int i))
            {
                if (i > 0 && i <= Items.Count)
                    SelectedItem = Items[i - 1];
            }
        }, o => this.SelectedItem != null);

        private RelayCommand? _cmdGoMinimize;
        public RelayCommand CmdGoMinimize => _cmdGoMinimize ??= new RelayCommand((_) =>
        {
            if (this.View is Window window)
                window.WindowState = WindowState.Minimized;
        });

        private RelayCommand? _cmdGoMaximize;
        public RelayCommand CmdGoMaximize => _cmdGoMaximize ??= new RelayCommand((o) =>
        {
            if (View.WindowState != WindowState.Maximized)
            {
                View.WindowState = WindowState.Maximized;
            }
            else
            {
                View.WindowStyle = WindowStyle.SingleBorderWindow;
                View.WindowState = WindowState.Normal;
            }
        });

        private RelayCommand? _cmdGoMaximizeF11;
        public RelayCommand CmdGoMaximizeF11 => _cmdGoMaximizeF11 ??= new RelayCommand((o) =>
        {
            if (View.WindowState != WindowState.Maximized)
            {
                View.WindowStyle = WindowStyle.None;
                View.WindowState = WindowState.Maximized;
            }
            else
            {
                View.WindowStyle = WindowStyle.SingleBorderWindow;
                View.WindowState = WindowState.Normal;
            }
        });

        private readonly object _canCmdClose = new object();
        private RelayCommand? _cmdCloseAll;
        public RelayCommand CmdCloseAll => _cmdCloseAll ??= new RelayCommand((o) =>
        {
            if (this.Items.Count <= 0 || App.ExitingFlag != false) return;
            lock (_canCmdClose)
            {
                if (this.Items.Count <= 0 || App.ExitingFlag != false) return;
                if (IoC.Get<ConfigurationService>().General.ConfirmBeforeClosingSession == true
                    && false == MessageBoxHelper.Confirm(IoC.Translate("Are you sure you want to close the connection?"), ownerViewModel: this))
                {
                }
                else
                {
                    IoC.Get<SessionControlService>().CloseProtocolHostAsync(Items.Select(x => x.Content.ConnectionId).ToArray());
                }
            }
        });

        private RelayCommand? _cmdClose;
        public RelayCommand CmdClose => _cmdClose ??= new RelayCommand((o) =>
        {
            lock (_canCmdClose)
            {
                if (IoC.Get<ConfigurationService>().General.ConfirmBeforeClosingSession == true
                    && App.ExitingFlag == false
                    && false == MessageBoxHelper.Confirm(IoC.Translate("Are you sure you want to close the connection?"), ownerViewModel: this))
                {
                }
                else
                {
                    HostBase? host = o is string connectionId
                        ? Items.FirstOrDefault(x => x.Content.ConnectionId == connectionId)?.Content
                        : SelectedItem?.Content;

                    if (host != null)
                        IoC.Get<SessionControlService>().CloseProtocolHostAsync(host.ConnectionId);
                }
            }
        }, o => this.SelectedItem != null);
    }

    public class InterTabClient : IInterTabClient
    {
        public INewTabHost<Window> GetNewHost(IInterTabClient interTabClient, object partition, TabablzControl source)
        {
            var v = new TabWindowView
            {
                IsCreatedByDragDetach = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            IoC.Get<SessionControlService>().AddTab(v);
            return new NewTabHost<Window>(v, v.TabablzControl);
        }

        public TabEmptiedResponse TabEmptiedHandler(TabablzControl tabControl, Window window)
        {
            if (window is TabWindowView tab)
            {
                tab.GetViewModel().Items.Clear();
                IoC.Get<SessionControlService>().CleanupProtocolsAndWindows();
            }
            return TabEmptiedResponse.CloseWindowOrLayoutBranch;
        }
    }
}
