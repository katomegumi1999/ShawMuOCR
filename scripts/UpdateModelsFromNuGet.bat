@echo off
chcp 65001 >nul
title 小沐 OCR - 自动更新官方图优化模型
echo 正在检查环境并从 NuGet 获取最新官方图优化模型...
where pwsh >nul 2>nul
if %errorlevel% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0UpdateModelsFromNuGet.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0UpdateModelsFromNuGet.ps1"
)
echo.
pause
