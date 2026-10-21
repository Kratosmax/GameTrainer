using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GameTrainer;

/// <summary>
/// 极简 CDP（Chrome DevTools Protocol）客户端。
///
/// 用于 NW.js / Electron / CEF 类游戏：通过 HTTP /json/list 发现页面目标，
/// 再用 WebSocket 发 Runtime.evaluate 在页面里执行 JS。
///
/// 这是 js_runtime 后端的基础设施——不碰进程内存，直接在 JS 引擎层读写变量，
/// 因此不受 V8 垃圾回收导致的地址漂移影响。
/// </summary>
internal sealed class CdpClient : IDisposable
{
    private ClientWebSocket _ws;
    /// <summary>测试注入通道：非 null 时 Evaluate/Dispose 走它而不是 _ws。</summary>
    private WebSocket _anyWs;
    private int _msgId;
    /// <summary>
    /// Evaluate 的串行化锁。WebSocket 不允许并发 Send/Receive，
    /// ++_msgId 也非原子；当前生产调用点都自建自毁连接所以未触发，
    /// 但 M5（连接复用）共享实例前必须先有这把锁。
    /// </summary>
    private readonly object _ioLock = new();

    private WebSocket Ws => _anyWs ?? _ws;

    public string TargetTitle { get; private set; } = "";
    public string TargetUrl { get; private set; } = "";
    public string BrowserVersion { get; private set; } = "";

    /// <summary>连接到指定端口上第一个 type=page 的目标。</summary>
    public static CdpClient Connect(int port, string titleFilter = null, int timeoutMs = 10000)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
        string baseUrl = $"http://127.0.0.1:{port}";

        string versionJson;
        try { versionJson = http.GetStringAsync($"{baseUrl}/json/version").GetAwaiter().GetResult(); }
        catch (Exception ex) { throw new InvalidOperationException($"无法连接 CDP 端口 {port}：{ex.Message}\n请用 --remote-debugging-port={port} 启动目标程序。"); }

        using var vdoc = JsonDocument.Parse(versionJson);
        string browser = vdoc.RootElement.TryGetProperty("Browser", out var b) ? b.GetString() : "?";

        string listJson = http.GetStringAsync($"{baseUrl}/json/list").GetAwaiter().GetResult();
        using var ldoc = JsonDocument.Parse(listJson);

        string wsUrl = null;
        string title = "", url = "";
        foreach (var t in ldoc.RootElement.EnumerateArray())
        {
            if (!t.TryGetProperty("type", out var ty) || ty.GetString() != "page") continue;
            string ti = t.TryGetProperty("title", out var tt) ? tt.GetString() : "";
            if (titleFilter != null && (ti == null || !ti.Contains(titleFilter, StringComparison.OrdinalIgnoreCase))) continue;
            wsUrl = t.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
            url = t.TryGetProperty("url", out var u) ? u.GetString() : "";
            title = ti;
            if (wsUrl != null) break;
        }

        if (wsUrl == null) throw new InvalidOperationException("没有找到可调试的 page 目标。");

        var c = new CdpClient { TargetTitle = title, TargetUrl = url, BrowserVersion = browser };
        c._ws = new ClientWebSocket();
        c._ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).GetAwaiter().GetResult();
        return c;
    }

    /// <summary>
    /// 测试专用：注入已建立的 WebSocket，跳过 HTTP 目标发现。
    /// 生产代码请用 Connect（它负责 /json/version 与 /json/list 与目标筛选）。
    /// </summary>
    internal static CdpClient CreateConnected(WebSocket ws)
        => new() { _anyWs = ws };

    /// <summary>在页面上下文执行 JS，返回求值结果（已转为字符串）。</summary>
    /// <remarks>
    /// 线程安全：整段串行化（_ioLock）。WebSocket 协议不允许同一实例上
    /// 并发 Send/Receive，两个线程同时 Evaluate 会交叉读到对方的响应。
    /// 想并发请各建各的连接（当前所有生产调用点都是这么做的）。
    /// </remarks>
    public string Evaluate(string expression, bool awaitPromise = true, int timeoutMs = 30000)
    {
        lock (_ioLock)
        {
            int id = ++_msgId;
            var payload = new
            {
                id,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression,
                    returnByValue = true,
                    awaitPromise,
                    userGesture = true
                }
            };

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
               .GetAwaiter().GetResult();

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = new byte[1 << 20];
            var sb = new StringBuilder();

            while (DateTime.UtcNow < deadline)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    var seg = new ArraySegment<byte>(buffer);
                    var task = Ws.ReceiveAsync(seg, CancellationToken.None);
                    if (!task.Wait(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)))
                        throw new TimeoutException("CDP 响应超时");
                    res = task.Result;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                } while (!res.EndOfMessage);

                using var doc = JsonDocument.Parse(sb.ToString());
                var root = doc.RootElement;

                // 跳过事件推送，只处理我们这次的响应
                if (!root.TryGetProperty("id", out var rid) || rid.GetInt32() != id) continue;

                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException("CDP 错误：" + err.GetRawText());

                var result = root.GetProperty("result");
                if (result.TryGetProperty("exceptionDetails", out var ex))
                    throw new InvalidOperationException("页面异常：" + ex.GetRawText());

                if (!result.TryGetProperty("result", out var value)) return null;
                return value.TryGetProperty("value", out var v)
                    ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
                    : (value.TryGetProperty("description", out var d) ? d.GetString() : null);
            }
            throw new TimeoutException("CDP 响应超时");
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            try { Ws?.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(2000); } catch { }
            Ws?.Dispose();
        }
    }
}
