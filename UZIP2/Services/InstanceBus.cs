using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UZIP2.Services
{
    // 单实例互斥 + 命名管道把第二进程收到的文件参数转发给主实例。
    public sealed class InstanceBus : IDisposable
    {
        private const string MutexName = @"Local\UZIP2_SingleInstance_Mutex";
        private const string PipeName = @"UZIP2_FileArgs_Pipe";

        private Mutex _mutex;
        private CancellationTokenSource _cts;

        // 参数到达（可能为空数组，表示"仅唤起窗口"）。在后台线程触发。
        public event Action<string[]> FilesReceived;

        public bool TryBecomePrimary()
        {
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // 上一实例异常退出时互斥体仍存活：等待接管
                try
                {
                    createdNew = _mutex.WaitOne(TimeSpan.FromSeconds(2), false);
                }
                catch (AbandonedMutexException)
                {
                    createdNew = true;
                }
            }
            if (!createdNew) return false;
            _cts = new CancellationTokenSource();
            Task.Run(() => ServeLoopAsync(_cts.Token));
            return true;
        }

        private async Task ServeLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var payload = await reader.ReadToEndAsync();
                    var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    FilesReceived?.Invoke(args);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 单次连接异常不影响监听循环
                }
            }
        }

        // 第二进程调用：把参数发给已运行的主实例。返回 true 表示本进程应立即退出。
        public static bool ForwardArgsToPrimary(string[] args)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                writer.Write(string.Join("\n", args ?? Array.Empty<string>()));
                writer.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _mutex?.Dispose();
        }
    }
}
