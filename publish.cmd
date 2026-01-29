@echo off
echo ========================================
echo  AchievoLab Self-Contained Publish
echo ========================================
echo.

echo [1/3] Publishing AnSAM...
dotnet publish AnSAM/AnSAM.csproj -c Release -r win-x64 --self-contained true
if %errorlevel% neq 0 goto :error

echo.
echo [2/3] Publishing RunGame...
dotnet publish RunGame/RunGame.csproj -c Release -r win-x64 --self-contained true
if %errorlevel% neq 0 goto :error

echo.
echo [3/3] Publishing MyOwnGames...
dotnet publish MyOwnGames/MyOwnGames.csproj -c Release -r win-x64 --self-contained true
if %errorlevel% neq 0 goto :error

echo.
echo ========================================
echo  All projects published successfully!
echo ========================================
goto :end

:error
echo.
echo *** Publish failed with error code %errorlevel% ***
exit /b %errorlevel%

:end
