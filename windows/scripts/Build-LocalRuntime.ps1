param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    # Leave out the GPU (Vulkan) module. Amanu then always uses the CPU.
    [switch]$NoVulkan
)

$ErrorActionPreference = "Stop"
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    throw "Building the local runtime requires CMake and the MSVC C++ build tools on Windows."
}
$sourceCommit = "a94e021ef658dc7c788837341a13f6acea3baf3c"
$workDirectory = Join-Path ([IO.Path]::GetTempPath()) ("amanu-transcribe-" + [Guid]::NewGuid().ToString("N"))
$sourceDirectory = Join-Path $workDirectory "source"
$buildDirectory = Join-Path $workDirectory "build"
$destinationDirectory = Join-Path $PublishDirectory "local-runtime"

try {
    git clone --depth 1 --branch v0.1.3 https://github.com/handy-computer/transcribe.cpp.git $sourceDirectory
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch the pinned transcribe.cpp source." }
    $actualCommit = (git -C $sourceDirectory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $sourceCommit) {
        throw "transcribe.cpp v0.1.3 did not resolve to the pinned commit."
    }

    # The pinned CLI initializes modules only for --list-devices. Model loads
    # in ordinary and batch passes need the same bootstrap.
    $bootstrapPatch = Join-Path $PSScriptRoot '../patches/transcribe-cli-init-backends.patch'
    git -C $sourceDirectory apply --ignore-space-change $bootstrapPatch
    if ($LASTEXITCODE -ne 0) { throw "Could not apply the pinned CLI backend initialization patch." }

    # The library takes UTF-8 paths, but Windows narrow argv and fopen use ACP.
    $unicodePatch = Join-Path $PSScriptRoot '../patches/transcribe-cli-unicode-paths.patch'
    git -C $sourceDirectory apply --ignore-space-change $unicodePatch
    if ($LASTEXITCODE -ne 0) { throw "Could not apply the pinned CLI Unicode path patch." }

    cmake -S $sourceDirectory -B $buildDirectory `
        -DTRANSCRIBE_BUILD_TESTS=OFF `
        -DTRANSCRIBE_BUILD_TOOLS=OFF `
        -DTRANSCRIBE_BUILD_SHARED=ON `
        -DTRANSCRIBE_GGML_BACKEND_DL=ON `
        -DTRANSCRIBE_VULKAN=OFF `
        -DTRANSCRIBE_USE_OPENMP=OFF `
        -DGGML_NATIVE=OFF `
        -DGGML_CPU_ALL_VARIANTS=ON `
        -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
    if ($LASTEXITCODE -ne 0) { throw "Could not configure transcribe-cli." }
    # Build the CPU modules too. Runtime dispatch selects instructions supported
    # by the user's processor instead of assuming the CI runner's CPU features.
    cmake --build $buildDirectory --config Release --parallel 2
    if ($LASTEXITCODE -ne 0) { throw "Could not build transcribe-cli." }

    $cli = Get-ChildItem -LiteralPath $buildDirectory -Filter transcribe-cli.exe -File -Recurse |
        Where-Object { $_.FullName -match '[\\/]Release[\\/]' } |
        Select-Object -First 1
    if ($null -eq $cli) { throw "The transcribe-cli build produced no Release executable." }
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    Copy-Item -LiteralPath $cli.FullName -Destination (Join-Path $destinationDirectory "transcribe-cli.exe") -Force
    Get-ChildItem -LiteralPath $buildDirectory -Filter '*.dll' -File -Recurse |
        Where-Object { $_.FullName -match '[\\/]Release[\\/]' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destinationDirectory -Force }
    $licenses = Join-Path $destinationDirectory "licenses"
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceDirectory "LICENSE") -Destination (Join-Path $licenses "transcribe.cpp-LICENSE") -Force
    Copy-Item -LiteralPath (Join-Path $sourceDirectory "ggml/LICENSE") -Destination (Join-Path $licenses "ggml-LICENSE") -Force
    if (-not $NoVulkan) {
        # GPU module from the official v0.1.3 release (same pinned source). A
        # Vulkan build here needs the Vulkan SDK; the release file needs only the
        # GPU driver at run time. ggml loads it as a separate backend module, so a
        # PC without a GPU, driver or VC++ runtime just keeps the CPU.
        $vulkanArchive = Join-Path $workDirectory "vulkan.tar.gz"
        $vulkanUrl = "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz"
        Invoke-WebRequest -Uri $vulkanUrl -OutFile $vulkanArchive -UseBasicParsing
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $vulkanArchive).Hash -ne "9F536CB0FB839BD305E6D92FB214FD417C7718A416A6C7646A9911FBD56FDAD5") {
            throw "The transcribe.cpp v0.1.3 Vulkan release file did not match its pinned hash."
        }
        $vulkanDirectory = Join-Path $workDirectory "vulkan"
        New-Item -ItemType Directory -Force -Path $vulkanDirectory | Out-Null
        tar -xzf $vulkanArchive -C $vulkanDirectory "transcribe-native-windows-x86_64-cpu-vulkan/ggml-vulkan.dll"
        if ($LASTEXITCODE -ne 0) { throw "Could not unpack the Vulkan module." }
        $vulkanModule = Join-Path $vulkanDirectory "transcribe-native-windows-x86_64-cpu-vulkan/ggml-vulkan.dll"
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $vulkanModule).Hash -ne "658260FF65065606198AE7303467444B5AF5CE1EE07258B3B54EAF35A09B6AF5") {
            throw "The Vulkan module did not match its pinned hash."
        }
        Copy-Item -LiteralPath $vulkanModule -Destination $destinationDirectory -Force
    }
    & (Join-Path $PSScriptRoot 'Test-NativeCpu.ps1') -RuntimeDirectory $destinationDirectory
    & (Join-Path $PSScriptRoot '../tests/Test-NativeUnicodePaths.ps1') -RuntimeDirectory $destinationDirectory
    Write-Host "Bundled transcribe-cli from $actualCommit"
}
finally {
    if ([IO.Path]::GetFullPath($workDirectory).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $workDirectory)) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}
