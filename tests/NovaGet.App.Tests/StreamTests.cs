using System.Windows;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Engine.Streams;

namespace NovaGet.App.Tests;

[Collection(WpfCollection.Name)]
public sealed class StreamTests(WpfFixture wpf)
{
    internal static StreamInfo SampleHls() => HlsParser.ParseMaster("""
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="en.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="Arabic",LANGUAGE="ar",URI="ar.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,AUDIO="aud"
        360.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1920x1080,AUDIO="aud"
        1080.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS="mp4a.40.2"
        audio.m3u8
        """, new Uri("https://cdn.example.com/show/master.m3u8")) with { Duration = 600 };

    [Fact]
    public void Quality_list_starts_at_the_best_and_offers_the_audio_languages()
    {
        var vm = new StreamQualityViewModel(SampleHls(), "Show");

        Assert.Equal(["MP4 1920x1080 · 4.5 Mbps", "MP4 640x360 · 800 kbps", "Audio · 128 kbps"], vm.Options.Select(o => o.Label));
        Assert.Equal("1", vm.Selected!.Variant.Id);
        Assert.Equal(337_500_000, vm.Options[0].EstimatedSize); // 4.5 Mbps for 10 minutes
        Assert.StartsWith("≈ ", vm.Options[0].SizeText, StringComparison.Ordinal);
        Assert.True(vm.HasAudioChoice);
        Assert.Equal("aud:0", vm.SelectedAudio!.Id); // the default rendition
        Assert.Equal(".mp4", vm.Extension);

        vm.SelectedAudio = vm.AudioOptions[1];
        var selection = vm.Selection;
        Assert.Equal(("1", "aud:1"), (selection.VariantId, selection.AudioId));
        Assert.Equal("https://cdn.example.com/show/master.m3u8", selection.ManifestUrl);

        vm.Selected = vm.Options[2];
        Assert.False(vm.HasAudioChoice);
        Assert.Equal(".m4a", vm.Extension);
        Assert.Null(vm.Selection.AudioId);
    }

    [Theory]
    [InlineData("https://example.com/v/master.m3u8", null, true)]
    [InlineData("https://example.com/manifest.mpd?token=1", null, true)]
    [InlineData("https://example.com/play?id=4", "application/vnd.apple.mpegurl", true)]
    [InlineData("https://example.com/play?id=4", "application/dash+xml", true)]
    [InlineData("https://example.com/video.mp4", "video/mp4", false)]
    public void Stream_addresses_are_recognized(string url, string? type, bool expected) =>
        Assert.Equal(expected, DownloadUiService.IsStreamCandidate(new Uri(url), type));

    [Fact]
    public void Quality_dialog_binds_its_choices()
    {
        wpf.Run(() =>
        {
            var dialog = new StreamQualityDialog(new StreamQualityViewModel(SampleHls(), "Show"))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
            };
            dialog.Show();
            dialog.UpdateLayout();
            var list = (System.Windows.Controls.ListBox)dialog.FindName("QualityList");
            Assert.Equal(3, list.Items.Count);
            Assert.Same(dialog.ViewModel.Selected, list.SelectedItem);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)((FrameworkElement)dialog.FindName("AudioBox")).Parent).Visibility);
            dialog.Close();
        });
    }
}
