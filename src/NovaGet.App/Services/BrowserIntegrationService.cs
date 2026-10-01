using System.Windows;
using Microsoft.Extensions.Logging;
using NovaGet.App.Localization;
using NovaGet.Core;
using NovaGet.Core.Integration;
using NovaGet.Core.Ipc;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// Messages from the browser extension (relayed by the native host). Every message is validated first; downloads
/// and links are handed to the UI and acknowledged at once, so the browser never waits on a dialog. A failure reply
/// tells the extension to let the browser download the file itself.
/// </summary>
internal sealed class BrowserIntegrationService(
    ISettingsService settings,
    Lazy<IAppController> controller,
    DownloadUiService downloadUi,
    ILogger<BrowserIntegrationService> logger)
{
    public const string Disabled = "disabled";

    public IpcResponse Handle(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var browser = request.Args is [var first, ..] && BrowserCatalog.Find(first) is { } known ? known.Id : null;
        if (request.Payload is not { } payload)
        {
            return IpcResponse.Failure("Empty message.");
        }

        var message = BrowserMessageValidator.Validate(payload, out var error);
        if (message is null)
        {
            logger.LogWarning("Rejected a message from the browser extension: {Error}", error);
            return IpcResponse.Failure(error ?? "Invalid message.");
        }

        var current = ExtensionSettings.From(settings.Current, browser);
        switch (message)
        {
            case BrowserSimpleMessage { Type: BrowserMessageTypes.Hello or BrowserMessageTypes.GetSettings }:
                return IpcResponse.Success(IpcJson.ToElement(current));
            case BrowserSimpleMessage { Type: BrowserMessageTypes.Ping }:
                return IpcResponse.Success(IpcJson.ToElement(new { version = AppInfo.InformationalVersion, revision = current.Revision }));
            case BrowserSimpleMessage { Type: BrowserMessageTypes.OpenApp }:
                controller.Value.ShowMainWindow();
                return IpcResponse.Success();
            case BrowserSimpleMessage { Type: BrowserMessageTypes.OpenOptions }:
                OnUi(() => controller.Value.ShowOptions("General"));
                return IpcResponse.Success();
        }

        if (!current.Enabled)
        {
            return IpcResponse.Failure(Disabled);
        }

        switch (message)
        {
            case BrowserDownload { PostData: not null }:
                // The engine downloads with GET only; a POST form result stays with the browser.
                return IpcResponse.Failure("post");
            case BrowserDownload download:
                logger.LogInformation("Download from {Browser}: {Url}", browser ?? "browser", download.Url);
                OnUi(() => _ = downloadUi.AddFromBrowserAsync(download));
                return Accepted();
            case BrowserLinks links:
                var title = string.IsNullOrWhiteSpace(links.PageTitle) ? links.PageUrl?.Host ?? Localizer.Get("Links_Title") : links.PageTitle;
                OnUi(() => downloadUi.ShowLinks(title, [.. links.Links.Select(l => (l.Url, l.Text))], links.Request, links.PageUrl));
                return Accepted();
            case BrowserMedia { Protected: true }:
                return IpcResponse.Failure("protected");
            case BrowserMedia { Items: [var item, ..] } media when item.IsManifest:
                // The item the user clicked comes first; a playlist goes through the quality list.
                OnUi(() => _ = downloadUi.AddStreamFromBrowserAsync(item.Url, media.PageTitle, media.Request));
                return Accepted();
            case BrowserMedia { Items: [var item, ..] } media:
                OnUi(() => _ = downloadUi.AddFromBrowserAsync(new BrowserDownload(
                    item.Url, null, MediaFileName(media.PageTitle, item), item.Size, item.Mime, media.PageTitle, media.Request, null, Forced: true)));
                return Accepted();
            case BrowserMedia:
                return IpcResponse.Failure("No media.");
            default:
                return IpcResponse.Failure("Unsupported message.");
        }
    }

    /// <summary>Video files are named after the page title, with the extension of the media type.</summary>
    internal static string? MediaFileName(string? pageTitle, BrowserMediaItem item)
    {
        if (string.IsNullOrWhiteSpace(pageTitle))
        {
            return null;
        }

        var fromUrl = System.IO.Path.GetExtension(item.Url.AbsolutePath);
        var extension = NovaGet.Core.Engine.Naming.MimeTypes.ExtensionFor(item.Mime)
            ?? (fromUrl.Length is > 1 and <= 6 ? fromUrl : ".mp4");
        return NovaGet.Core.Engine.Naming.FileNameSanitizer.Sanitize(pageTitle) + extension;
    }

    private static IpcResponse Accepted() => IpcResponse.Success(IpcJson.ToElement(new { accepted = true }));

    private static void OnUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
