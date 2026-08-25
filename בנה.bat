@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo   Building KolHalashonKiosk ...
echo ============================================

echo.
echo --- Cleaning previous build artifacts ---
rmdir /s /q obj 2>nul
rmdir /s /q bin 2>nul

echo.
echo --- Publishing single-file EXE (self-contained, win-x64) ---
dotnet publish KolHalashonKiosk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
if errorlevel 1 goto error
del /q publish\*.xml publish\*.pdb 2>nul

echo.
echo ============================================
echo   Done:  publish\KolHalashonKiosk.exe
echo ============================================
pause
exit /b 0

:error
echo.
echo ============================================
echo   BUILD FAILED - see the errors above.
echo ============================================
pause
exit /b 1
