using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace UZIP2.Shell.Pages
{
    // {conv:EqualVisibility Failed} -> 值等于参数时 Visible，否则 Collapsed
    public sealed class EqualVisibilityExtension : MarkupExtension
    {
        private readonly string _target;
        public EqualVisibilityExtension(string target) { _target = target; }

        public override object ProvideValue(IServiceProvider serviceProvider)
            => new EqualVisibilityConverter(_target);
    }

    public sealed class EqualVisibilityConverter : IValueConverter
    {
        private readonly string _target;
        public EqualVisibilityConverter(string target) { _target = target; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), _target, StringComparison.Ordinal)
                ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // {conv:AnyVisibility Queued,Running} -> 值命中任一参数时 Visible
    public sealed class AnyVisibilityExtension : MarkupExtension
    {
        private readonly string[] _targets;
        public AnyVisibilityExtension(string targets)
        {
            _targets = targets.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        public override object ProvideValue(IServiceProvider serviceProvider)
            => new AnyVisibilityConverter(_targets);
    }

    public sealed class AnyVisibilityConverter : IValueConverter
    {
        private readonly string[] _targets;
        public AnyVisibilityConverter(string[] targets) { _targets = targets; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => _targets.Contains(value?.ToString(), StringComparer.Ordinal)
                ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
