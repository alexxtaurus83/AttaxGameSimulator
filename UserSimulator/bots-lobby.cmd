@echo off
setlocal

rem S11.1 bots-lobby runner
rem Usage: bots-lobby.cmd [server_url]
rem Default server: http://localhost:5000
rem Total cores budget: 12 (6 cores x 2 threads) distributed across 5 bots: 2, 2, 2, 3, 3 = 12 total.

set SERVER_URL=%~1
if "%SERVER_URL%"=="" set SERVER_URL=http://localhost:5000

echo Starting 5 Lobby Bots against %SERVER_URL% (12 cores total: 2, 2, 2, 3, 3)...
echo Bot 1 and Bot 2: active challengers (search)
echo Bot 3, Bot 4, Bot 5: waiting opponents (wait)

start "Bot 1 (search, depth=2, cores=2)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot1 --mode lobby --lobby-behavior search --spec "classic:depth=2" --cores 2
start "Bot 2 (search, depth=2, cores=2)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot2 --mode lobby --lobby-behavior search --spec "classic:depth=2" --cores 2
start "Bot 3 (wait, depth=2, cores=2)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot3 --mode lobby --lobby-behavior wait --spec "classic:depth=2" --cores 2
start "Bot 4 (wait, depth=3, cores=3)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot4 --mode lobby --lobby-behavior wait --spec "classic:depth=3" --cores 3
start "Bot 5 (wait, depth=3, cores=3)" dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- --server %SERVER_URL% --name bot5 --mode lobby --lobby-behavior wait --spec "classic:depth=3" --cores 3

echo All 5 bots started in separate windows.
