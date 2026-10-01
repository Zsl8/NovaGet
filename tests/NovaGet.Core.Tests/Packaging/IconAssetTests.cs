using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>
/// Section 17: every icon comes in 16/20/24/32/48/64 pixels (plus 96 for the large toolbar at 200%), toolbar icons also
/// in the monochrome skin, all rendered from assets/icons/svg by build-icons.mjs.
/// </summary>
public sealed class IconAssetTests
{
    private static readonly int[] RequiredSizes = [16, 20, 24, 32, 48, 64];
    private static readonly int[] ToolbarSizes = [24, 48, 96];

    private static readonly string[] ToolbarIcons =
    [
        "add-url", "resume", "stop", "stop-all", "delete", "delete-completed", "options", "scheduler",
        "start-queue", "stop-queue", "grabber", "tell-friend",
    ];

    private static string Icons => RepoPaths.Combine("assets", "icons");

    [Fact]
    public void Every_svg_is_rendered_in_every_size()
    {
        var svgs = Directory.EnumerateFiles(Path.Combine(Icons, "svg"), "*.svg").Select(Path.GetFileNameWithoutExtension).ToList();
        Assert.NotEmpty(svgs);
        var missing = svgs
            .SelectMany(name => RequiredSizes.Select(size => $"{name}-{size}.png"))
            .Where(file => !File.Exists(Path.Combine(Icons, "png", file)))
            .ToList();
        Assert.True(missing.Count == 0, "Run assets/icons/build-icons.mjs; missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void Toolbar_icons_exist_in_both_skins_at_toolbar_sizes()
    {
        var missing = ToolbarIcons
            .SelectMany(name => ToolbarSizes.SelectMany(size => new[] { $"{name}-{size}.png", $"{name}-mono-{size}.png" }))
            .Where(file => !File.Exists(Path.Combine(Icons, "png", file)))
            .ToList();
        Assert.True(missing.Count == 0, "Missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void Pngs_are_real_images_of_the_named_size()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Icons, "png"), "*.png"))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.Length > 24 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G', file);

            // IHDR width and height, big-endian, right after the signature and chunk header.
            var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            var size = int.Parse(Path.GetFileNameWithoutExtension(file).Split('-')[^1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(width == size && height == size, $"{Path.GetFileName(file)} is {width}×{height}");
        }
    }

    [Fact]
    public void App_icons_carry_every_windows_size()
    {
        foreach (var name in new[] { "novaget", "tray-active" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(Icons, "ico", name + ".ico"));
            var count = BitConverter.ToUInt16(bytes, 4);
            var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + (16 * i)] is 0 ? 256 : bytes[6 + (16 * i)]).ToHashSet();
            Assert.Superset(new HashSet<int>([.. RequiredSizes, 256]), sizes);
        }
    }
}
