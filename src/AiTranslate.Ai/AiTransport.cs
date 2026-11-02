using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiTranslate.Ai;

public sealed record AiToolCall(string Id, string Name, string Arguments);
public sealed record AiCompletion(string Text, IReadOnlyList<AiToolCall> Calls, int InputTokens = 0, int OutputTokens = 0);

public interface IAiTransport
{
    Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token);
}

public sealed class AiTransport(AiProviderConfig config, HttpClient? client = null) : IAiTransport, IDisposable
{
    private readonly HttpClient _client = client ?? AiHttp.CreateClient();

    public async Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
    {
        var provider = AiProviders.Resolve(config);
        var modelId = string.IsNullOrWhiteSpace(config.ModelId)
            ? provider.DefaultModels.FirstOrDefault()?.Id ?? "deepseek-chat"
            : config.ModelId.Trim();

        var body = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["stream"] = true
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        if (provider.Id == "deepseek")
        {
            body["max_tokens"] = 8192;
        }
        else if (AiProviders.IsCompatible(provider.Id))
        {
            body["max_tokens"] = 8192;
        }
        else
        {
            body["max_completion_tokens"] = 8192;
        }

        // Every request in this app is a translation or dictionary lookup: a high-frequency,
        // latency-sensitive, semantically simple task. Extended "thinking"/reasoning only adds
        // cost and delay here, so explicitly request the fastest non-thinking mode whenever the
        // selected model is known to support such a toggle. Models that don't recognize the field
        // are left untouched so this never breaks a plain chat model's request.
        if (provider.Id == "openai" && IsGpt5ReasoningModel(modelId))
        {
            // "minimal" is only a valid reasoning_effort value for the GPT-5 family; the o-series
            // models (o1/o3/o4-mini) reject it outright and only accept low/medium/high.
            body["reasoning_effort"] = "minimal";
        }
        else if (provider.Id == "openai" && IsOSeriesReasoningModel(modelId))
        {
            body["reasoning_effort"] = "low";
        }
        else if (AiProviders.IsCompatible(provider.Id) && IsHybridThinkingModel(modelId))
        {
            body["enable_thinking"] = false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl + "chat/completions");
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey.Trim());
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            AiHttp.EnsureSuccess(response);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await ReadStreamAsync(stream, onText, timeout.Token, () => timeout.CancelAfter(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new AiException("AI 响应超时，请检查网络或稍后重试。");
        }
        catch (HttpRequestException)
        {
            throw new AiException("无法连接到 AI 服务端点，请检查网络或设置中的 API 地址。");
        }
        catch (IOException)
        {
            throw new AiException("AI 流式传输连接中断，请重试。");
        }
        catch (JsonException)
        {
            throw new AiException("模型返回格式异常，请重试或更换模型。");
        }
    }

    public static async Task<AiCompletion> ReadStreamAsync(Stream stream, Action<string> onText, CancellationToken token, Action? onProgress = null)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Args)>();
        var input = 0; var output = 0; string? finish = null;
        await foreach (var line in ReadLinesAsync(stream, token, onProgress).ConfigureAwait(false))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;
            using var doc = ParseChunk(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidChunk();
            if (root.TryGetProperty("error", out _)) throw new AiException("供应商中断了请求，请稍后重试。");
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var prompt) && prompt >= 0) input = prompt;
                if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var completion) && completion >= 0) output = completion;
            }
            if (!root.TryGetProperty("choices", out var choices)) continue;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object) throw InvalidChunk();
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) finish = reason.GetString();
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind == JsonValueKind.Null) continue;
            if (delta.ValueKind != JsonValueKind.Object) throw InvalidChunk();
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var part = content.GetString()!;
                if (text.Length + part.Length > 64_000) throw new AiException("回答过长，已停止接收。");
                text.Append(part);
                onText(part);
            }
        }
        if (finish == "length") throw new AiException("供应商的单次输出额度已用尽。");
        if (calls.Count == 0 && text.Length == 0) throw new AiException("模型没有返回可用内容。");
        var resultCalls = calls.Values.Select(c => new AiToolCall(c.Id.ToString(), c.Name.ToString(), c.Args.ToString())).ToArray();
        return new(text.ToString(), resultCalls, input, output);
    }

    /// <summary>
    /// The GPT-5 family (excluding the non-reasoning "gpt-5-chat" variant). This is the only family
    /// for which OpenAI's unified "reasoning_effort" accepts the value "minimal".
    /// </summary>
    private static bool IsGpt5ReasoningModel(string modelId) =>
        modelId.Contains("gpt-5", StringComparison.OrdinalIgnoreCase)
            && !modelId.Contains("chat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OpenAI's o-series reasoning models (o1/o3/o4), excluding "o1-mini"/"o1-preview" which never
    /// accepted "reasoning_effort" at all and return an error for it. Unlike GPT-5, this family only
    /// accepts low/medium/high for "reasoning_effort" — "minimal" is rejected outright.
    /// </summary>
    private static bool IsOSeriesReasoningModel(string modelId) =>
        (modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            && !modelId.Contains("mini", StringComparison.OrdinalIgnoreCase)
            && !modelId.Contains("preview", StringComparison.OrdinalIgnoreCase)) ||
        modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hybrid-thinking OpenAI-compatible models that accept an "enable_thinking" toggle (Qwen3, GLM, etc.).
    /// "kimi-k2-thinking" is intentionally NOT included: unlike the others, it is a dedicated always-thinking
    /// model (as opposed to the separate non-thinking "kimi-k2"/"kimi-k2-turbo" model ids) with no documented
    /// on/off switch, so a user who explicitly selects it wants reasoning and the field would be meaningless
    /// (or rejected) rather than toggling anything.
    /// </summary>
    private static bool IsHybridThinkingModel(string modelId) =>
        modelId.Contains("qwen3", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("qwq", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("glm-4.5", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("glm-4.6", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("hunyuan-a", StringComparison.OrdinalIgnoreCase) ||
        modelId.Contains("deepseek-v3.1", StringComparison.OrdinalIgnoreCase);

    private static AiException InvalidChunk() => new("模型返回格式异常，请重试或更换模型。");
    private static JsonDocument ParseChunk(string data)
    {
        try { return JsonDocument.Parse(data); }
        catch (JsonException) { throw InvalidChunk(); }
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token, Action? onProgress)
    {
        const int maxLineChars = 2_000_000;
        const int maxStreamChars = 32_000_000;
        using var reader = new StreamReader(stream);
        var buffer = new char[4_096]; var line = new StringBuilder(); var total = 0; var skipLf = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            onProgress?.Invoke(); total += count;
            if (total > maxStreamChars) throw new AiException("模型响应的数据流过大，已停止接收。");
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (skipLf && character == '\n') { skipLf = false; continue; }
                skipLf = character == '\r';
                if (character is '\r' or '\n') { yield return line.ToString(); line.Clear(); }
                else
                {
                    if (line.Length >= maxLineChars) throw new AiException("模型响应的单行数据过长，已停止接收。");
                    line.Append(character);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }

    public void Dispose() { if (client == null) _client.Dispose(); }
}
