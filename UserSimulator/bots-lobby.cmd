@echo off
setlocal

rem S11.1 bots-lobby runner
rem Usage: bots-lobby.cmd [server_url]
rem Default server: http://localhost:5000
rem Total cores budget: 12 (6 cores x 2 threads) distributed across 5 bots: 2, 2, 2, 3, 3 = 12 total.

set SERVER_URL=%~1
if "%SERVER_URL%"=="" set SERVER_URL=http://localhost:5000

echo Starting Lobby Bots against %SERVER_URL% (12 cores total: 2, 2, 2, 3, 3)...

rem start "Bot 1 (search, depth=2, cores=2)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server https://evial.dev:5080 --name bot1 --mode lobby --lobby-behavior search --spec "classic:depth=2" --cores 2
rem start "Bot 2 (search, depth=2, cores=2)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot2 --mode lobby --lobby-behavior search --spec "classic:depth=2" --cores 2
rem start "Bot 3 (wait, depth=2, cores=2)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot3 --mode lobby --lobby-behavior wait --spec "classic:depth=2" --cores 2
rem start "Bot 4 (wait, depth=3, cores=3)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot4 --mode lobby --lobby-behavior wait --spec "classic:depth=3" --cores 3
rem start "Bot 5 (wait, depth=3, cores=3)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot5 --mode lobby --lobby-behavior wait --spec "classic:depth=3" --cores 3

start "Bot 1" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server https://evial.dev:5080 --name bot-d1-qt --mode lobby --lobby-behavior wait --spec "classic:depth=1" --new-identity --abilities policy
start "Bot 2" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server https://evial.dev:5080 --name bot-d2-qt --mode lobby --lobby-behavior wait --spec "classic:depth=2" --new-identity --abilities policy 
start "Bot 3" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server https://evial.dev:5080 --name bot-d3-qt --mode lobby --lobby-behavior wait --spec "classic:depth=3" --new-identity --abilities policy

start "Bot 4" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server https://evial.dev:5080 --name bot-d1-qf --mode lobby --lobby-behavior wait --spec "classic:depth=1" --new-identity --abilities policy --disable-quiescence
start "Bot 5" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server https://evial.dev:5080 --name bot-d2-qf --mode lobby --lobby-behavior wait --spec "classic:depth=2" --new-identity --abilities policy --disable-quiescence
start "Bot 6" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server https://evial.dev:5080 --name bot-d3-qf --mode lobby --lobby-behavior wait --spec "classic:depth=3" --new-identity --abilities policy --disable-quiescence


echo All bots started in separate windows.
