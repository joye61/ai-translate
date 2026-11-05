using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace AiTranslate.Tests;

public sealed class BundledOcrModelTests
{
    [Fact]
    public void ShippedFilesMatchTheOcrManifest()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "OcrModels");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
        var root = manifest.RootElement;
        foreach (var model in root.GetProperty("models").EnumerateArray())
        {
            Check(model, "file", "sha256");
            Check(model, "configuration", "configurationSha256");
        }
        Check(root.GetProperty("dictionary"), "file", "sha256");

        void Check(JsonElement item, string fileProperty, string hashProperty)
        {
            var name = item.GetProperty(fileProperty).GetString()!;
            using var file = File.OpenRead(Path.Combine(directory, name));
            Assert.Equal(item.GetProperty(hashProperty).GetString(), Convert.ToHexStringLower(SHA256.HashData(file)));
        }
    }
}
