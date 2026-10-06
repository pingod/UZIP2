using System;
using System.Collections.Generic;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 全局异常兜底：崩之前必须把现场留在日志里，且不能把致命的异常当普通弹窗吞掉。
    public class CrashGuardTests
    {
        [Fact]
        public void Ui_thread_exception_is_logged_and_marked_handled()
        {
            var log = new Log();
            var shown = new List<string>();
            bool handled = CrashGuard.OnUiThreadException(
                new InvalidOperationException("绑定失败"), log, shown.Add);

            Assert.True(handled);
            Assert.Single(shown);
            Assert.Contains("绑定失败", shown[0]);
            Assert.Contains(log.Lines, l => l.Contains("绑定失败"));
        }

        // OOM / 栈溢出之后继续跑，只会在已经坏掉的状态上写用户的文件
        [Theory]
        [InlineData("oom")]
        [InlineData("stack")]
        public void Fatal_exceptions_are_not_swallowed_and_show_no_dialog(string kind)
        {
            var ex = kind == "oom"
                ? (Exception)new OutOfMemoryException("没了")
                : new StackOverflowException("递归爆了");
            var log = new Log();
            var shown = new List<string>();

            Assert.False(CrashGuard.OnUiThreadException(ex, log, shown.Add));
            Assert.Empty(shown);
            Assert.Contains(log.Lines, l => l.Contains("递归爆了") || l.Contains("没了"));
        }

        [Fact]
        public void Background_exception_logs_without_needing_a_dialog()
        {
            var log = new Log();
            var ex = Record.Exception(() => CrashGuard.OnBackgroundException(
                new AggregateException(new InvalidOperationException("后台炸了")), log));

            Assert.Null(ex);
            Assert.Contains(log.Lines, l => l.Contains("后台炸了"));
        }

        [Fact]
        public void Missing_logger_never_turns_a_crash_into_a_second_crash()
        {
            Assert.Null(Record.Exception(() => CrashGuard.OnUiThreadException(new Exception("x"), null, null)));
            Assert.Null(Record.Exception(() => CrashGuard.OnBackgroundException(new Exception("y"), null)));
        }

        [Fact]
        public void Null_exception_is_ignored()
        {
            Assert.False(CrashGuard.OnUiThreadException(null, new Log(), _ => { }));
        }

        class Log : IFileLogger
        {
            public readonly List<string> Lines = new List<string>();
            public void Info(string message) { lock (Lines) Lines.Add(message ?? ""); }
            public void Warn(string message) { lock (Lines) Lines.Add(message ?? ""); }
            public void Error(string message, Exception exception = null)
            {
                lock (Lines) Lines.Add((message ?? "") + (exception == null ? "" : " | " + exception.Message));
            }
            public string LatestLogPath => "";
        }
    }
}
