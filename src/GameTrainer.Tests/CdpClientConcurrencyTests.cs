using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using GameTrainer;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// CdpClient 并发防护的回归测试（M1）。
///
/// 背景：Evaluate 无并发保护——++_msgId 非原子，WebSocket 也不允许
/// 并发 Send/Receive。今天生产代码每个调用点都自建自毁连接
/// （AdapterRunner/PersistentWatcher/Program 均是 using var cdp = Connect），
/// 所以竞态没被触发；但待办 M5（连接复用）一旦落地，共享实例就是前提，
/// 不先串行化 Evaluate 会立刻交叉读响应。
///
/// 这里不用真实浏览器：HttpListener + AcceptWebSocketAsync 造一个真握手
/// 的假 CDP 服务端，按 id 回对应响应，通过 internal 注入点接进 CdpClient。
/// 并发 8 线程 × 若干轮，断言全部成功且拿到自己的 id。
/// </summary>
public class CdpClientConcurrencyTests
{
    private sealed class FakeCdpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task _serve;

        public int Port { get; }

        public FakeCdpServer()
        {
            // HttpListener 不支持端口 0 自动分配，先用 TcpListener 探一个空闲端口
            int port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Port = port;
            _serve = Task.Run(AcceptLoop);
        }

        private static int GetFreePort()
        {
            using var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { break; }

                if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
                _ = Handle(ctx);
            }
        }

        private static async Task Handle(HttpListenerContext ctx)
        {
            HttpListenerWebSocketContext wsCtx;
            try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null); }
            catch { return; }
            using var ws = wsCtx.WebSocket;
            var buffer = new byte[1 << 16];
            while (ws.State == WebSocketState.Open)
            {
                string msg;
                try
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult res;
                    do
                    {
                        res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    } while (!res.EndOfMessage);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    msg = sb.ToString();
                }
                catch { break; }

                // 假 CDP 行为：解析 id，回一个 echo 响应（value = id）
                int id;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(msg);
                    id = doc.RootElement.GetProperty("id").GetInt32();
                }
                catch { break; }

                string reply = "{\"id\":" + id + ",\"result\":{\"result\":{\"type\":\"number\",\"value\":" + id + "}}}";
                byte[] bytes = Encoding.UTF8.GetBytes(reply);
                try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { break; }
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _serve.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }

    private static async Task<WebSocket> ConnectToAsync(int port)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/page/test"), CancellationToken.None);
        return ws;
    }

    [Fact]
    public async Task 并发Evaluate_每个线程必须拿到自己的响应()
    {
        using var server = new FakeCdpServer();
        using var ws = await ConnectToAsync(server.Port);
        using var cdp = CdpClient.CreateConnected(ws);

        const int threads = 8;
        const int rounds = 12;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
        {
            for (int r = 0; r < rounds; r++)
            {
                try
                {
                    string got = await Task.Run(() => cdp.Evaluate("1+1", timeoutMs: 10000));
                    // 假服务端回 value = 请求自身的 id；空响应或异常都算失败
                    if (string.IsNullOrEmpty(got))
                        errors.Enqueue("空响应");
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex.GetType().Name + ": " + ex.Message);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // 竞态的两种表现：异常（并发 send/receive 交叠）与空响应
        Assert.True(errors.IsEmpty,
            "并发 Evaluate 出现失败（共 " + errors.Count + " 次），前 5 条：\n" +
            string.Join("\n", errors.Take(5)));
    }
}
