# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DaroEngine2 is a real-time animation and graphics rendering engine with a designer UI, consisting of:
- **DaroEngine**: Native C++ DirectX 11 rendering engine (compiled as DLL)
- **Designer**: C# WPF desktop application for creating/managing animations

Target: Windows x64, 1920x1080 @ 50 FPS

## Build Commands

```bash
# Build entire solution (Visual Studio 2022 / MSBuild required)
msbuild DaroEngine2.slnx /p:Configuration=Debug /p:Platform=x64
msbuild DaroEngine2.slnx /p:Configuration=Release /p:Platform=x64

# Build individual projects
msbuild Engine/DaroEngine.vcxproj /p:Configuration=Debug /p:Platform=x64
msbuild Designer/Designer.csproj /p:Configuration=Debug
```

**Output locations:**
- Engine: `bin/Debug/DaroEngine.dll` or `bin/Release/DaroEngine.dll`
- Designer: `Designer/bin/Debug/DaroDesigner.exe`

Note: Designer post-build automatically copies `DaroEngine.dll` to its output folder.

## Architecture

```
Designer (C#/WPF)
    ↓ P/Invoke interop
DaroEngine.dll (C++/DirectX 11)
    ├─ Renderer (Direct3D 11, Direct2D, DirectWrite)
    ├─ FrameBuffer (shared memory for Designer preview)
    └─ Spout (video stream send/receive)
```

### Engine C API (exported from DaroEngine.dll)

All exports use `Daro_*` prefix with `__stdcall` convention:
- **Lifecycle**: `Daro_Initialize()`, `Daro_Shutdown()`, `Daro_IsInitialized()`
- **Rendering**: `Daro_BeginFrame()`, `Daro_EndFrame()`, `Daro_Present()`
- **Layers**: `Daro_SetLayerCount()`, `Daro_UpdateLayer()`, `Daro_ClearLayers()`
- **Playback**: `Daro_Play()`, `Daro_Stop()`, `Daro_SeekToFrame()`
- **Textures**: `Daro_LoadTexture()`, `Daro_UnloadTexture()`
- **Spout**: `Daro_EnableSpoutOutput()`, `Daro_ConnectSpoutReceiver()`
- **Frame Buffer**: `Daro_LockFrameBuffer()`, `Daro_UnlockFrameBuffer()`

### Layer System

`DaroLayer` struct (2832 bytes, packed) defined in `Engine/SharedTypes.h`:
- Types: Rectangle(0), Circle(1), Text(2), Image(3), Video(4), Mask(5), Group(6)
- Texture sources: SolidColor(0), SpoutInput(1), ImageFile(2), VideoFile(3)
- Transform: position, size, rotation (3D), anchor point
- Appearance: color (RGBA), opacity
- Text: content (1024 wchars max), font, size, alignment, line height, letter spacing
- Mask: maskMode (Inner/Outer), maskedLayerIds array

### Interop (Designer ↔ Engine)

P/Invoke declarations in `Designer/Engine/EngineInterop.cs`:
- Uses `CharSet.Unicode` and `Pack = 1` struct layout
- `DaroLayerNative` mirrors C++ `DaroLayer` exactly

### Constants (SharedTypes.h)

- `DARO_MAX_LAYERS`: 64
- `DARO_FRAME_WIDTH`: 1920
- `DARO_FRAME_HEIGHT`: 1080
- `DARO_TARGET_FPS`: 50.0

## Key Files

**Engine:**
- `Engine/DaroEngine.h` - Public API declarations and error codes
- `Engine/SharedTypes.h` - DaroLayer struct and type constants
- `Engine/Renderer.cpp` - DirectX 11 rendering implementation
- `Engine/FrameBuffer.cpp` - Shared memory frame buffer

**Designer:**
- `Designer/MainWindow.xaml.cs` - Main UI, timeline, preview
- `Designer/Engine/EngineInterop.cs` - P/Invoke declarations
- `Designer/Engine/EngineRenderer.cs` - Engine wrapper
- `Designer/Models/` - ProjectModel, AnimationModel, LayerModel, KeyframeModel

## Naming Conventions

**C++ Engine:**
- Exported functions: `Daro_*` prefix
- Classes: PascalCase
- Members: `m_` prefix
- Globals: `g_` prefix

**C# Designer:**
- Private fields: `_` prefix
- Root namespace: `DaroDesigner`

## Error Handling

Engine returns error codes from `DaroEngine.h`:
- `DARO_OK` (0) - Success
- `DARO_ERROR_ALREADY_INIT` (1) - Already initialized
- `DARO_ERROR_CREATE_DEVICE` (2) - D3D device creation failed
- Use `Daro_GetLastError()` to retrieve last error

## Serialization

Project files use JSON via `System.Text.Json`. Models in `Designer/Models/` define the serialization structure.
