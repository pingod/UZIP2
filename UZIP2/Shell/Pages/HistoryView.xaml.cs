using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.ViewModel;

namespace UZIP2.Shell.Pages
{
    public partial class HistoryView : UserControl
    {
        private HistoryViewModel Vm => DataContext as HistoryViewModel;

        public HistoryView()
        {
            InitializeComponent();
            DataContext = App.Services.GetRequiredService<HistoryViewModel>();
        }

        void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (Vm != null) Vm.SearchText = SearchBox.Text ?? "";
        }

        void OnClear(object sender, RoutedEventArgs e)
        {
            if (Vm == null || Vm.Rows.Count == 0) return;
            var r = MessageBox.Show("确定清空全部历史？此操作会删除 Config\\history.json 里的记录。",
                "UZIP 历史", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.OK) Vm.ClearCommand.Execute(null);
        }

        void OnRetry(object sender, RoutedEventArgs e)
        {
            var row = (HistoryViewModel.HistoryRow)((FrameworkElement)sender).DataContext;
            Vm?.RetryCommand.Execute(row);
            // 重跑已入队，切到主页让用户看到进度
            Vm?.RefreshCommand.Execute(null);
        }
    }
}
