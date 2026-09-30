using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.ViewModel;

namespace UZIP2.Shell.Pages
{
    public partial class PasswordBookView : UserControl
    {
        private PasswordBookViewModel Vm => DataContext as PasswordBookViewModel;
        private PasswordBookViewModel.PasswordRow _editingRow;

        public PasswordBookView()
        {
            InitializeComponent();
            DataContext = App.Services.GetRequiredService<PasswordBookViewModel>();
            Vm.PropertyChanged += OnVmChanged;
            RefreshList();
        }

        void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PasswordBookViewModel.VisibleRows))
                RefreshList();
        }

        void RefreshList()
        {
            BookList.ItemsSource = Vm?.VisibleRows;
        }

        void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (Vm != null) Vm.SearchText = SearchBox.Text ?? "";
        }

        // ---- 添加 / 编辑 ----

        void OnAddBook(object sender, RoutedEventArgs e)
        {
            _editingRow = null;
            EditTitle.Text = "添加密码";
            EditName.Text = "";
            EditPlain.Text = "";
            EditOverlay.Visibility = Visibility.Visible;
            EditName.Focus();
        }

        void OnEdit(object sender, RoutedEventArgs e)
        {
            var row = (PasswordBookViewModel.PasswordRow)((FrameworkElement)sender).DataContext;
            _editingRow = row;
            EditTitle.Text = "编辑密码";
            EditName.Text = row.Name;
            EditPlain.Text = row.Entry.Text;
            EditOverlay.Visibility = Visibility.Visible;
            EditPlain.Focus();
        }

        void OnDelete(object sender, RoutedEventArgs e)
        {
            var row = (PasswordBookViewModel.PasswordRow)((FrameworkElement)sender).DataContext;
            Vm?.Remove(row);
        }

        void OnReveal(object sender, RoutedEventArgs e)
        {
            var row = (PasswordBookViewModel.PasswordRow)((FrameworkElement)sender).DataContext;
            Vm?.Reveal(row);
        }

        void OnEditSave(object sender, RoutedEventArgs e)
        {
            var name = EditName.Text.Trim();
            var plain = EditPlain.Text;
            if (_editingRow == null)
                Vm?.Add(name, plain);
            else
                Vm?.Update(_editingRow, name, plain);
            CloseOverlay();
        }

        void OnEditCancel(object sender, RoutedEventArgs e) => CloseOverlay();

        void OnOverlayClick(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, EditOverlay)) CloseOverlay();
        }

        void OnOverlayBodyClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

        void CloseOverlay()
        {
            EditOverlay.Visibility = Visibility.Collapsed;
            _editingRow = null;
        }

        // ---- 密码纸 ----

        void OnPastePaper(object sender, RoutedEventArgs e)
        {
            if (Vm != null) Vm.PasteFromClipboardCommand.Execute(null);
        }

        void OnClearPaper(object sender, RoutedEventArgs e)
        {
            Vm?.ClearPaper();
        }
    }
}
