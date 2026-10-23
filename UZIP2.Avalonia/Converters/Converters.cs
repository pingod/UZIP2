using System;
using System.Globalization;
using Avalonia.Data.Converters;
using UZIP2.Models;

namespace UZIP2.Avalonia.Converters
{
    /// <summary>double? 进度 → ProgressBar 的 0..100（null 视为 0）。</summary>
    public sealed class PercentValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d ? Math.Clamp(d, 0, 100) : 0d;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>作业状态 → 中文。</summary>
    public sealed class JobStatusTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is JobStatus s)) return "";
            switch (s)
            {
                case JobStatus.Queued: return "排队中";
                case JobStatus.Running: return "处理中";
                case JobStatus.Success: return "完成";
                case JobStatus.Failed: return "失败";
                case JobStatus.Cancelled: return "已取消";
                default: return s.ToString();
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>历史记录的 Unix 毫秒时间戳 → 本地时间文本。</summary>
    public sealed class UnixMsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is long ms) || ms <= 0) return "";
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm:ss"); }
            catch { return ""; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
