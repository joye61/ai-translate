using AiTranslate.Desktop;
using Avalonia.Headless;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class FontAwesomeIconTests
{
    [Fact]
    public async Task EveryRegisteredIconParsesAndUnknownNamesFallBackToTheDefaultIcon()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        await session.Dispatch(() =>
        {
            Assert.NotEmpty(FontAwesomeIconLibrary.Names);
            foreach (var name in FontAwesomeIconLibrary.Names)
            {
                var (geometry, width, height) = FontAwesomeIconLibrary.GetGeometry(name);
                Assert.True(geometry.Bounds.Width > 0 && geometry.Bounds.Height > 0 && width > 0 && height > 0, name);
            }

            Assert.False(FontAwesomeIconLibrary.IsRegistered("not-a-font-awesome-icon"));
            Assert.Equal(FontAwesomeIconLibrary.GetGeometry(FontAwesomeIconLibrary.DefaultIcon).Geometry.Bounds,
                FontAwesomeIconLibrary.GetGeometry("not-a-font-awesome-icon").Geometry.Bounds);
        }, CancellationToken.None);
    }
}
