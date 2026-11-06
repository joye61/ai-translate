namespace AiTranslate.Ai;

/// <summary>One budget for the entire operation, including physical HTTP retries.</summary>
public sealed class AiRequestBudget(int maximumRequests = 4)
{
    private int _requests;
    private int _recoveryUsed;
    public int Requests => Volatile.Read(ref _requests);
    public int Remaining => Math.Max(0, maximumRequests - Requests);
    public bool TryReserveRetry() => Remaining > 0 && Interlocked.Exchange(ref _recoveryUsed, 1) == 0;

    public void Consume()
    {
        while (true)
        {
            var used = Requests;
            if (used >= maximumRequests) throw new AiException("本次 AI 请求次数已达上限，已保留可用结果。");
            if (Interlocked.CompareExchange(ref _requests, used + 1, used) == used) return;
        }
    }
}

public sealed record AiRequestOptions(AiRequestBudget Budget, int MaxOutputTokens = 4096);
