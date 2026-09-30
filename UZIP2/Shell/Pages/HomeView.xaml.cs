using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.ViewModel;

namespace UZIP2.Shell.Pages
{
    public partial class HomeView : UserControl
    {
        private HomeViewModel Vm => DataContext as HomeViewModel;

        public HomeView()
        {
            InitializeComponent();
            DataContext = App.Services.GetRequiredService<HomeViewModel>();
            Loaded += (s, e) => ApplyModeToRadios();
        }

        void ApplyModeToRadios()
        {
            if (Vm == null) return;
            switch (Vm.Mode)
            {
                case 1: RbExtract.IsChecked = true; break;
                case 2: RbCompress.IsChecked = true; break;
                default: RbAuto.IsChecked = true; break;
            }
        }

        void OnModeChecked(object sender, RoutedEventArgs e)
        {
            if (Vm == null || !(sender is RadioButton rb) || rb.Tag == null) return;
            Vm.Mode = int.Parse((string)rb.Tag);
        }

        static string[] GetFiles(DragEventArgs e)
        {
            return e.Data.GetDataPresent(DataFormats.FileDrop)
                ? (string[])e.Data.GetData(DataFormats.FileDrop)
                : System.Array.Empty<string>();
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            Vm?.ShowPreview(GetFiles(e));
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDragLeave(object sender, DragEventArgs e)
        {
            Vm?.ClearPreview();
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            Vm?.DropFiles(GetFiles(e));
        }

        void OnOpen7zLocation(object sender, RoutedEventArgs e)
        {
            (Window.GetWindow(this) as MainWindow)?.NavigateToSettings();
        }
    }
}
