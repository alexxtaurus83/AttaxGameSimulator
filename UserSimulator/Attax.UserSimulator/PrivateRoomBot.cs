using System;
using System.Threading;
using System.Threading.Tasks;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Gameplay;
using Attax.Server.Contracts.Lobby;
using Attax.Server.Contracts.Transport;
using Serilog;

namespace Attax.UserSimulator {
    public sealed class PrivateRoomBot {
        private readonly BotOptions _options;
        private readonly ServerConnection _connection;
        private readonly GamePlayer _player;
        private readonly ILogger _log;
        private readonly IConsoleIO _io;

        private TaskCompletionSource<PlayerNotificationDto>? _gameStartedTcs;

        public PrivateRoomBot(BotOptions options, ServerConnection connection, GamePlayer player, ILogger log, IConsoleIO io) {
            _options = options;
            _connection = connection;
            _player = player;
            _log = log;
            _io = io;
            _gameStartedTcs = new TaskCompletionSource<PlayerNotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connection.OnPlayerNotification += HandleNotification;
        }

        public async Task RunAsync(CancellationToken ct = default) {
            try {
                if (_options.Role == "host") {
                    await RunHostAsync(ct);
                } else {
                    await RunGuestAsync(ct);
                }
            } finally {
                // Keep notification listener active
            }
        }

        private async Task RunHostAsync(CancellationToken ct) {
            _log.Information("Creating private room as host");
            var req = new CreateRoomRequest { RoomName = _options.Name };
            var reply = await _connection.CallAsync<RoomDto>("CreateRoom", req);

            _io.WriteLine($"ROOM CODE: {reply.RoomCode}");
            _log.Information("ROOM CODE: {RoomCode} (RoomId: {RoomId})", reply.RoomCode, reply.RoomId);

            var file = new RoomCodeFile {
                RoomCode = reply.RoomCode,
                RoomId = reply.RoomId,
                HostName = _options.Name,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = reply.ExpiresAtUtc
            };
            RoomCodeFolder.Write(_options.RoomsDir, file);

            try {
                _log.Information("Waiting for guest to join room {RoomCode}...", reply.RoomCode);
                var started = await _gameStartedTcs!.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);

                RoomCodeFolder.Delete(_options.RoomsDir, reply.RoomCode);

                var myColor = started.AssignedColor == PlayerColorDto.Red ? PlayerColor.Red : PlayerColor.Blue;
                await _player.PlayAsync(started.GameId!, myColor, ct);
            } catch (TimeoutException) {
                _log.Warning("Private room {RoomCode} expired after 10 minutes", reply.RoomCode);
                RoomCodeFolder.Delete(_options.RoomsDir, reply.RoomCode);
            } catch (Exception) {
                RoomCodeFolder.Delete(_options.RoomsDir, reply.RoomCode);
                throw;
            }
        }

        private async Task RunGuestAsync(CancellationToken ct) {
            string? codeToJoin = _options.RoomCode;

            while (string.IsNullOrEmpty(codeToJoin) && !ct.IsCancellationRequested) {
                var rooms = RoomCodeFolder.ListValid(_options.RoomsDir, DateTimeOffset.UtcNow);
                if (rooms.Count == 0) {
                    _io.WriteLine("No available rooms found. Press 'r' to refresh, 'q' to quit, or type a room code:");
                    string? input = _io.ReadLine()?.Trim();
                    if (input == "q" || input == "Q") return;
                    if (input == "r" || input == "R" || string.IsNullOrEmpty(input)) continue;
                    codeToJoin = input;
                    break;
                }

                _io.WriteLine("Available Private Rooms (newest first):");
                for (int i = 0; i < rooms.Count; i++) {
                    _io.WriteLine($"[{i + 1}] Code: {rooms[i].RoomCode} | Host: {rooms[i].HostName} | Created: {rooms[i].CreatedAtUtc:HH:mm:ss}");
                }
                _io.WriteLine("Enter number to join, 'r' to refresh, 'q' to quit, or type a room code:");
                string? choice = _io.ReadLine()?.Trim();
                if (choice == "q" || choice == "Q") return;
                if (choice == "r" || choice == "R" || string.IsNullOrEmpty(choice)) continue;

                if (int.TryParse(choice, out int num) && num >= 1 && num <= rooms.Count) {
                    codeToJoin = rooms[num - 1].RoomCode;
                } else {
                    codeToJoin = choice;
                }
            }

            if (string.IsNullOrEmpty(codeToJoin)) return;

            _log.Information("Joining private room with code: {RoomCode}", codeToJoin);
            try {
                var req = new JoinRoomRequest { RoomCode = codeToJoin };
                var reply = await _connection.CallAsync<RoomDto>("JoinRoom", req);
                _log.Information("Joined private room successfully. GameId: {GameId}", reply.GameId);

                var started = await _gameStartedTcs!.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                var myColor = started.AssignedColor == PlayerColorDto.Red ? PlayerColor.Red : PlayerColor.Blue;
                await _player.PlayAsync(started.GameId!, myColor, ct);
            } catch (ServerErrorException ex) when (ex.Code == "ROOM_NOT_FOUND" || ex.Code == "ROOM_EXPIRED" || ex.Code == "ROOM_NOT_JOINABLE") {
                _log.Warning("Failed to join room: {Code}. Removing cached code.", ex.Code);
                RoomCodeFolder.Delete(_options.RoomsDir, codeToJoin);
                throw;
            }
        }

        private void HandleNotification(PlayerNotificationDto notif) {
            if (notif.Type == PlayerNotificationTypes.GameStarted && !string.IsNullOrEmpty(notif.GameId)) {
                _log.Information("GameStarted received for {GameId}", notif.GameId);
                _gameStartedTcs?.TrySetResult(notif);
            }
        }
    }
}
