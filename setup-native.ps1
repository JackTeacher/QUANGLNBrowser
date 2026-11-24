<#
.SYNOPSIS
    Tự động tải và giải nén các binary native (win-x64) cho dự án QUANGLN Browser.

.DESCRIPTION
    Script tải phiên bản mới nhất của:
      1. sing-box.exe   (SagerNet/sing-box - VPN Core)
      2. ffmpeg.exe     (BtbN/FFmpeg-Builds - HLS/DASH Remuxer, kèm tùy chọn ffprobe.exe)
      3. yt-dlp.exe     (yt-dlp/yt-dlp - Site Media Extractor)
      4. libmpv-2.dll   (shinchiro/mpv-winbuild-cmake - Trình xem Video độc lập)

    Tất cả file sau khi giải nén được đưa vào thư mục native/win-x64, gỡ cờ Mark-of-the-Web
    (Unblock-File) và kiểm tra trực tiếp PE Header để xác nhận chuẩn kiến trúc x64 (AMD64).

.PARAMETER TargetDir
    Thư mục đích chứa binary. Mặc định: <Thư mục script>\native\win-x64

.PARAMETER Force
    Tải lại và ghi đè ngay cả khi file đã tồn tại.

.PARAMETER SkipFfprobe
    Bỏ qua việc trích xuất ffprobe.exe từ gói FFmpeg (mặc định có lấy kèm để hỗ trợ yt-dlp).

.PARAMETER GitHubToken
    Personal Access Token (tùy chọn) để tránh giới hạn 60 request/giờ của GitHub API.

.EXAMPLE
    .\setup-native.ps1
    .\setup-native.ps1 -Force
    .\setup-native.ps1 -TargetDir "D:\Projects\QUANGLNBrowser\native\win-x64"
#>

[CmdletBinding()]
param(
    [string]$TargetDir = "",
    [switch]$Force,
    [switch]$SkipFfprobe,
    [string]$GitHubToken = $env:GITHUB_TOKEN
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($TargetDir)) { $TargetDir = Join-Path (Get-Location).Path "native\win-x64" }

# Bật TLS 1.2+ cho Windows PowerShell 5.1
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch { }

# ==============================================================================
# HÀM TIỆN ÍCH (HELPERS)
# ==============================================================================

function Write-Banner {
    Write-Host ""
    Write-Host "=================================================================" -ForegroundColor Cyan
    Write-Host "   QUANGLN BROWSER - NATIVE BINARIES SETUP (WIN-X64)" -ForegroundColor Cyan
    Write-Host "=================================================================" -ForegroundColor Cyan
    Write-Host "  Thư mục đích : $TargetDir" -ForegroundColor Gray
    Write-Host "  Chế độ       : $(if ($Force) { 'Force (Ghi đè tất cả)' } else { 'Chỉ tải file còn thiếu' })" -ForegroundColor Gray
    Write-Host ""
}

function Write-Step {
    param([string]$Message)
    Write-Host "[*] $Message" -ForegroundColor Yellow
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    [OK] $Message" -ForegroundColor Green
}

function Write-Skip {
    param([string]$Message)
    Write-Host "    [SKIP] $Message" -ForegroundColor DarkGray
}

function Write-WarnMsg {
    param([string]$Message)
    Write-Host "    [WARN] $Message" -ForegroundColor DarkYellow
}

function Get-GitHubHeaders {
    $headers = @{
        "User-Agent" = "QUANGLN-Browser-Setup/1.0"
        "Accept"     = "application/vnd.github+json"
    }
    if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
        $headers["Authorization"] = "Bearer $GitHubToken"
    }
    return $headers
}

function Get-GitHubReleaseAsset {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Pattern,
        [string]$FallbackPattern = ""
    )

    $apiUrl = "https://api.github.com/repos/$Repo/releases/latest"
    $release = Invoke-RestMethod -Uri $apiUrl -Headers (Get-GitHubHeaders) -TimeoutSec 30

    $asset = $release.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
    if (-not $asset -and -not [string]::IsNullOrWhiteSpace($FallbackPattern)) {
        $asset = $release.assets | Where-Object { $_.name -match $FallbackPattern } | Select-Object -First 1
    }

    if (-not $asset) {
        throw "Không tìm thấy asset khớp mẫu '$Pattern' trong release mới nhất của $Repo (Tag: $($release.tag_name))."
    }

    return [PSCustomObject]@{
        TagName = $release.tag_name
        Name    = $asset.name
        Url     = $asset.browser_download_url
        SizeMB  = [Math]::Round($asset.size / 1MB, 2)
    }
}

function Invoke-DownloadFile {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Destination
    )

    Write-Host "    Đang tải: $Url" -ForegroundColor DarkCyan

    # Ưu tiên curl.exe tích hợp sẵn của Windows 10/11 vì tốc độ tải và hỗ trợ redirect ổn định hơn PS 5.1
    $curlCmd = Get-Command "curl.exe" -ErrorAction SilentlyContinue
    if ($curlCmd) {
        & $curlCmd.Source -L --fail --retry 3 --retry-delay 2 --progress-bar -o $Destination $Url
        if ($LASTEXITCODE -ne 0) {
            throw "curl.exe tải thất bại (ExitCode=$LASTEXITCODE): $Url"
        }
        return
    }

    # Fallback sang Invoke-WebRequest (tắt ProgressBar để tăng tốc 5-10x trên PS 5.1)
    $oldProgress = $ProgressPreference
    try {
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -MaximumRedirection 10
    } finally {
        $ProgressPreference = $oldProgress
    }
}

function Expand-7zArchive {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$DestinationDir,
        [Parameter(Mandatory)][string]$TempRoot
    )

    New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null

    # Cách 1: Kiểm tra 7-Zip đã cài sẵn trên máy
    $sevenZipPaths = @(
        (Get-Command "7z.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    $sevenZipPaths = @($sevenZipPaths | Where-Object { $_ }); if ($sevenZipPaths.Count -gt 0) {
        $sevenZip = $sevenZipPaths[0]
        Write-Host "    Giải nén .7z bằng 7-Zip ($sevenZip)..." -ForegroundColor Gray
        & $sevenZip x $ArchivePath "-o$DestinationDir" -y | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
    }

    # Cách 2: Thử tar.exe gốc của Windows 11 (libarchive hỗ trợ .7z từ Win11 23H2+)
    $tarCmd = Get-Command "tar.exe" -ErrorAction SilentlyContinue
    if ($tarCmd) {
        Write-Host "    Thử giải nén .7z bằng Windows tar.exe..." -ForegroundColor Gray
        try { & $tarCmd.Source -xf $ArchivePath -C $DestinationDir } catch { }
        if ($LASTEXITCODE -eq 0 -and (Get-ChildItem -Path $DestinationDir -Recurse -File | Measure-Object).Count -gt 0) {
            return
        }
    }

    # Cách 3: Tự động tải công cụ chính chủ 7zr.exe (standalone ~600KB) vào thư mục tạm
    $sevenZrPath = Join-Path $TempRoot "7zr.exe"
    if (-not (Test-Path $sevenZrPath)) {
        Write-Host "    Máy chưa cài 7-Zip. Tự động tải 7zr.exe tạm thời từ 7-zip.org..." -ForegroundColor Gray
        Invoke-DownloadFile -Url "https://www.7-zip.org/a/7zr.exe" -Destination $sevenZrPath
    }

    & $sevenZrPath x $ArchivePath "-o$DestinationDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Giải nén file .7z thất bại với 7zr.exe (ExitCode=$LASTEXITCODE)."
    }
}

function Test-IsPeX64Binary {
    param([Parameter(Mandatory)][string]$FilePath)

    if (-not (Test-Path $FilePath)) { return $false }

    $stream = [System.IO.File]::OpenRead($FilePath)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        if ($stream.Length -lt 64) { return $false }

        # Kiểm tra chữ ký DOS Header "MZ" (0x5A4D)
        $mz = $reader.ReadUInt16()
        if ($mz -ne 0x5A4D) { return $false }

        # Đọc offset e_lfanew tại 0x3C
        $stream.Seek(0x3C, [System.IO.SeekOrigin]::Begin) | Out-Null
        $peOffset = $reader.ReadInt32()
        if ($peOffset -le 0 -or ($peOffset + 6) -gt $stream.Length) { return $false }

        # Kiểm tra chữ ký "PE\0\0" (0x00004550)
        $stream.Seek($peOffset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $peSig = $reader.ReadUInt32()
        if ($peSig -ne 0x00004550) { return $false }

        # Đọc Machine field: 0x8664 = IMAGE_FILE_MACHINE_AMD64 (x64)
        $machine = $reader.ReadUInt16()
        return ($machine -eq 0x8664)
    } finally {
        $stream.Dispose()
    }
}

# ==============================================================================
# QUY TRÌNH TẢI & GIẢI NÉN CHÍNH
# ==============================================================================

Write-Banner

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("QUANGLNBrowser_NativeSetup_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    # --------------------------------------------------------------------------
    # 1. YT-DLP (yt-dlp.exe)
    # --------------------------------------------------------------------------
    Write-Step "[1/4] Kiểm tra yt-dlp.exe (Site Media Extractor)..."
    $ytdlpDest = Join-Path $TargetDir "yt-dlp.exe"
    if (-not $Force -and (Test-Path $ytdlpDest)) {
        Write-Skip "yt-dlp.exe đã tồn tại. Dùng -Force để tải mới."
    } else {
        $ytdlpTmp = Join-Path $tempRoot "yt-dlp.exe"
        Invoke-DownloadFile -Url "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -Destination $ytdlpTmp
        Copy-Item -Path $ytdlpTmp -Destination $ytdlpDest -Force
        Unblock-File -Path $ytdlpDest -ErrorAction SilentlyContinue
        Write-Ok "Đã cài đặt yt-dlp.exe"
    }

    # --------------------------------------------------------------------------
    # 2. SING-BOX (sing-box.exe - windows-amd64)
    # --------------------------------------------------------------------------
    Write-Step "[2/4] Kiểm tra sing-box.exe (VPN Core - SagerNet/sing-box)..."
    $singboxDest = Join-Path $TargetDir "sing-box.exe"
    if (-not $Force -and (Test-Path $singboxDest)) {
        Write-Skip "sing-box.exe đã tồn tại. Dùng -Force để tải mới."
    } else {
        # Lọc đúng bản chuẩn windows-amd64.zip (loại bỏ bản legacy-windows-7 / fips)
        $sbAsset = Get-GitHubReleaseAsset `
            -Repo "SagerNet/sing-box" `
            -Pattern "^sing-box-[\d\.]+-windows-amd64\.zip$" `
            -FallbackPattern "windows-amd64\.zip$"

        Write-Host "    Phiên bản mới nhất: $($sbAsset.TagName) ($($sbAsset.Name) - $($sbAsset.SizeMB) MB)" -ForegroundColor Gray
        $sbZip = Join-Path $tempRoot $sbAsset.Name
        $sbExtractDir = Join-Path $tempRoot "singbox_extract"

        Invoke-DownloadFile -Url $sbAsset.Url -Destination $sbZip
        Expand-Archive -Path $sbZip -DestinationPath $sbExtractDir -Force

        $sbExe = Get-ChildItem -Path $sbExtractDir -Filter "sing-box.exe" -Recurse -File | Select-Object -First 1
        if (-not $sbExe) { throw "Không tìm thấy sing-box.exe bên trong gói giải nén." }

        Copy-Item -Path $sbExe.FullName -Destination $singboxDest -Force
        Unblock-File -Path $singboxDest -ErrorAction SilentlyContinue
        Write-Ok "Đã cài đặt sing-box.exe ($($sbAsset.TagName))"
    }

    # --------------------------------------------------------------------------
    # 3. FFMPEG (ffmpeg.exe + ffprobe.exe - BtbN win64 GPL static)
    # --------------------------------------------------------------------------
    Write-Step "[3/4] Kiểm tra ffmpeg.exe (HLS/DASH Remuxer - BtbN/FFmpeg-Builds)..."
    $ffmpegDest = Join-Path $TargetDir "ffmpeg.exe"
    $ffprobeDest = Join-Path $TargetDir "ffprobe.exe"
    $needFfmpeg = $Force -or (-not (Test-Path $ffmpegDest)) -or ((-not $SkipFfprobe) -and (-not (Test-Path $ffprobeDest)))

    if (-not $needFfmpeg) {
        Write-Skip "ffmpeg.exe đã tồn tại. Dùng -Force để tải mới."
    } else {
        $ffZip = Join-Path $tempRoot "ffmpeg-master-latest-win64-gpl.zip"
        $ffExtractDir = Join-Path $tempRoot "ffmpeg_extract"

        Invoke-DownloadFile -Url "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip" -Destination $ffZip
        Write-Host "    Đang giải nén gói FFmpeg..." -ForegroundColor Gray
        Expand-Archive -Path $ffZip -DestinationPath $ffExtractDir -Force

        $ffExe = Get-ChildItem -Path $ffExtractDir -Filter "ffmpeg.exe" -Recurse -File | Select-Object -First 1
        if (-not $ffExe) { throw "Không tìm thấy ffmpeg.exe bên trong gói FFmpeg." }
        Copy-Item -Path $ffExe.FullName -Destination $ffmpegDest -Force
        Unblock-File -Path $ffmpegDest -ErrorAction SilentlyContinue
        Write-Ok "Đã cài đặt ffmpeg.exe"

        if (-not $SkipFfprobe) {
            $fpExe = Get-ChildItem -Path $ffExtractDir -Filter "ffprobe.exe" -Recurse -File | Select-Object -First 1
            if ($fpExe) {
                Copy-Item -Path $fpExe.FullName -Destination $ffprobeDest -Force
                Unblock-File -Path $ffprobeDest -ErrorAction SilentlyContinue
                Write-Ok "Đã cài đặt kèm ffprobe.exe (hỗ trợ yt-dlp phân tích luồng)"
            }
        }
    }

    # --------------------------------------------------------------------------
    # 4. LIBMPV (libmpv-2.dll - shinchiro/mpv-winbuild-cmake)
    # --------------------------------------------------------------------------
    Write-Step "[4/4] Kiểm tra libmpv-2.dll (Trình xem Video độc lập - shinchiro/mpv-winbuild-cmake)..."
    $libmpvDest = Join-Path $TargetDir "libmpv-2.dll"
    if (-not $Force -and (Test-Path $libmpvDest)) {
        Write-Skip "libmpv-2.dll đã tồn tại. Dùng -Force để tải mới."
    } else {
        # Ưu tiên bản x86_64 tiêu chuẩn (tương thích mọi CPU x64), fallback sang x86_64-v3 nếu không có
        $mpvAsset = Get-GitHubReleaseAsset `
            -Repo "shinchiro/mpv-winbuild-cmake" `
            -Pattern "^mpv-dev-x86_64-(?!v3).*\.7z$" `
            -FallbackPattern "^mpv-dev-x86_64.*\.7z$"

        Write-Host "    Phiên bản mới nhất: $($mpvAsset.TagName) ($($mpvAsset.Name) - $($mpvAsset.SizeMB) MB)" -ForegroundColor Gray
        $mpv7z = Join-Path $tempRoot $mpvAsset.Name
        $mpvExtractDir = Join-Path $tempRoot "libmpv_extract"

        Invoke-DownloadFile -Url $mpvAsset.Url -Destination $mpv7z
        Expand-7zArchive -ArchivePath $mpv7z -DestinationDir $mpvExtractDir -TempRoot $tempRoot

        $mpvDll = Get-ChildItem -Path $mpvExtractDir -Filter "libmpv-2.dll" -Recurse -File | Select-Object -First 1
        if (-not $mpvDll) { throw "Không tìm thấy libmpv-2.dll bên trong gói $($mpvAsset.Name)." }

        Copy-Item -Path $mpvDll.FullName -Destination $libmpvDest -Force
        Unblock-File -Path $libmpvDest -ErrorAction SilentlyContinue
        Write-Ok "Đã cài đặt libmpv-2.dll ($($mpvAsset.TagName))"
    }

    # ==========================================================================
    # KIỂM TRA XÁC NHẬN & BÁO CÁO KẾT QUẢ (PE x64 VERIFICATION)
    # ==========================================================================
    Write-Host ""
    Write-Host "=================================================================" -ForegroundColor Cyan
    Write-Host "   KẾT QUẢ KIỂM TRA NATIVE BINARIES (QUANGLN BROWSER)" -ForegroundColor Cyan
    Write-Host "=================================================================" -ForegroundColor Cyan

    $expectedFiles = @("sing-box.exe", "ffmpeg.exe", "yt-dlp.exe", "libmpv-2.dll")
    if (-not $SkipFfprobe) { $expectedFiles += "ffprobe.exe" }

    $allValid = $true
    $report = foreach ($fileName in $expectedFiles) {
        $fullPath = Join-Path $TargetDir $fileName
        $exists = Test-Path $fullPath
        $sizeMB = if ($exists) { [Math]::Round((Get-Item $fullPath).Length / 1MB, 2) } else { 0 }
        $isX64 = if ($exists) { Test-IsPeX64Binary -FilePath $fullPath } else { $false }

        if (-not ($exists -and $isX64)) { $allValid = $false }

        [PSCustomObject]@{
            File       = $fileName
            Status     = if ($exists) { "OK" } else { "MISSING" }
            Arch       = if ($isX64) { "x64 (AMD64)" } elseif ($exists) { "KHÔNG PHẢI X64!" } else { "-" }
            "Size(MB)" = $sizeMB
        }
    }

    $report | Format-Table -AutoSize

    if (-not (Test-Path (Join-Path $TargetDir "adblock_rust.dll"))) {
        Write-WarnMsg "Lưu ý: adblock_rust.dll cần được biên dịch từ Rust crate (adblock-rust cdylib target x86_64-pc-windows-msvc) và chép vào $TargetDir."
    }

    if (-not $allValid) {
        throw "Một hoặc nhiều binary native bị thiếu hoặc sai kiến trúc x64!"
    }

    Write-Host "Hoàn tất! Bạn có thể chạy 'dotnet build -p:Platform=x64' cho QUANGLN Browser." -ForegroundColor Green
    Write-Host ""
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Path $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
