# App and web icons

`icon.svg` is the source: a prompt chevron with its cursor and the spark of an agent answering it, without letters, so
every brand can use it. Everything else here is rendered from it on a Mac, with the system's own SVG support:

```bash
swift tools/icons/render.swift brands/icons web/apps/web/public
```

- `AppIcon.icns`: the macOS app, inside Apple's icon grid with its shadow (the bundle's `CFBundleIconFile`, and the Dock
  icon of an app started with `dotnet run`).
- `icon.ico`: the Windows executable (`ApplicationIcon`), whose windows and taskbar button show it.
- `icon-<size>.png`: the sizes the web page and Linux need; the script copies the web's into `web/apps/web/public`.

A brand with its own icon sets `BrandIcons` in its `brands/<brand>.props` to a folder with the same files.
