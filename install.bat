@echo off
chcp 65001 >nul
title Files AV Resource Manager
echo.
echo  ==========================================
echo   Files AV Resource Manager 安装程序
echo  ==========================================
echo.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [提示] 需要管理员权限，正在请求提升...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)
echo [1/3] 安装签名证书...
powershell -Command "Import-Certificate -FilePath '%~dp0FilesAVManager.cer' -CertStoreLocation Cert:\LocalMachine\Root" >nul 2>&1
echo       完成
echo.
echo [2/3] 安装 Windows App Runtime...
powershell -Command "Add-AppxPackage -Path '%~dp0Microsoft.WindowsAppRuntime.2.msix'" 2>nul
echo       完成
echo.
echo [3/3] 安装 Files AV Resource Manager...
for %%f in ("%~dp0Files.App_*.msix") do (
    powershell -Command "Add-AppxPackage -Path '%%f'"
    goto :done
)
:done
if %errorlevel% equ 0 (
    echo.
    echo  ==========================================
    echo   安装完成! 在开始菜单搜索 Files 打开
    echo  ==========================================
) else (
    echo.
    echo  [错误] 安装失败
    echo  请确保已开启旁加载或开发者模式
    echo  设置 - 开发者选项 - 开发人员模式
)
echo.
pause
