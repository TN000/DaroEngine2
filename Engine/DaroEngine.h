// Engine/DaroEngine.h
#pragma once

#ifdef DAROENGINE_EXPORTS
#define DARO_API __declspec(dllexport)
#else
#define DARO_API __declspec(dllimport)
#endif

#include "SharedTypes.h"

extern "C"
{
    // Lifecycle
    DARO_API int __stdcall Daro_Initialize(int width, int height, double targetFps);
    DARO_API void __stdcall Daro_Shutdown();
    DARO_API bool __stdcall Daro_IsInitialized();
    DARO_API int __stdcall Daro_GetLastError();
    
    // Rendering
    DARO_API void __stdcall Daro_BeginFrame();
    DARO_API void __stdcall Daro_EndFrame();
    DARO_API void __stdcall Daro_Render();
    DARO_API void __stdcall Daro_Present();
    
    // Frame buffer access
    DARO_API bool __stdcall Daro_LockFrameBuffer(void** ppData, int* pWidth, int* pHeight, int* pStride);
    DARO_API void __stdcall Daro_UnlockFrameBuffer();
    DARO_API long long __stdcall Daro_GetFrameNumber();
    
    // Layer management
    DARO_API void __stdcall Daro_SetLayerCount(int count);
    DARO_API void __stdcall Daro_UpdateLayer(int index, const DaroLayer* layer);
    DARO_API void __stdcall Daro_GetLayer(int index, DaroLayer* layer);
    DARO_API void __stdcall Daro_ClearLayers();
    
    // Playback control
    DARO_API void __stdcall Daro_Play();
    DARO_API void __stdcall Daro_Stop();
    DARO_API void __stdcall Daro_SeekToFrame(int frame);
    DARO_API void __stdcall Daro_SeekToTime(float time);
    DARO_API bool __stdcall Daro_IsPlaying();
    DARO_API int __stdcall Daro_GetCurrentFrame();
    
    // Stats
    DARO_API double __stdcall Daro_GetFPS();
    DARO_API double __stdcall Daro_GetFrameTime();
    DARO_API int __stdcall Daro_GetDroppedFrames();
    
    // Spout Output
    DARO_API bool __stdcall Daro_EnableSpoutOutput(const char* senderName);
    DARO_API void __stdcall Daro_DisableSpoutOutput();
    DARO_API bool __stdcall Daro_IsSpoutEnabled();
    
    // Texture management
    DARO_API int __stdcall Daro_LoadTexture(const char* filePath);
    DARO_API void __stdcall Daro_UnloadTexture(int textureId);
    
    // Spout Input
    DARO_API int __stdcall Daro_GetSpoutSenderCount();
    DARO_API bool __stdcall Daro_GetSpoutSenderName(int index, char* buffer, int bufferSize);
    DARO_API int __stdcall Daro_ConnectSpoutReceiver(const char* senderName);
    DARO_API void __stdcall Daro_DisconnectSpoutReceiver(int receiverId);
    
    // Debug - structure info
    DARO_API int __stdcall Daro_GetStructSize();
    DARO_API int __stdcall Daro_GetOffsetPosX();
    DARO_API int __stdcall Daro_GetOffsetSizeX();
    DARO_API int __stdcall Daro_GetOffsetOpacity();
    DARO_API int __stdcall Daro_GetOffsetTextContent();

    // Debug - bounding boxes
    DARO_API void __stdcall Daro_SetShowBounds(bool show);

    // Device status
    DARO_API bool __stdcall Daro_IsDeviceLost();

    // Edge antialiasing (shader-based smooth edges)
    DARO_API void __stdcall Daro_SetEdgeSmoothing(float width);
    DARO_API float __stdcall Daro_GetEdgeSmoothing();

    // Video playback
    DARO_API int __stdcall Daro_LoadVideo(const char* filePath);
    DARO_API void __stdcall Daro_UnloadVideo(int videoId);
    DARO_API void __stdcall Daro_PlayVideo(int videoId);
    DARO_API void __stdcall Daro_PauseVideo(int videoId);
    DARO_API void __stdcall Daro_StopVideo(int videoId);
    DARO_API void __stdcall Daro_SeekVideo(int videoId, int frame);
    DARO_API void __stdcall Daro_SeekVideoTime(int videoId, double seconds);
    DARO_API bool __stdcall Daro_IsVideoPlaying(int videoId);
    DARO_API int __stdcall Daro_GetVideoFrame(int videoId);
    DARO_API int __stdcall Daro_GetVideoTotalFrames(int videoId);
    DARO_API void __stdcall Daro_SetVideoLoop(int videoId, bool loop);
    DARO_API void __stdcall Daro_SetVideoAlpha(int videoId, bool alpha);
}

// Error codes
#define DARO_OK 0
#define DARO_ERROR_ALREADY_INIT 1
#define DARO_ERROR_CREATE_DEVICE 2
#define DARO_ERROR_CREATE_RT 3
#define DARO_ERROR_CREATE_SHADERS 4
#define DARO_ERROR_CREATE_GEOMETRY 5
#define DARO_ERROR_CREATE_STAGING 6
#define DARO_ERROR_CREATE_FRAMEBUFFER 7
