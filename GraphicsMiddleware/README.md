# Graphics Middleware

High-performance middleware for integrating Octopus Newsroom (NRCS) with Mosart Automation and DaroEngine graphics renderer.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           GRAPHICS MIDDLEWARE                                │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   ┌──────────────┐       ┌──────────────┐       ┌──────────────────────┐    │
│   │  REST API    │       │  TCP Server  │       │  DaroEngineController│    │
│   │  :5000       │       │  :5555       │       │  (P/Invoke)          │    │
│   └──────┬───────┘       └──────┬───────┘       └──────────┬───────────┘    │
│          │                      │                          │                 │
│          │  ┌───────────────────┴───────────────┐          │                 │
│          │  │        PlaylistItemRepository     │          │                 │
│          │  │        (SQLite + Dapper)          │          │                 │
│          │  └───────────────────────────────────┘          │                 │
│          │                                                 │                 │
└──────────┼─────────────────────────────────────────────────┼─────────────────┘
           │                                                 │
           ▼                                                 ▼
┌──────────────────┐                              ┌──────────────────────┐
│  Octopus NRCS    │                              │   DaroEngine.dll     │
│  (HTML5 Plugin)  │                              │   (DirectX 11)       │
└──────────────────┘                              └──────────┬───────────┘
                                                             │
┌──────────────────┐                              ┌──────────▼───────────┐
│  Mosart          │                              │   Spout Output       │
│  Automation      │──────TCP Protocol───────────▶│   (1920x1080@50fps)  │
└──────────────────┘                              └──────────────────────┘
```

## Quick Start

```bash
# Build
cd GraphicsMiddleware
dotnet build

# Run (requires DaroEngine.dll in bin folder)
dotnet run
```

Server endpoints:
- **REST API:** http://localhost:5000
- **Mosart TCP:** port 5555
- **Web UI:** http://localhost:5000/index.html

## DaroEngine Integration

This middleware directly integrates with the DaroEngine C++ renderer via P/Invoke:

1. **Project Loading** - Reads `.daro` project files (JSON format)
2. **Transfunctioner Binding** - Applies template values to layer properties
3. **Keyframe Animation** - Interpolates animation tracks at 50 FPS
4. **Native Rendering** - Calls DaroEngine.dll for DirectX 11 rendering
5. **Spout Output** - Sends video feed to downstream systems

### Required Files

Copy to the `bin/Debug/net9.0/` folder:
- `DaroEngine.dll` - Main rendering engine
- Any dependencies (DirectX runtime, etc.)

## REST API

### Playlist Items

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/items` | Create playlist item from .daro file |
| GET | `/api/items/{id}` | Get item by ID |
| GET | `/api/items` | List all items |
| DELETE | `/api/items/{id}` | Delete item |

### Engine Control

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/control/cue` | Direct CUE from file path |
| POST | `/api/control/play` | Start playback |
| POST | `/api/control/stop` | Stop and clear |
| POST | `/api/control/pause` | Pause playback |
| POST | `/api/control/continue` | Resume playback |
| GET | `/api/control/status` | Get engine state |
| POST | `/api/control/spout/enable` | Enable Spout output |
| POST | `/api/control/spout/disable` | Disable Spout output |

### Create Item Example

```bash
curl -X POST http://localhost:5000/api/items \
  -H "Content-Type: application/json" \
  -d '{
    "linkedScenePath": "D:\\Projects\\LowerThird.daro",
    "name": "Breaking News",
    "filledData": {
      "element_title": "BREAKING NEWS",
      "element_subtitle": "More details coming..."
    }
  }'
```

## Mosart TCP Protocol

Connect to port 5555 and send commands: `GUID|COMMAND[|TAKE_NAME]\r\n`

| Command | Value | Description |
|---------|-------|-------------|
| CUE | 0 | Load item from database |
| PLAY | 1 | Start playback |
| STOP | 2 | Stop and clear |
| CONTINUE | 3 | Resume from pause |
| PAUSE | 4 | Pause playback |

### Example Session

```
>> 550e8400-e29b-41d4-a716-446655440000|0
<< OK:CUED

>> 550e8400-e29b-41d4-a716-446655440000|1
<< OK:PLAYING

>> 550e8400-e29b-41d4-a716-446655440000|2
<< OK:STOPPED
```

## Transfunctioner System

Templates can bind form fields to layer properties via transfunctioners:

```json
{
  "filledData": {
    "element_title": "Breaking News",
    "element_name": "John Smith"
  }
}
```

The middleware maps these values to layer properties defined in the .daro project file:
- `TextContent` - Text layer content
- `PosX/PosY` - Position
- `SizeX/SizeY` - Size
- `Opacity` - Transparency
- `ColorR/ColorG/ColorB` - Color components
- `FontSize` - Text size

## Testing

```powershell
# Test REST API
.\Tools\test-api.ps1 -ScenePath "D:\Projects\MyScene.daro"

# Test Mosart TCP
.\Tools\test-mosart.ps1
```

## Configuration

`appsettings.json`:

```json
{
  "Database": {
    "Path": "graphics_middleware.db"
  },
  "Mosart": {
    "Port": 5555
  },
  "Engine": {
    "SpoutSenderName": "GraphicsMiddleware"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      }
    }
  }
}
```

## Project Structure

```
GraphicsMiddleware/
├── Program.cs                      # Entry point, API endpoints
├── Engine/
│   └── DaroEngineInterop.cs        # P/Invoke declarations
├── Models/
│   └── Template.cs                 # Data models (compatible with Designer)
├── Repositories/
│   ├── DatabaseConnectionFactory.cs
│   └── TemplateRepository.cs       # Playlist item persistence
├── Services/
│   ├── IEngineController.cs        # Engine interface
│   ├── DaroEngineController.cs     # Real P/Invoke implementation
│   └── MosartTcpServer.cs          # TCP server for automation
├── wwwroot/
│   └── index.html                  # Web control panel
└── Tools/
    ├── test-api.ps1
    └── test-mosart.ps1
```

## Data Flow

1. **Octopus Plugin** creates playlist item via REST API
2. Item stored in SQLite with scene path + filled data
3. **Mosart** sends CUE command with item GUID via TCP
4. Middleware loads .daro project, applies transfunctioners
5. **DaroEngineController** converts to native layers, starts render loop
6. **Spout** sends video output at 50 FPS

## Requirements

- .NET 9.0 SDK
- Windows x64 (for DaroEngine.dll)
- DaroEngine.dll compiled and available
