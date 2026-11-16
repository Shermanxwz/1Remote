using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using _1RM.View.Host;
using _1RM.View.Host.ProtocolHosts;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using Stylet;

namespace _1RM.Service
{
    public partial class SessionControlService
    {
        public void MarkTabWindowActive(string token)
        {
            if (!string.IsNullOrEmpty(token))
            {
                lock (_dictLock)
                {
                    _lastTabToken = token;
                }
            }
        }

        public void AddTab(TabWindowView tab)
        {
            lock (_dictLock)
            {
                var token = tab.Token;
                Debug.Assert(!_token2TabWindows.ContainsKey(token));
                Debug.Assert(!string.IsNullOrEmpty(token));
                _token2TabWindows.TryAdd(token, tab);
                tab.Activated += (sender, args) =>
                    _lastTabToken = tab.Token;
            }
        }

        private FullScreenWindowView MoveToExistedFullScreenWindow(HostBase host, TabWindowView? fromTab)
        {
            var full = _connectionId2FullScreenWindows[host.ConnectionId];
            full.LastTabToken = "";
            if (fromTab != null)
            {
                var screenEx = ScreenInfoEx.GetCurrentScreen(fromTab);
                full.Top = screenEx.VirtualWorkingAreaCenter.Y - full.Height / 2;
                full.Left = screenEx.VirtualWorkingAreaCenter.X - full.Width / 2;
                full.LastTabToken = _lastTabToken;
            }
            full.ShowOrHide(host);
            return full;
        }

        private FullScreenWindowView MoveToNewFullScreenWindow(HostBase host, TabWindowView? fromTab)
        {
            var full = FullScreenWindowView.Create(fromTab?.Token ?? "", host, fromTab);
            full.ShowOrHide(host);
            _connectionId2FullScreenWindows.TryAdd(host.ConnectionId, full);
            return full;
        }

        public void MoveSessionToFullScreen(string connectionId)
        {
            if (!_connectionId2Hosts.ContainsKey(connectionId))
                throw new NullReferenceException($"can not find host by connectionId = `{connectionId}`");

            var host = _connectionId2Hosts[connectionId];
            var tab = GetTabByConnectionId(connectionId);
            if (tab != null)
            {
                if (tab.IsLoaded == false)
                    return;

                tab.GetViewModel().TryRemoveItem(connectionId);
            }

            if (_connectionId2FullScreenWindows.TryGetValue(connectionId, out var cachedFull) && cachedFull.IsClosed)
            {
                _connectionId2FullScreenWindows.TryRemove(connectionId, out _);
            }
            var full = _connectionId2FullScreenWindows.ContainsKey(connectionId)
                ? this.MoveToExistedFullScreenWindow(host, tab)
                : this.MoveToNewFullScreenWindow(host, tab);

            this.CleanupProtocolsAndWindows();
            PrintCacheCount();
        }

        public void MoveSessionToTabWindow(string connectionId)
        {
            Debug.Assert(_connectionId2Hosts.ContainsKey(connectionId) == true);
            var host = _connectionId2Hosts[connectionId];
            TabWindowView? tab;

            lock (_dictLock)
            {
                if (host.ParentWindow is FullScreenWindowView full)
                {
                    if (full.IsLoaded == false)
                        return;

                    var lastTabToken = full.LastTabToken ?? "";
                    tab = this.GetOrCreateTabWindow(_token2TabWindows.ContainsKey(lastTabToken) ? lastTabToken : "");
                    if (tab.IsClosed)
                        tab = this.GetOrCreateTabWindow();

                    full.ShowOrHide(null);
                }
                else
                    tab = this.GetOrCreateTabWindow();
            }

            if (tab.GetViewModel().Items.All(x => x.Content != host))
            {
                tab.GetViewModel().AddItem(new TabItemViewModel(host, host.ProtocolServer.DisplayName));
            }
            else
            {
                tab.GetViewModel().SelectedItem = tab.GetViewModel().Items.First(x => x.Content == host);
            }
            tab.Activate();
            PrintCacheCount();
        }

        private static bool IsUsableScreen(System.Windows.Forms.Screen screen)
        {
            if (screen == null)
                return false;

            try
            {
                return screen.Bounds.Width > 0
                    && screen.Bounds.Height > 0
                    && screen.WorkingArea.Width > 0
                    && screen.WorkingArea.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryMoveTabWindowToVisibleScreen(TabWindowView tab)
        {
            var targetScreen = System.Windows.Forms.Screen.AllScreens
                .Where(IsUsableScreen)
                .OrderBy(screen => screen.Primary ? 0 : 1)
                .ThenBy(screen => screen.Bounds.Left)
                .FirstOrDefault();

            if (targetScreen == null)
                return false;

            if (tab.WindowState == WindowState.Minimized)
                tab.WindowState = WindowState.Normal;

            var width = Math.Max(tab.ActualWidth > 0 ? tab.ActualWidth : tab.Width, 1);
            var height = Math.Max(tab.ActualHeight > 0 ? tab.ActualHeight : tab.Height, 1);
            var workingArea = new ScreenInfoEx(targetScreen).VirtualWorkingArea;

            tab.Left = workingArea.X + Math.Max(0, (workingArea.Width - width) / 2.0);
            tab.Top = workingArea.Y + Math.Max(0, (workingArea.Height - height) / 2.0);
            return true;
        }

        private static bool IsUsableTabWindow(TabWindowView tab)
        {
            if (tab == null || tab.IsClosed)
                return false;
            if (!tab.IsLoaded)
                return true;

            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(tab).Handle;
                if (hwnd == IntPtr.Zero)
                    return false;

                var bounds = tab.WindowState == WindowState.Minimized
                    ? tab.RestoreBounds
                    : new Rect(tab.Left, tab.Top, tab.Width, tab.Height);

                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return false;

                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    if (!IsUsableScreen(screen))
                        continue;

                    var workingArea = new ScreenInfoEx(screen).VirtualWorkingArea;
                    var workingAreaRect = new Rect(workingArea.X, workingArea.Y, workingArea.Width, workingArea.Height);
                    var intersection = Rect.Intersect(bounds, workingAreaRect);
                    if (!intersection.IsEmpty && intersection.Width > 0 && intersection.Height > 0)
                        return true;
                }

                return TryMoveTabWindowToVisibleScreen(tab);
            }
            catch
            {
                return false;
            }
        }

        private void PruneStaleTabWindows()
        {
            foreach (var kv in _token2TabWindows.ToArray())
            {
                var key = kv.Key;
                var tab = kv.Value;
                if (tab.IsClosed)
                {
                    _token2TabWindows.TryRemove(key, out _);
                    continue;
                }
                if (!IsUsableTabWindow(tab) && tab.GetViewModel().Items.Count == 0)
                {
                    _token2TabWindows.TryRemove(key, out _);
                }
            }
        }

        private TabWindowView GetOrCreateTabWindow(string assignTabToken = "", bool preferLastOpenedDisplayWhenNew = false)
        {
            TabWindowView? ret = null;
            lock (_dictLock)
            {
                PruneStaleTabWindows();

                if (_token2TabWindows.ContainsKey(assignTabToken))
                {
                    ret = _token2TabWindows[assignTabToken];
                    if (ret == null || ret.IsClosed)
                    {
                        _token2TabWindows.TryRemove(assignTabToken, out _);
                        ret = null;
                    }
                    else if (!IsUsableTabWindow(ret) && ret.GetViewModel().Items.Count == 0)
                    {
                        _token2TabWindows.TryRemove(assignTabToken, out _);
                        ret = null;
                    }
                    else if (!IsUsableTabWindow(ret))
                    {
                        ret = null;
                    }
                }
                else if (string.IsNullOrEmpty(assignTabToken))
                {
                    if (_token2TabWindows.ContainsKey(_lastTabToken))
                    {
                        var last = _token2TabWindows[_lastTabToken];
                        if (last.IsClosed)
                        {
                            _token2TabWindows.TryRemove(_lastTabToken, out _);
                        }
                        else if (!IsUsableTabWindow(last) && last.GetViewModel().Items.Count == 0)
                        {
                            _token2TabWindows.TryRemove(_lastTabToken, out _);
                        }
                        else if (IsUsableTabWindow(last))
                        {
                            ret = last;
                        }
                    }

                    if (ret == null && _token2TabWindows.IsEmpty == false)
                    {
                        ret = _token2TabWindows.Values.FirstOrDefault(IsUsableTabWindow);
                    }
                }

                if (ret == null)
                {
                    Execute.OnUIThreadSync(() =>
                    {
                        ret = new TabWindowView();
                        ret.PrepareStartupDisplay(preferLastOpenedDisplayWhenNew);
                        AddTab(ret);
                        ret.Show();
                        ret.ShowInTaskbar = true;
                        _lastTabToken = ret.Token;

                        int loopCount = 0;
                        while (ret.IsLoaded == false)
                        {
                            ++loopCount;
                            Thread.Sleep(100);
                            if (loopCount > 50)
                                break;
                        }
                    });
                }
                Debug.Assert(ret != null);
                return ret!;
            }
        }

        public TabWindowView? GetTabByConnectionId(string connectionId)
        {
            lock (_dictLock)
                return _token2TabWindows.Values.FirstOrDefault(x => x.GetViewModel().Items.Any(y => y.Content.ConnectionId == connectionId));
        }
    }
}
