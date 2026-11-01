# Attax UserSimulator (Stage S11.1)

Attax UserSimulator is an autonomous headless client designed for live server stress testing, matchmaking rehearsal, and bot games against humans or other bots.

## Features
- **Lobby Mode**: Discovers online players in the lobby, challenges opponents, accepts incoming challenges, and handles concurrent matches.
- **Private Room Mode**:
  - `host`: Creates a private room, writes discovery metadata to `sim-rooms/`, and waits for a human or bot guest.
  - `guest`: Lists available rooms with human-friendly CLI selection, joins via room code, and initiates immediate play.
- **AI Engine Integration**: Uses `Attax.Agents` and `Attax.Play` with customizable `PlayerSpec` (e.g. `classic:depth=3,cores=2`). Note: AI search depth is constrained to `<= 3`.
- **Abilities Support**: Optional policy-driven ability execution (`--abilities policy --ability-config ai-ability-config.json`).
- **Resilient Transport**: Single-use SignalR tickets, automatic reconnect, `GameSyncReconciler` state recovery, and correlation logging via Serilog.

## Usage

### Quick Start (Lobby)
Start 5 parallel bots against a running server:
```cmd
bots-lobby.cmd http://localhost:5000
```

### Manual CLI Invocations

**Host a private room:**
```powershell
dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- `
  --server http://localhost:5000 `
  --name hostbot `
  --mode private `
  --role host `
  --spec "classic:depth=2"
```

**Join a private room as guest:**
```powershell
dotnet run --project Attax.UserSimulator\Attax.UserSimulator.csproj -c Release -- `
  --server http://localhost:5000 `
  --name guestbot `
  --mode private `
  --role guest `
  --spec "classic:depth=2"
```

## Configuration Options
- `--server`: Server base URL (default: `http://localhost:5000`).
- `--name`: Unique bot display identifier (e.g. `bot1`).
- `--mode`: `lobby` or `private`.
- `--lobby-behavior`: Lobby pairing behavior: `wait` (default: waits for challenges and auto-accepts) or `search` (actively searches free players, auto-declines incoming).
- `--role`: Required for `private` mode (`host` or `guest`).
- `--room-code`: Specific 6-character room code to join (skips prompt).
- `--rooms-dir`: Directory for room discovery files (default: `./sim-rooms`).
- `--spec`: AI player specification (e.g. `classic:depth=3,cores=2`).
- `--abilities`: `off` or `policy`.
- `--think-ms`: Simulated human thinking delay range in ms (default: `1000-4000`).
- `--affinity`: Hex bitmask for CPU affinity (e.g. `0x01`).
