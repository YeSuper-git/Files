@echo off
chcp 65001 >nul
title Files AV Resource Manager - 安装程序
echo.
echo  ========================================
echo   Files AV Resource Manager 安装程序
echo  ========================================
echo.

:: 检查管理员权限
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [提示] 需要管理员权限，正在请求提升...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo [1/2] 安装 Windows App Runtime...
powershell -Command "Add-AppxPackage -Path '%~dp0Microsoft.WindowsAppRuntime.2.msix'" 2>nul
if %errorlevel% equ 0 (
    echo       完成
) else (
    echo       已安装或安装失败，继续...
)

echo.
echo [2/2] 安装 Files AV Resource Manager...
powershell -Command "Add-AppxPackage -Path '%~dp0Files.App_4.2.31.0_x64.msix'"
if %errorlevel% equ 0 (
    echo.
    echo  ========================================
    echo   安装完成！
    echo   在开始菜单搜索 "Files" 即可打开
    echo  ========================================
) else (
    echo.
    echo  [错误] 安装失败，请确保：
    echo   1. 已开启旁加载应用或开发者模式
    echo   2. 系统为 Windows 10/11
    echo.
)

echo.
pause
