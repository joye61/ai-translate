using System.Net.Http.Headers;
using System.Text.Json;

namespace AiTranslate.Ai;

public sealed record AiProviderConfig(
    string ProviderId = "deepseek",
    string ModelId = "deepseek-chat",
    string? BaseUrl = null,
    string ApiKey = "");

public sealed record AiModelInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record AiProviderInfo(string Id, string Name, string BaseUrl, string KeyUrl, IReadOnlyList<AiModelInfo> DefaultModels)
{
    public override string ToString() => Name;
}

public static class AiProviders
{
    public static IReadOnlyList<AiProviderInfo> All { get; } =
    [
        // Only non-thinking models are offered by default: every request this app makes is a
        // translation/dictionary lookup — a high-frequency, latency-sensitive, semantically simple
        // task that gains nothing from a "deep reasoning" model (e.g. deepseek-reasoner), which can
        // also not be switched out of thinking mode via the API. Advanced users can still type any
        // custom model id in Settings.
        new("deepseek", "DeepSeek", "https://api.deepseek.com/", "https://platform.deepseek.com/api_keys",
            [new("deepseek-chat", "DeepSeek V3 · 极速推荐")]),
        new("openai", "OpenAI", "https://api.openai.com/v1/", "https://platform.openai.com/api-keys",
            [new("gpt-4o-mini", "GPT-4o Mini · 快速"), new("gpt-4o", "GPT-4o · 旗舰")]),
        new("compatible", "OpenAI 兼容端点 (硅基流动/通义/自定义)", "https://api.siliconflow.cn/v1/", "https://platform.openai.com/docs/api-reference/chat",
            [new("Qwen/Qwen2.5-72B-Instruct", "Qwen 2.5 72B")]),
        new("ollama", "Ollama · 本地大模型", "http://localhost:11434/v1/", "https://ollama.com/",
            [new("qwen2.5:7b", "qwen2.5:7b · 本地离线"), new("llama3.1:8b", "llama3.1:8b · 本地离线")])
    ];

    public static bool IsCompatible(string id) => id is "compatible" or "ollama";

    public static AiProviderInfo Get(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static AiProviderInfo Resolve(AiProviderConfig config)
    {
        var provider = Get(config.ProviderId);
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
            return provider with { BaseUrl = NormalizeBaseUrl(config.BaseUrl) };
        return provider;
    }

    public static bool IsConfigured(AiProviderConfig config)
    {
        var provider = Resolve(config);
        if (provider.Id == "ollama") return true;
        if (Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback) return true;
        return !string.IsNullOrWhiteSpace(config.ApiKey);
    }

    public static string NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\')
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new AiException("请填写 HTTPS API 基础地址（本机 localhost / 回环地址可使用 HTTP），不要包含账号、查询参数或片段。");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new AiException("请填写 API 基础地址（通常以 /v1 结尾），不要包含 /chat/completions。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }
}

public class AiException(string message, Exception? innerException = null) : Exception(message, innerException);

internal static class AiHttp
{
    private static readonly SocketsHttpHandler Connections = new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public static HttpClient CreateClient() => new(Connections, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new AiException((int)response.StatusCode switch
        {
            400 or 422 => $"端点拒绝了请求（HTTP {(int)response.StatusCode}）。请核对模型 ID，并确认该端点和模型支持非思考模式。",
            401 => "API Key 无效或已失效，请在设置中配置有效的 API Key。",
            402 => "AI 供应商账户余额不足，请充值后重试。",
            403 => "供应商拒绝访问，请检查账户权限与服务区域。",
            404 => "所选模型或 API 端点不可用，请在设置中检查模型名称。",
            429 => "请求过于频繁或额度不足，请稍后重试。",
            503 => "当前翻译服务繁忙或维护中（HTTP 503）。请稍后重试，或在「应用设置」中切换服务商。",
            >= 500 => $"AI 供应商服务暂时不可用（HTTP {(int)response.StatusCode}），请稍后重试。",
            _ => $"AI 请求失败（HTTP {(int)response.StatusCode}）。"
        });
    }
}
