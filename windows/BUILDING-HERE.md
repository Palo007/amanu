# Building the Windows app on the fork's own PC

This is how the fork's Windows binary is built on the maintainer's PC (Windows 10
Pro, `C:\Backup\CoDiNG\Amanu\amanu-windows`). It is not the release recipe in
`README.md`: that one needs tools this PC does not have. Read this before you
build here, so nobody again reaches for a toolchain that is not installed.

## The stack

| Part | What it is | Where |
|---|---|---|
| App | C# on .NET 10, WPF (plus WinForms for the tray), `net10.0-windows10.0.26100.0`, assembly `Amanu` | `src/Amanu.App` |
| Core | C# class library: settings, sessions, processing | `src/Amanu.Core` |
| Tests | xUnit: `Amanu.Core.Tests`, `Amanu.Live.Tests` (sees `Amanu.App` internals) | `tests/` |
| Solution | `Amanu.Windows.slnx`; package versions in `Directory.Packages.props` | `windows/` |
| Packages | Markdig, NAudio, Velopack | NuGet |
| Final-transcript runtime | transcribe.cpp v0.1.3: `transcribe-cli.exe`, ggml DLLs, `ggml-vulkan.dll` | `artifacts/publish/local-runtime` |
| Live runtime | streaming recognizer DLLs | `artifacts/publish/live-runtime` |
| Echo runtime | `liblocalvqe.dll` and its model, checked in | `native/localvqe` → `echo-runtime` |

The Swift package at the repository root is the macOS app. It does not build on
Windows; `make`, `swift` and Xcode are not used here.

## What this PC has, and what it lacks

- **.NET SDK 10.0.403 in `%USERPROFILE%\.dotnet`.** The `dotnet` on `PATH` is
  `C:\Program Files\dotnet\dotnet.exe`, which has runtimes but **no SDK**: it
  fails with "No .NET SDKs were found". Always put the user SDK first (below).
- **No CMake, no MSVC C++ build tools, no `vswhere`.** So
  `scripts/Build-Release.ps1`, `Build-LocalRuntime.ps1` and
  `Build-LiveRuntime.ps1` cannot run here. Do not try them, and do not install
  a C++ toolchain to work around it.
- The native runtimes were built once elsewhere and live **only** in
  `windows/artifacts/publish/local-runtime` and `live-runtime`. `windows/artifacts/`
  is in `.gitignore`. **Never delete or clean that folder**: on this PC there is
  no way to make those files again. Publish over it instead; `dotnet publish`
  replaces the managed files and leaves the native folders alone.

## Build

1. Quit Amanu first (tray icon → Quit). It runs from `artifacts\publish`, and a
   running copy locks the files. Check with `Get-Process Amanu`.
2. In PowerShell, from `windows\`:

```powershell
$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version   # must print 10.0.x, not an error
```

```powershell
dotnet test Amanu.Windows.slnx
```

```powershell
$version = (Get-Content ..\VERSION -Raw).Trim()
dotnet publish src\Amanu.App\Amanu.App.csproj -c Release -r win-x64 --self-contained true --property:Version=$version -o artifacts\publish
```

3. Run `artifacts\publish\Amanu.exe`. Its file version must match `VERSION`.

The version comes from the root `VERSION` file; pass it, so the exe carries the
same version as the release.

## Portable zip

The fork ships a zip, not the Velopack installer: zip the whole of
`artifacts\publish` (native folders included) as
`artifacts\fork-release\Amanu-<version>-fork-win-x64-portable.zip` and add its
SHA-256 to `SHA256SUMS.txt` beside it.

## When the native runtimes must change

Only on a machine with CMake and the Visual Studio 2022 C++ build tools (or in
CI, `.github/workflows/windows-beta.yml`). Run `scripts\Build-LocalRuntime.ps1`
and `scripts\Build-LiveRuntime.ps1` with `-PublishDirectory` there, and copy the
resulting `local-runtime` and `live-runtime` folders back into
`artifacts\publish` here.
