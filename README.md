# PomoDeck

A flow-state Pomodoro timer for the Logitech MX Creative Console.

PomoDeck pairs a desktop app (Tauri + Svelte + Rust) with a hardware plugin (C# + SkiaSharp) to create a focus system that spans your screen and your desk.

## Structure

```
pomotroid/          — Desktop app (Tauri 2, Svelte 5, Rust)
PomodoroPlugin/     — Loupedeck plugin (C#, SkiaSharp, .NET 8)
```

## Build

### App
```bash
cd pomotroid
npm install
npm run tauri build
```
Output: `src-tauri/target/release/bundle/nsis/PomoDeck_1.5.0_x64-setup.exe`

### Plugin
```bash
cd PomodoroPlugin/src
dotnet build -c Release
```
Then zip `PomodoroPlugin/bin/Release/` as `PomoDeck.lplug4`

## Dev

```bash
cd pomotroid && npm run tauri dev    # app
cd PomodoroPlugin/src && dotnet build # plugin (auto-reloads)
```

## License

MIT — see [LICENSE](pomotroid/LICENSE)
