@echo off
chcp 65001 >nul
echo.
echo  Збираю «Lumos»...
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo  Не знайдено .NET SDK.
  echo  Встанови .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  echo  Потім запусти цей файл ще раз.
  pause
  exit /b 1
)

dotnet publish "%~dp0src\Lumos.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o "%~dp0out"

if errorlevel 1 (
  echo.
  echo  Збірка не вдалася. Скопіюй текст помилки вище і надішли його Claude.
  pause
  exit /b 1
)

echo.
echo  Готово: %~dp0out\Lumos.exe
explorer "%~dp0out"
pause
