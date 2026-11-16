@echo off
setlocal

set SERVER=http://192.168.0.31:5080
set PROJECT=Attax.UserSimulator\Attax.UserSimulator.csproj

echo Building project...
dotnet build "%PROJECT%" -c Release
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

echo Starting lobby bots...

start "Bot 1" cmd /k dotnet run --project "%PROJECT%" -c Release --no-build --no-restore -- --server %SERVER% --name bot-d1-qt --mode lobby --lobby-behavior wait --spec "classic:depth=1" --new-identity --abilities policy

timeout /t 2 /nobreak >nul

start "Bot 2" cmd /k dotnet run --project "%PROJECT%" -c Release --no-build --no-restore -- --server %SERVER% --name bot-d2-qt --mode lobby --lobby-behavior wait --spec "classic:depth=2" --new-identity --abilities policy

timeout /t 2 /nobreak >nul

start "Bot 3" cmd /k dotnet run --project "%PROJECT%" -c Release --no-build --no-restore -- --server %SERVER% --name bot-d3-qt --mode lobby --lobby-behavior wait --spec "classic:depth=3" --new-identity --abilities policy

timeout /t 2 /nobreak >nul

start "Bot 4" cmd /k dotnet run --project "%PROJECT%" -c Release --no-build --no-restore -- --server %SERVER% --name bot-d1-qf --mode lobby --lobby-behavior wait --spec "classic:depth=1" --new-identity --abilities policy --disable-quiescence

timeout /t 2 /nobreak >nul

start "Bot 5" cmd /k dotnet run --project "%PROJECT%" -c Release --no-build --no-restore -- --server %SERVER% --name bot-d2-qf --mode lobby --lobby-behavior wait --spec "classic:depth=2" --new-identity --abilities policy --disable-quiescence

timeout /t 2 /nobreak >nul

start "Bot 6" cmd /k dotnet run --project "%PROJECT%" -c Release --no-build --no-restore -- --server %SERVER% --name bot-d3-qf --mode lobby --lobby-behavior wait --spec "classic:depth=3" --new-identity --abilities policy --disable-quiescence

echo All bots started.