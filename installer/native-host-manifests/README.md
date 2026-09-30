# Native messaging host manifests

Reference templates. The installer generates the real files at install time in `{app}\native-host\`
(see `WriteNativeHostManifests` in `NovaGet.iss`), with the actual install path JSON-escaped and the
extension IDs from `installer/extension-ids.iss`, then points these registry keys at them:

| Browser | Key (HKCU for per-user installs, HKLM for all-users installs) |
|---|---|
| Chrome, Brave, Opera, Vivaldi | `Software\Google\Chrome\NativeMessagingHosts\com.novaget.nativehost` |
| Edge | `Software\Microsoft\Edge\NativeMessagingHosts\com.novaget.nativehost` |
| Chromium | `Software\Chromium\NativeMessagingHosts\com.novaget.nativehost` |
| Firefox | `Software\Mozilla\NativeMessagingHosts\com.novaget.nativehost` |
