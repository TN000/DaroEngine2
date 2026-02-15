# DaroEngine2

**Real-time broadcast graphics engine for live production**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)]()
[![DirectX 11](https://img.shields.io/badge/DirectX-11-green.svg)]()

---

## What is DaroEngine2?

DaroEngine2 is an open-source real-time graphics engine built for broadcast and live production workflows. It combines a native C++ DirectX 11 rendering engine with a C# WPF designer application, allowing you to create animated graphics, build reusable templates, manage playlists, and send the final output to OBS (or any Spout-compatible application) at 1920x1080 @ 50 FPS.

Think of it as an open-source alternative to commercial broadcast graphics systems — design your lower thirds, titles, and overlays in the visual editor, then play them out live.

> **Built in 7 days** using vibecoding with [Claude Code](https://claude.ai/code) — from zero to a working broadcast graphics engine.

<!-- screenshots here -->

---

## Key Features

- **Visual Designer** — WYSIWYG editor with timeline, keyframe animation, and real-time preview
- **Layer System** — Rectangles, circles, text, images, video, masks, and groups (up to 64 layers)
- **Template System** — Create reusable templates with fill forms and named property bindings (transfunctioners)
- **Playout Engine** — Playlist management with cue/take/clear workflow, auto-advance, and looping
- **Spout Output** — Send rendered frames to OBS, vMix, CasparCG, or any Spout receiver
- **Spout Input** — Receive live video streams from other Spout senders and use them as layer textures (e.g., live camera feeds, other applications)
- **Video Support** — MP4, MOV, ProRes 4444, Apple Animation with alpha channel support
- **Mosart Automation** — TCP command interface for newsroom automation integration
- **REST API Middleware** — ASP.NET Core service for external system integration (Octopus NRCS, etc.)
- **Edge Antialiasing** — Shader-based smoothing with configurable quality levels
- **3D Transforms** — Full rotation on all axes with anchor point control

---

## Quick Start

### Prerequisites

- **Windows 10/11** (x64)
- **Visual Studio 2022** or later with:
  - C++ Desktop Development workload
  - .NET Desktop Development workload
- **.NET 10 SDK**
- **DirectX 11 compatible GPU**

### Build

```bash
# Clone the repository
git clone https://github.com/TN000/DaroEngine2.git
cd DaroEngine2

# Build the full solution
msbuild DaroEngine2.slnx /p:Configuration=Debug /p:Platform=x64
```

**Output:**
- Engine DLL: `bin/Debug/DaroEngine.dll`
- Designer App: `Designer/bin/Debug/net10.0-windows/DaroDesigner.exe`

### First Launch

1. Run `DaroDesigner.exe`
2. File > New Scene to create a new project
3. Add layers using the layer panel
4. Animate with the timeline at the bottom
5. Switch to **PLAYOUT** tab for live playback

---

## How It Works

DaroEngine2 separates **design** from **playout** — a designer creates animated scenes and templates once, and an operator can then use them live with just a few clicks. No technical knowledge needed during the broadcast.

```
  PREPARATION (before show)              LIVE (during broadcast)

  ┌──────────┐   ┌──────────────┐       ┌──────────────────────┐
  │  Design   │──>│  Create      │       │      PLAYOUT         │
  │  Scenes   │   │  Templates   │       │                      │
  │  (.daro)  │   │  (.dtemplate)│       │  ┌────────────────┐  │
  └──────────┘   └──────────────┘       │  │ Playlist:      │  │
                                         │  │  1. Lower Third│  │
                                         │  │  2. Full Screen│  │
                                         │  │  3. Score      │  │
                                         │  │  4. Outro      │  │
                                         │  └────────┬───────┘  │
                                         │           │          │
                                         │    [Cue Next]       │
                                         │    [>> TAKE <<]     │
                                         │    [Clear]          │
                                         └──────────┬───────────┘
                                                    │ Spout
                                         ┌──────────▼───────────┐
                                         │   OBS / vMix / etc.  │
                                         └──────────────────────┘
```

### Preparation Workflow (before the show)

1. **Design a Scene** — Open the visual editor, add layers (text, images, shapes, video), animate them on the timeline with keyframes
2. **Create a Template** — Build a fill form in the Template Maker that maps input fields to scene properties (e.g., a "Name" field that controls the text layer)
3. **Save** — Scene as `.daro`, template as `.dtemplate`. These are your reusable building blocks

**File locations:** The playout template browser looks for files in:

```
Documents\DaroEngine\Templates\
├── Lower Thirds\
│   ├── breaking-news.dtemplate
│   └── breaking-news.daro
├── Fullscreen\
│   ├── opener.dtemplate
│   └── opener.daro
└── ...
```

Save your `.dtemplate` and `.daro` files into `Documents\DaroEngine\Templates\`. You can organize them into subfolders — the playout browser will show the folder structure as a tree. Each template links to its scene file via the `LinkedScenePath` property set in the Template Maker.

### Live Playout Workflow (during the broadcast)

Once your templates are ready, going live is simple — the operator just works with a playlist:

1. **Enable Playout** — Click **Enable Daro Playout**. The designer preview will shut down to free the rendering engine for live output
2. **Start the Engine** — Click **Run Daro Engine** to initialize the rendering engine for playout
3. **Browse Templates** — In the PLAYOUT tab, pick a template from the library
2. **Fill In** — Type the text (headline, name, score...) into the form fields
3. **Add to Playlist** — Click **+ Add to Playlist**. Repeat for all graphics you'll need during the show
4. **Cue Next** — Double-click an item to load it into the engine, ready to go
5. **Take** — One click to play the animation on air. The graphic renders and streams via Spout to OBS in real time
6. **Clear** — Remove the graphic from output when done
7. **Repeat** — Cue the next item and take again. With auto-advance enabled, the next item cues automatically

The entire live workflow is **Cue → Take → Clear**, making it fast enough for live news, sports, or event production. The playlist can be prepared in advance or built on the fly during the show.

---

## Getting Output into OBS

1. Install the [Spout plugin for OBS](https://github.com/Off-World-Live/obs-spout2-plugin)
2. In DaroDesigner, go to the **PLAYOUT** tab
3. Check **Enable Spout** and set a sender name (default: `DaroPlayout`)
4. In OBS, add a **Spout2 Capture** source
5. Select the sender name from the dropdown
6. Your graphics are now live in OBS with full alpha transparency

---

## Architecture

| Component | Technology | Description |
|-----------|-----------|-------------|
| **DaroEngine** | C++ / DirectX 11 | Native rendering engine compiled as a DLL. Handles all GPU rendering, text layout (DirectWrite), video decoding (Media Foundation + FFmpeg), and Spout output. |
| **DaroDesigner** | C# / WPF | Visual designer application. Scene editor, timeline, template maker, and playout engine. Communicates with the engine via P/Invoke. |
| **GraphicsMiddleware** | ASP.NET Core | REST API service with SQLite database. Bridges external systems (newsroom, automation) with the playout engine. |

### Engine API

The engine exports a C API with `Daro_*` prefix and `__stdcall` calling convention. Key functions:

- **Lifecycle:** `Daro_Initialize()`, `Daro_Shutdown()`
- **Rendering:** `Daro_BeginFrame()`, `Daro_EndFrame()`, `Daro_Present()`
- **Layers:** `Daro_SetLayerCount()`, `Daro_UpdateLayer()`, `Daro_ClearLayers()`
- **Playback:** `Daro_Play()`, `Daro_Stop()`, `Daro_SeekToFrame()`
- **Textures:** `Daro_LoadTexture()`, `Daro_UnloadTexture()`
- **Spout:** `Daro_EnableSpoutOutput()`, `Daro_ConnectSpoutReceiver()`
- **Frame Buffer:** `Daro_LockFrameBuffer()`, `Daro_UnlockFrameBuffer()`

See `Engine/DaroEngine.h` for the full API reference.

---

## Graphics Middleware

The GraphicsMiddleware is an ASP.NET Core service that acts as a bridge between external systems (newsroom, automation) and DaroDesigner. It provides a REST API and stores playlist items in a SQLite database.

### Running the Middleware

```bash
cd GraphicsMiddleware
dotnet run
```

This starts the service on `http://localhost:5000` with:
- **REST API** for creating and managing playlist items (`/api/items`)
- **Control endpoints** for remote playout commands (`/api/control/cue`, `/play`, `/stop`, etc.)
- **Spout control** for enabling/disabling output remotely
- **Web dashboard** at `http://localhost:5000` for quick testing

### REST API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/items` | Create a playlist item (template path, scene path, filled data) |
| GET | `/api/items/{id}` | Get item by ID |
| GET | `/api/items` | List all items |
| DELETE | `/api/items/{id}` | Delete item |
| POST | `/api/control/cue` | CUE — load item into playout |
| POST | `/api/control/play` | PLAY — take on air |
| POST | `/api/control/stop` | STOP — clear output |

### Example: Create and Play a Graphic

```bash
# 1. Create a playlist item
curl -X POST http://localhost:5000/api/items \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Breaking News",
    "templateFilePath": "path/to/template.dtemplate",
    "linkedScenePath": "path/to/scene.daro",
    "filledData": { "title-tf": "Breaking News", "subtitle-tf": "Live report" }
  }'

# 2. CUE the item (returns item ID from step 1)
curl -X POST http://localhost:5000/api/control/cue -d '"ITEM-GUID"'

# 3. PLAY — take on air
curl -X POST http://localhost:5000/api/control/play
```

---

## Mosart / Automation Integration

DaroDesigner includes a TCP server (port 5555) for newsroom automation integration:

```
Octopus NRCS ──> GraphicsMiddleware (REST :5000) ──> Mosart ──> DaroDesigner (TCP :5555)
```

Commands: `CUE`, `PLAY`, `STOP`, `PAUSE`, `CONTINUE` — see [CONTRIBUTING.md](CONTRIBUTING.md) for protocol details.

---

## Roadmap

Community contributions welcome in any of these areas:

- [ ] [EBU OGraf](https://tech.ebu.ch/news/2025/04/ograf-the-ebu's-open-spec-for-cross-platform-graphics-integration) support — render HTML-based `.ograf.json` graphics packages and/or export DaroEngine templates as OGraf web components
- [ ] Multi-resolution support (4K, custom resolutions)
- [ ] NDI output (alternative to Spout)
- [ ] GPU-accelerated text effects (drop shadows, outlines, gradients)
- [ ] Plugin system for custom layer types
- [ ] Web-based remote control panel
- [ ] Linux / cross-platform rendering backend (Vulkan)
- [ ] Audio support
- [ ] Ticker / crawl layer type
- [ ] Clock / countdown layer type
- [ ] Data binding (RSS feeds, APIs, spreadsheets)
- [ ] Multi-language UI

Have an idea? [Open an issue](../../issues) to discuss it.

---

## Contributing

We welcome contributions! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on setting up your development environment, code style, and how to submit pull requests.

---

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.
