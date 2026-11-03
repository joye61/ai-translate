using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiTranslate.Ai;

/// <summary>Project the received JSON prefix into a read-only preview. The wire response stays intact for final validation.</summary>
internal sealed class DictionaryFieldPreview(Action<string>? translation, Action<JsonElement>? fields)
{
    private readonly StringBuilder _json = new();
    private readonly Stack<char> _closers = new();
    private bool _inString, _valueString, _escaped, _needsQuote, _done;
    private int _unicodeDigits, _safeEnd, _publishedEnd;
    private char _previous;
    private string? _lastTranslation;
    private long _lastFrame;

    public void Append(string delta)
    {
        if (_done || (translation is null && fields is null)) return;
        if (_json.Length == 0)
        {
            var start = delta.IndexOf('{');
            if (start < 0) return; // Some providers wrap the object in a Markdown fence.
            delta = delta[start..];
        }

        var valueFinished = false;
        foreach (var c in delta)
        {
            _json.Append(c);
            if (_inString)
            {
                if (_unicodeDigits > 0)
                {
                    if (--_unicodeDigits > 0) continue;
                    if (!ushort.TryParse(_json.ToString(_json.Length - 4, 4), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var code)) { _done = true; return; }
                    if (char.IsHighSurrogate((char)code)) continue;
                }
                else if (_escaped)
                {
                    _escaped = false;
                    if (c == 'u') { _unicodeDigits = 4; continue; }
                }
                else if (c == '\\') { _escaped = true; continue; }
                else if (c == '"')
                {
                    _inString = false;
                    _previous = c;
                    if (!_valueString) continue; // Never show a partial JSON member name.
                    valueFinished = true;
                }
                else if (char.IsHighSurrogate(c)) continue;

                if (_valueString)
                {
                    _safeEnd = _json.Length;
                    _needsQuote = _inString;
                }
                continue;
            }

            if (char.IsWhiteSpace(c)) continue;
            switch (c)
            {
                case '"':
                    _inString = true;
                    _valueString = _previous == ':' || _closers.TryPeek(out var closer) && closer == ']';
                    break;
                case '{':
                case '[':
                    if (_closers.Count == 64) { _done = true; return; }
                    _closers.Push(c == '{' ? '}' : ']');
                    _safeEnd = _json.Length;
                    break;
                case '}':
                case ']':
                    if (!_closers.TryPop(out var expected) || expected != c) { _done = true; return; }
                    _safeEnd = _json.Length;
                    valueFinished = true;
                    _done = _closers.Count == 0;
                    break;
                case ',':
                    _safeEnd = _json.Length - 1;
                    break;
            }
            _previous = c;
            if (_done) break;
        }

        if (_safeEnd == _publishedEnd) return;
        if (!valueFinished && _lastFrame != 0 && Stopwatch.GetElapsedTime(_lastFrame).TotalMilliseconds < 40) return;
        var prefix = _json.ToString(0, _safeEnd) + (_needsQuote ? "\"" : "") + string.Concat(_closers);
        JsonDocument snapshot;
        try { snapshot = JsonDocument.Parse(prefix); }
        catch (JsonException) { return; } // Never promote a preview to a successful result.
        using (snapshot)
        {
            var root = snapshot.RootElement;
            if (root.TryGetProperty("primaryTranslation", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text) && text != _lastTranslation)
                {
                    _lastTranslation = text;
                    translation?.Invoke(text);
                }
                // Popup callers only ask for the main translation.
                if (fields is null && !_needsQuote && _lastTranslation is not null) _done = true;
            }
            fields?.Invoke(root);
            _publishedEnd = _safeEnd;
            if (_lastTranslation is not null || fields is not null) _lastFrame = Stopwatch.GetTimestamp();
        }
    }
}
