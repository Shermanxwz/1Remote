using System.Windows;
using _1RM.Service;
using _1RM.View.Utils;
using Shawn.Utils.Wpf;

namespace _1RM.View
{
    public class RequestRatingViewModel : MaskLayer
    {
        public bool DoNotShowAgain { get; set; } = true;
        public Visibility RequestRatingPopupVisibility { get; } = Visibility.Collapsed;

        private RelayCommand? _cmdClose;
        public RelayCommand CmdClose => _cmdClose ??= new RelayCommand((_) =>
        {
            if (IoC.TryGet<MainWindowViewModel>() is { } mvm)
            {
                mvm.TopLevelViewModel = null;
                mvm.HideMe();
            }
        });
    }
}
