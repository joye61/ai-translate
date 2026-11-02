using System.Text;
using System.Text.Json;
using AiTranslate.Ai;
using AiTranslate.Core;

namespace AiTranslate.Desktop;

/// <summary>
/// Persists user settings (AI provider, model, API key, hotkeys, monitoring toggles) to LocalApplicationData.
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private AppSettings _current;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public event EventHandler<AppSettings>? Changed;

    public AppSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiTranslate",
            "app-settings.json");
        _current = LoadOrDefault();
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public void Update(AppSettings updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (_gate)
        {
            _current = updated;
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_current, JsonOptions), Encoding.UTF8);
            }
            catch
            {
                // Ignore disk write issues
            }
        }
        Changed?.Invoke(this, updated);
    }

    public IAiTransport? CreateTransport()
    {
        var s = Current;
        var config = new AiProviderConfig(s.ProviderId, s.ModelId, s.CustomBaseUrl, s.ApiKey);
        if (!AiProviders.IsConfigured(config))
            return null;
        return new AiTransport(config);
    }

    private AppSettings LoadOrDefault()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (parsed is not null) return parsed;
            }
        }
        catch
        {
        }
        return new AppSettings();
    }
}
