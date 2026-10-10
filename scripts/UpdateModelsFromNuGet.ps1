# UpdateModelsFromNuGet.ps1
# 自动从 NuGet 下载并提取 Sdcb.SimdPaddleOCR 官方配套的最新图优化模型文件至 Models/ 目录
param(
    [string]$TargetDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $PSScriptRoot "..\Models"
}

$models = @(
    @{
        PackageId = "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny"
        DllName   = "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll"
        DestSubDir = "PP-OCRv6_tiny"
        FileMap   = @{
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.det.onnx" = "PP-OCRv6_det_tiny.onnx"
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.rec.onnx" = "PP-OCRv6_rec_tiny.onnx"
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dict.txt" = "ppocrv6_dict_tiny.txt"
        }
    },
    @{
        PackageId = "Sdcb.SimdPaddleOCR.Models.ChineseV6Small"
        DllName   = "Sdcb.SimdPaddleOCR.Models.ChineseV6Small.dll"
        DestSubDir = "PP-OCRv6_small"
        FileMap   = @{
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Small.det.onnx" = "PP-OCRv6_det_small.onnx"
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Small.rec.onnx" = "PP-OCRv6_rec_small.onnx"
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Small.dict.txt" = "ppocrv6_dict.txt"
        }
    },
    @{
        PackageId = "Sdcb.SimdPaddleOCR.Models.ChineseV6Medium"
        DllName   = "Sdcb.SimdPaddleOCR.Models.ChineseV6Medium.dll"
        DestSubDir = "PP-OCRv6_medium"
        FileMap   = @{
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Medium.det.onnx" = "PP-OCRv6_medium_det.onnx"
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Medium.rec.onnx" = "PP-OCRv6_medium_rec.onnx"
            "Sdcb.SimdPaddleOCR.Models.ChineseV6Medium.dict.txt" = "ppocrv6_dict.txt"
        }
    },
    @{
        PackageId = "Sdcb.SimdPaddleOCR.Models.TextLineOrientation"
        DllName   = "Sdcb.SimdPaddleOCR.Models.TextLineOrientation.dll"
        DestSubDir = ""
        FileMap   = @{
            "Sdcb.SimdPaddleOCR.Models.TextLineOrientation.cls.onnx" = "ch_ppocr_mobile_v2.0_cls.onnx"
        }
    }
)

$targetResolved = [System.IO.Path]::GetFullPath($TargetDir)
Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  ShawMuOCR - Update Models from NuGet" -ForegroundColor Cyan
Write-Host "  Target Models Dir: $targetResolved" -ForegroundColor Gray
Write-Host "==========================================================================" -ForegroundColor Cyan

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ShawMuOCR_ModelUpdate_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    foreach ($m in $models) {
        Write-Host ""
        Write-Host (">>> Checking: " + $m.PackageId + "...") -ForegroundColor Yellow
        $destDir = if ([string]::IsNullOrEmpty($m.DestSubDir)) { $targetResolved } else { Join-Path $targetResolved $m.DestSubDir }
        if (-not (Test-Path $destDir)) {
            New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        }

        # 1. 查询 NuGet 获取最新版本号
        $indexUrl = "https://api.nuget.org/v3-flatcontainer/" + $m.PackageId.ToLowerInvariant() + "/index.json"
        $indexJson = Invoke-RestMethod -Uri $indexUrl -Method Get
        $latestVersion = $indexJson.versions[-1]
        Write-Host ("    Latest version on NuGet: v" + $latestVersion) -ForegroundColor Gray

        # 2. 下载 nupkg
        $nupkgPath = Join-Path $tempRoot ($m.PackageId + "." + $latestVersion + ".nupkg")
        $downloadUrl = "https://api.nuget.org/v3-flatcontainer/" + $m.PackageId.ToLowerInvariant() + "/" + $latestVersion + "/" + $m.PackageId.ToLowerInvariant() + "." + $latestVersion + ".nupkg"
        Write-Host ("    Downloading: " + $downloadUrl + "...") -ForegroundColor Gray
        Invoke-WebRequest -Uri $downloadUrl -OutFile $nupkgPath -UseBasicParsing

        # 3. 从 nupkg 中解压出包含资源的 DLL
        $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
        $dllEntry = $zip.Entries | Where-Object { $_.FullName -like ("*" + $m.DllName) } | Select-Object -First 1
        if ($null -eq $dllEntry) {
            $zip.Dispose()
            throw ("DLL not found in nupkg: " + $m.DllName)
        }

        $tempDllPath = Join-Path $tempRoot $m.DllName
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dllEntry, $tempDllPath, $true)
        $zip.Dispose()

        # 4. 从 DLL 提取嵌入的 ONNX 与字典资源
        $dllBytes = [System.IO.File]::ReadAllBytes($tempDllPath)
        $asm = [System.Reflection.Assembly]::Load([byte[]]$dllBytes)
        foreach ($resName in $m.FileMap.Keys) {
            $targetFileName = $m.FileMap[$resName]
            $outPath = Join-Path $destDir $targetFileName
            $stream = $asm.GetManifestResourceStream($resName)
            if ($null -eq $stream) {
                Write-Warning ("    Resource not found: " + $resName)
                continue
            }
            $outFile = [System.IO.File]::Create($outPath)
            $stream.CopyTo($outFile)
            $outFile.Dispose()
            $stream.Dispose()

            $fi = Get-Item $outPath
            $sizeMb = [Math]::Round($fi.Length / 1MB, 2)
            Write-Host ("    [OK] " + $targetFileName + " (" + $sizeMb + " MB) -> " + $destDir) -ForegroundColor Green
        }
    }
    Write-Host ""
    Write-Host "==========================================================================" -ForegroundColor Cyan
    Write-Host "  All official graph-optimized models updated successfully!" -ForegroundColor Green
    Write-Host "==========================================================================" -ForegroundColor Cyan
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Recurse -Force -Path $tempRoot -ErrorAction SilentlyContinue
    }
}
