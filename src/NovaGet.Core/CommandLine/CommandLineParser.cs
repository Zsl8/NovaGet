namespace NovaGet.Core.CommandLine;

public static class CommandLineParser
{
    private static readonly string[] s_urlSchemes = ["http://", "https://", "ftp://", "ftps://", "ftpes://"];

    /// <summary>Parses switches case-insensitively. Accepts <c>/x</c>, <c>-x</c> and <c>--x</c> forms.</summary>
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new CommandLineOptions();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (!IsSwitch(arg))
            {
                // A bare address (e.g. from the novaget:// protocol handler) means "download this".
                var bare = NormalizeUrlArgument(arg);
                if (bare is not null && options.Url is null)
                {
                    options.Url = bare;
                }
                else
                {
                    options.Errors.Add($"Unexpected argument: {arg}");
                }

                continue;
            }

            var name = arg.TrimStart('/', '-').ToLowerInvariant();
            switch (name)
            {
                case "d":
                    if (TryTakeValue(args, ref i, options, name, out var url))
                    {
                        var normalized = NormalizeUrlArgument(url);
                        if (normalized is null)
                        {
                            options.Errors.Add($"Unsupported address: {url}");
                        }
                        else
                        {
                            options.Url = normalized;
                        }
                    }

                    break;
                case "p":
                    if (TryTakeValue(args, ref i, options, name, out var folder))
                    {
                        options.SaveFolder = folder;
                    }

                    break;
                case "f":
                    if (TryTakeValue(args, ref i, options, name, out var file))
                    {
                        options.FileName = file;
                    }

                    break;
                case "startqueue":
                    if (TryTakeValue(args, ref i, options, name, out var startQueue))
                    {
                        options.StartQueues.Add(startQueue);
                    }

                    break;
                case "stopqueue":
                    if (TryTakeValue(args, ref i, options, name, out var stopQueue))
                    {
                        options.StopQueues.Add(stopQueue);
                    }

                    break;
                case "s":
                    options.StartMainQueue = true;
                    break;
                case "q":
                    options.ExitWhenDone = true;
                    break;
                case "h":
                    options.HangUpWhenDone = true;
                    break;
                case "n":
                    options.Silent = true;
                    break;
                case "a":
                    options.AddToQueueOnly = true;
                    break;
                case "tray":
                    options.StartInTray = true;
                    break;
                case "exit":
                    options.Exit = true;
                    break;
                case "cleanup":
                    options.Cleanup = true;
                    break;
                default:
                    // Windows starts the app with -ToastActivated (or -Embedding) when a toast is clicked after it exited.
                    if (!s_activationSwitches.Contains(name))
                    {
                        options.Errors.Add($"Unknown switch: {arg}");
                    }

                    break;
            }
        }

        return options;
    }

    private static readonly HashSet<string> s_activationSwitches = new(StringComparer.OrdinalIgnoreCase) { "toastactivated", "embedding" };

    /// <summary>
    /// Accepts http/https/ftp/ftps addresses and unwraps <c>novaget://</c> links
    /// (<c>novaget://https://host/file</c>, <c>novaget:https://host/file</c>, <c>novaget://download?url=…</c>).
    /// Returns null for anything else.
    /// </summary>
    public static string? NormalizeUrlArgument(string value)
    {
        var text = value.Trim().Trim('"');
        const string scheme = "novaget:";
        if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            text = text[scheme.Length..].TrimStart('/');
            var queryIndex = text.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
            if (text.StartsWith("download", StringComparison.OrdinalIgnoreCase) && queryIndex >= 0)
            {
                var encoded = text[(queryIndex + 4)..];
                var amp = encoded.IndexOf('&', StringComparison.Ordinal);
                if (amp >= 0)
                {
                    encoded = encoded[..amp];
                }

                text = Uri.UnescapeDataString(encoded);
            }
            else if (!HasSupportedScheme(text))
            {
                // Browsers sometimes collapse "https://" to "https:/" inside custom-protocol links.
                text = RepairCollapsedScheme(text);
            }
        }

        if (!HasSupportedScheme(text) || !Uri.TryCreate(text, UriKind.Absolute, out _))
        {
            return null;
        }

        return text;
    }

    private static bool HasSupportedScheme(string text) =>
        s_urlSchemes.Any(s => text.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    private static string RepairCollapsedScheme(string text)
    {
        foreach (var s in s_urlSchemes)
        {
            var collapsed = s[..^1]; // "https:/"
            if (text.StartsWith(collapsed, StringComparison.OrdinalIgnoreCase))
            {
                return s + text[collapsed.Length..];
            }
        }

        return text;
    }

    private static bool IsSwitch(string arg) =>
        (arg.StartsWith('/') || arg.StartsWith('-')) && arg.Length > 1 && !arg.StartsWith("//", StringComparison.Ordinal);

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, CommandLineOptions options, string name, out string value)
    {
        if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]) && !IsSwitch(args[i + 1]))
        {
            value = args[++i].Trim().Trim('"');
            return true;
        }

        // Allow paths such as "/p /home/x" on non-Windows by accepting the next token anyway for /p.
        if (name == "p" && i + 1 < args.Count && args[i + 1].StartsWith('/'))
        {
            value = args[++i];
            return true;
        }

        options.Errors.Add($"Switch /{name} needs a value.");
        value = string.Empty;
        return false;
    }
}
