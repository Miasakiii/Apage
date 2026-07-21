using System.Text.Json;

namespace Apage.Spikes.WebMessage;

/// <summary>
/// 模拟 §4.4 的后端分发：按 type 异步处理，返回 data 或抛异常。
/// </summary>
public static class BackendDispatcher
{
    public static async Task<object?> DispatchAsync(string type, JsonElement payload)
    {
        switch (type)
        {
            // 心跳：document-start 可用性探测 + 延迟实测
            case "ping":
                return new { reply = "pong" };

            // 原样回显：验证请求/响应 id 关联
            case "echo":
                return payload;

            // 延迟回显：并发测试中人为制造乱序响应
            case "delayedEcho":
            {
                var delayMs = payload.ValueKind == JsonValueKind.Object
                              && payload.TryGetProperty("delayMs", out var d)
                              && d.ValueKind == JsonValueKind.Number
                    ? d.GetInt32()
                    : 0;
                if (delayMs > 0) await Task.Delay(delayMs);
                return payload;
            }

            // 错误路径：故意抛异常，C# 侧捕获后应回 {id, ok:false, error}
            case "boom":
                await Task.Yield();
                throw new InvalidOperationException("spike-boom: 故意抛出的后端异常（错误路径测试）");

            default:
                throw new NotSupportedException($"未知消息类型: {type}");
        }
    }
}
