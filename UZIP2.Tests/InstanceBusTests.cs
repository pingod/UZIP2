using System;
using System.Threading.Tasks;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 单实例转发：第二进程把命令行里的文件路径通过命名管道交给已运行的主实例。
    // 测试必须用独立的名字，否则会和用户正在运行的 UZIP2 抢同一个互斥体/管道。
    public class InstanceBusTests : IDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N");
        private string MutexName => "UZipTestBus_" + _suffix;
        private string PipeName => "UZipTestPipe_" + _suffix;

        public void Dispose() { }

        [Fact]
        public async Task Primary_receives_the_paths_forwarded_by_the_second_process()
        {
            using var bus = new InstanceBus(MutexName, PipeName);
            Assert.True(bus.TryBecomePrimary());

            var received = new TaskCompletionSource<string[]>();
            bus.FilesReceived += args => received.TrySetResult(args);

            Assert.True(InstanceBus.ForwardArgsToPrimary(PipeName, new[] { @"C:\a.zip", @"D:\b.7z" }));

            Assert.Same(received.Task, await Task.WhenAny(received.Task, Task.Delay(5000)));
            Assert.Equal(new[] { @"C:\a.zip", @"D:\b.7z" }, await received.Task);
        }

        // 空参数 = 仅唤起窗口，事件仍要触发（否则双击图标没反应）
        [Fact]
        public async Task Forwarding_no_paths_still_wakes_the_primary()
        {
            using var bus = new InstanceBus(MutexName, PipeName);
            Assert.True(bus.TryBecomePrimary());

            var received = new TaskCompletionSource<string[]>();
            bus.FilesReceived += args => received.TrySetResult(args);

            Assert.True(InstanceBus.ForwardArgsToPrimary(PipeName, Array.Empty<string>()));

            Assert.Same(received.Task, await Task.WhenAny(received.Task, Task.Delay(5000)));
            Assert.Empty(await received.Task);
        }

        [Fact]
        public void Forwarding_to_an_absent_primary_reports_failure()
        {
            Assert.False(InstanceBus.ForwardArgsToPrimary("UZipTestPipe_missing_" + _suffix, new[] { "x" }));
        }

        [Fact]
        public void Dispose_stops_the_listener_so_forwarding_fails_afterwards()
        {
            var bus = new InstanceBus(MutexName, PipeName);
            Assert.True(bus.TryBecomePrimary());
            bus.Dispose();
            Assert.False(InstanceBus.ForwardArgsToPrimary(PipeName, new[] { "x" }));
        }
    }
}
