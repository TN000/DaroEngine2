// Engine/FrameBuffer.h
#pragma once

#include <Windows.h>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include <string>

#define DARO_FRAME_MEM_PREFIX L"DaroFrameBuffer_"

#pragma pack(push, 1)
struct DaroFrameHeader
{
    int width;
    int height;
    int stride;
    long long frameNumber;
    int locked;
};
#pragma pack(pop)

class DaroFrameBuffer
{
public:
    DaroFrameBuffer();
    ~DaroFrameBuffer();

    bool Initialize(int width, int height);
    void Shutdown();

    void Write(const void* pData, int srcStride, long long frameNumber);
    bool Lock(void** ppData, int* pWidth, int* pHeight, int* pStride);
    void Unlock();

    int GetWidth() const { return m_Width; }
    int GetHeight() const { return m_Height; }

private:
    int m_Width = 0;
    int m_Height = 0;
    int m_Stride = 0;
    size_t m_BufferSize = 0;

    HANDLE m_hMapFile = nullptr;
    void* m_pMapView = nullptr;
    DaroFrameHeader* m_pHeader = nullptr;
    BYTE* m_pPixels = nullptr;

    std::mutex m_Mutex;
    std::condition_variable m_UnlockCondition;
    std::atomic<bool> m_IsLocked{ false };
};