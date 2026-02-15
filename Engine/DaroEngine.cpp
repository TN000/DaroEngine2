// Engine/DaroEngine.cpp
#include "DaroEngine.h"
#include "Renderer.h"
#include "FrameBuffer.h"
#include "VideoPlayer.h"  // For VideoLog
#include <memory>
#include <mutex>
#include <atomic>
#include <cstddef>
#include <unordered_map>
#include <vector>
#include <Windows.h>
#include <objbase.h>

static std::unique_ptr<DaroRenderer> g_Renderer;
static std::unique_ptr<DaroFrameBuffer> g_FrameBuffer;
static std::mutex g_Mutex;
static std::atomic<bool> g_Initialized{ false };
static std::atomic<int> g_LastError{ DARO_OK };
static bool g_ComInitializedByUs = false; // Track if we initialized COM

// g_TempMaskList used by Daro_UpdateLayer
static std::vector<int> g_TempMaskList;

static DaroLayer g_Layers[DARO_MAX_LAYERS];
static int g_LayerCount = 0;

static std::atomic<bool> g_IsPlaying{ false };
static std::atomic<int> g_CurrentFrame{ 0 };
static std::atomic<int> g_TotalFrames{ 250 };

static std::atomic<double> g_FPS{ 0.0 };
static std::atomic<double> g_FrameTime{ 0.0 };
static std::atomic<int> g_DroppedFrames{ 0 };
static std::atomic<long long> g_FrameNumber{ 0 };

static double g_TargetFps = 50.0;
static LARGE_INTEGER g_PerfFreq;
static LARGE_INTEGER g_LastFrameTime;

DARO_API int __stdcall Daro_Initialize(int width, int height, double targetFps)
{
    std::lock_guard<std::mutex> lock(g_Mutex);
    
    if (g_Initialized)
    {
        g_LastError = DARO_ERROR_ALREADY_INIT;
        return DARO_ERROR_ALREADY_INIT;
    }
    
    // Initialize COM for Media Foundation and WIC
    // S_OK = freshly initialized by us
    // S_FALSE = already initialized same model (OK)
    // RPC_E_CHANGED_MODE = already initialized as STA by WPF (OK, COM is usable)
    HRESULT hrCom = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hrCom) && hrCom != RPC_E_CHANGED_MODE)
    {
        g_LastError = DARO_ERROR_CREATE_DEVICE;
        return DARO_ERROR_CREATE_DEVICE;
    }
    g_ComInitializedByUs = (hrCom == S_OK);

    g_TargetFps = targetFps;
    QueryPerformanceFrequency(&g_PerfFreq);
    QueryPerformanceCounter(&g_LastFrameTime);

    // Initialize renderer
    g_Renderer = std::make_unique<DaroRenderer>();
    int rendererResult = g_Renderer->Initialize(width, height);
    if (rendererResult != DARO_OK)
    {
        g_LastError = rendererResult;
        g_Renderer.reset();
        return rendererResult;
    }
    
    // Initialize frame buffer
    g_FrameBuffer = std::make_unique<DaroFrameBuffer>();
    if (!g_FrameBuffer->Initialize(width, height))
    {
        g_LastError = DARO_ERROR_CREATE_FRAMEBUFFER;
        g_Renderer.reset();
        g_FrameBuffer.reset();
        return DARO_ERROR_CREATE_FRAMEBUFFER;
    }
    
    memset(g_Layers, 0, sizeof(g_Layers));
    g_LayerCount = 0;
    
    g_Initialized = true;
    g_LastError = DARO_OK;
    return DARO_OK;
}

DARO_API void __stdcall Daro_Shutdown()
{
    std::lock_guard<std::mutex> lock(g_Mutex);
    if (!g_Initialized) return;
    g_FrameBuffer.reset();
    g_Renderer.reset();
    g_Initialized = false;
    if (g_ComInitializedByUs)
    {
        CoUninitialize();
        g_ComInitializedByUs = false;
    }
}

DARO_API bool __stdcall Daro_IsInitialized() { return g_Initialized; }
DARO_API int __stdcall Daro_GetLastError() { return g_LastError; }

DARO_API void __stdcall Daro_BeginFrame()
{
    if (!g_Initialized) return;
    std::lock_guard<std::mutex> lock(g_Mutex);
    if (g_Renderer) g_Renderer->BeginFrame();
}

DARO_API void __stdcall Daro_EndFrame()
{
    if (!g_Initialized) return;

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    double elapsed = (double)(now.QuadPart - g_LastFrameTime.QuadPart) / g_PerfFreq.QuadPart;
    g_FrameTime.store(elapsed * 1000.0);
    g_FPS.store((elapsed > 0.000001) ? 1.0 / elapsed : 0.0);

    // Detect dropped frames: if we took longer than target frame time
    double targetFrameTime = 1.0 / g_TargetFps;
    if (elapsed > targetFrameTime * 1.5)  // 50% tolerance before counting as dropped
    {
        // Count how many frames we missed
        int droppedCount = (int)(elapsed / targetFrameTime) - 1;
        if (droppedCount > 0)
        {
            g_DroppedFrames.fetch_add(droppedCount);
        }
    }

    g_LastFrameTime = now;
    g_FrameNumber++;
}

DARO_API void __stdcall Daro_Render()
{
    if (!g_Initialized) return;

    // Copy layers under lock, then render outside lock to minimize contention
    // But validate g_Renderer under lock to prevent use-after-free on Shutdown
    int localLayerCount;
    static thread_local DaroLayer localLayers[DARO_MAX_LAYERS];

    {
        std::lock_guard<std::mutex> lock(g_Mutex);
        if (!g_Renderer) return;
        localLayerCount = g_LayerCount;
        memcpy(localLayers, g_Layers, sizeof(DaroLayer) * localLayerCount);
    }

    // Build mask lookup from local copy (function-local static to avoid per-frame allocation)
    static thread_local std::unordered_map<int, std::vector<int>> layerToMasks;
    for (auto& pair : layerToMasks)
    {
        pair.second.clear();
    }

    for (int i = 0; i < localLayerCount; i++)
    {
        if (localLayers[i].layerType == DARO_TYPE_MASK && localLayers[i].maskedLayerCount > 0)
        {
            int safeCount = min(localLayers[i].maskedLayerCount, DARO_MAX_LAYERS);
            for (int j = 0; j < safeCount; j++)
            {
                int maskedId = localLayers[i].maskedLayerIds[j];
                if (maskedId >= 0)
                    layerToMasks[maskedId].push_back(i);
            }
        }
    }

    // Render - safe because C# side serializes all engine calls through _engineLock
    // and Stop() waits for render thread before Shutdown() is called
    g_Renderer->Clear(0.0f, 0.0f, 0.0f, 0.0f);
    g_Renderer->RenderWithMasks(localLayers, localLayerCount, layerToMasks);
    g_Renderer->CopyToStaging();

    void* pData = nullptr;
    int rowPitch = 0;
    if (g_Renderer->MapStaging(&pData, &rowPitch))
    {
        if (g_FrameBuffer)
            g_FrameBuffer->Write(pData, rowPitch, g_FrameNumber.load());
        g_Renderer->UnmapStaging();
    }
}

DARO_API void __stdcall Daro_Present()
{
    if (!g_Initialized || !g_Renderer) return;
    g_Renderer->SendSpout();
}

DARO_API bool __stdcall Daro_LockFrameBuffer(void** ppData, int* pWidth, int* pHeight, int* pStride)
{
    if (!g_Initialized || !g_FrameBuffer || !ppData || !pWidth || !pHeight || !pStride) return false;
    return g_FrameBuffer->Lock(ppData, pWidth, pHeight, pStride);
}

DARO_API void __stdcall Daro_UnlockFrameBuffer()
{
    if (g_FrameBuffer) g_FrameBuffer->Unlock();
}

DARO_API long long __stdcall Daro_GetFrameNumber() { return g_FrameNumber; }

DARO_API void __stdcall Daro_SetLayerCount(int count)
{
    std::lock_guard<std::mutex> lock(g_Mutex);
    // Clamp to valid range [0, DARO_MAX_LAYERS] to prevent negative or excessive values
    if (count < 0) count = 0;
    g_LayerCount = min(count, DARO_MAX_LAYERS);
}

DARO_API void __stdcall Daro_UpdateLayer(int index, const DaroLayer* layer)
{
    if (index < 0 || index >= DARO_MAX_LAYERS || !layer) return;
    std::lock_guard<std::mutex> lock(g_Mutex);
    memcpy(&g_Layers[index], layer, sizeof(DaroLayer));
}

DARO_API void __stdcall Daro_GetLayer(int index, DaroLayer* layer)
{
    if (index < 0 || index >= DARO_MAX_LAYERS || !layer) return;
    std::lock_guard<std::mutex> lock(g_Mutex);
    memcpy(layer, &g_Layers[index], sizeof(DaroLayer));
}

DARO_API void __stdcall Daro_ClearLayers()
{
    std::lock_guard<std::mutex> lock(g_Mutex);
    memset(g_Layers, 0, sizeof(g_Layers));
    g_LayerCount = 0;
}

DARO_API void __stdcall Daro_Play() { g_IsPlaying = true; }
DARO_API void __stdcall Daro_Stop() { g_IsPlaying = false; }
DARO_API void __stdcall Daro_SeekToFrame(int frame)
{
    if (!g_Initialized) return;
    int totalFrames = g_TotalFrames.load();
    if (totalFrames <= 0) return;
    g_CurrentFrame = max(0, min(frame, totalFrames - 1));
}
DARO_API void __stdcall Daro_SeekToTime(float time)
{
    if (!g_Initialized) return;
    Daro_SeekToFrame((int)(time * g_TargetFps));
}
DARO_API bool __stdcall Daro_IsPlaying() { return g_IsPlaying; }
DARO_API int __stdcall Daro_GetCurrentFrame() { return g_CurrentFrame; }

DARO_API double __stdcall Daro_GetFPS() { return g_FPS; }
DARO_API double __stdcall Daro_GetFrameTime() { return g_FrameTime; }
DARO_API int __stdcall Daro_GetDroppedFrames() { return g_DroppedFrames; }

// Spout Output - NOW IMPLEMENTED!
DARO_API bool __stdcall Daro_EnableSpoutOutput(const char* senderName)
{
    if (!g_Initialized || !g_Renderer) return false;
    return g_Renderer->EnableSpout(senderName);
}

DARO_API void __stdcall Daro_DisableSpoutOutput()
{
    if (g_Initialized && g_Renderer)
        g_Renderer->DisableSpout();
}

DARO_API bool __stdcall Daro_IsSpoutEnabled()
{
    if (!g_Initialized || !g_Renderer) return false;
    return g_Renderer->IsSpoutEnabled();
}

// Texture management
DARO_API int __stdcall Daro_LoadTexture(const char* filePath)
{
    if (!g_Initialized || !g_Renderer) return -1;
    return g_Renderer->LoadTexture(filePath);
}

DARO_API void __stdcall Daro_UnloadTexture(int textureId)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->UnloadTexture(textureId);
}

// Spout Input
DARO_API int __stdcall Daro_GetSpoutSenderCount()
{
    if (!g_Initialized || !g_Renderer) return 0;
    return g_Renderer->GetSpoutSenderCount();
}

DARO_API bool __stdcall Daro_GetSpoutSenderName(int index, char* buffer, int bufferSize)
{
    if (!g_Initialized || !g_Renderer) return false;
    return g_Renderer->GetSpoutSenderName(index, buffer, bufferSize);
}

DARO_API int __stdcall Daro_ConnectSpoutReceiver(const char* senderName)
{
    if (!g_Initialized || !g_Renderer) return -1;
    return g_Renderer->ConnectSpoutReceiver(senderName);
}

DARO_API void __stdcall Daro_DisconnectSpoutReceiver(int receiverId)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->DisconnectSpoutReceiver(receiverId);
}

// Debug - structure info
DARO_API int __stdcall Daro_GetStructSize()
{
    return (int)sizeof(DaroLayer);
}

DARO_API int __stdcall Daro_GetOffsetPosX()
{
    return (int)offsetof(DaroLayer, posX);
}

DARO_API int __stdcall Daro_GetOffsetSizeX()
{
    return (int)offsetof(DaroLayer, sizeX);
}

DARO_API int __stdcall Daro_GetOffsetOpacity()
{
    return (int)offsetof(DaroLayer, opacity);
}

DARO_API int __stdcall Daro_GetOffsetTextContent()
{
    return (int)offsetof(DaroLayer, textContent);
}

// Debug - bounding boxes
DARO_API void __stdcall Daro_SetShowBounds(bool show)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->SetShowBounds(show);
}

// Device status
DARO_API bool __stdcall Daro_IsDeviceLost()
{
    if (!g_Initialized || !g_Renderer) return false;
    return g_Renderer->IsDeviceLost();
}

// Edge antialiasing
DARO_API void __stdcall Daro_SetEdgeSmoothing(float width)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->SetEdgeSmoothing(width);
}

DARO_API float __stdcall Daro_GetEdgeSmoothing()
{
    if (!g_Initialized || !g_Renderer) return 0.0f;
    return g_Renderer->GetEdgeSmoothing();
}

// Video playback
DARO_API int __stdcall Daro_LoadVideo(const char* filePath)
{
    if (!g_Initialized || !g_Renderer)
    {
        VideoLog("[DaroVideo] Daro_LoadVideo: engine not initialized\n");
        return -1;
    }
    int id = g_Renderer->LoadVideo(filePath);
    char dbg[256];
    sprintf_s(dbg, "[DaroVideo] Daro_LoadVideo: returned id=%d\n", id);
    VideoLog(dbg);
    return id;
}

DARO_API void __stdcall Daro_UnloadVideo(int videoId)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->UnloadVideo(videoId);
}

DARO_API void __stdcall Daro_PlayVideo(int videoId)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->PlayVideo(videoId);
}

DARO_API void __stdcall Daro_PauseVideo(int videoId)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->PauseVideo(videoId);
}

DARO_API void __stdcall Daro_StopVideo(int videoId)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->StopVideo(videoId);
}

DARO_API void __stdcall Daro_SeekVideo(int videoId, int frame)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->SeekVideo(videoId, frame);
}

DARO_API void __stdcall Daro_SeekVideoTime(int videoId, double seconds)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->SeekVideoTime(videoId, seconds);
}

DARO_API bool __stdcall Daro_IsVideoPlaying(int videoId)
{
    if (!g_Initialized || !g_Renderer) return false;
    return g_Renderer->IsVideoPlaying(videoId);
}

DARO_API int __stdcall Daro_GetVideoFrame(int videoId)
{
    if (!g_Initialized || !g_Renderer) return 0;
    return g_Renderer->GetVideoFrame(videoId);
}

DARO_API int __stdcall Daro_GetVideoTotalFrames(int videoId)
{
    if (!g_Initialized || !g_Renderer) return 0;
    return g_Renderer->GetVideoTotalFrames(videoId);
}

DARO_API void __stdcall Daro_SetVideoLoop(int videoId, bool loop)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->SetVideoLoop(videoId, loop);
}

DARO_API void __stdcall Daro_SetVideoAlpha(int videoId, bool alpha)
{
    if (g_Initialized && g_Renderer)
        g_Renderer->SetVideoAlpha(videoId, alpha);
}
