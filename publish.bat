@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo [1/2] 发布单文件 self-contained exe (win-x64)...
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o ".\publish"

if errorlevel 1 (
  echo 发布失败
  pause
  exit /b 1
)

echo.
echo [2/2] 完成。可执行文件：
dir /b ".\publish\EcommerceWorkbench.exe"
echo 路径：%~dp0publish\EcommerceWorkbench.exe
echo.
pause
