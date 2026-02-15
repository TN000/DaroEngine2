// Engine/FrameBuffer.cpp
#include "FrameBuffer.h"
#include <chrono>
#include <sddl.h>

DaroFrameBuffer::DaroFrameBuffer() {}

DaroFrameBuffer::~DaroFrameBuffer()
{
    Shutdown();
}

bool DaroFrameBuffer::Initialize(int width, int height)
{
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        return false;

    m_Width = width;
    m_Height = height;
    m_Stride = width * 4; // BGRA
    m_BufferSize = sizeof(DaroFrameHeader) + (size_t)m_Stride * height;

    // Build per-process shared memory name to avoid collision between instances
    std::wstring memName = DARO_FRAME_MEM_PREFIX + std::to_wstring(GetCurrentProcessId());

    // Create security descriptor that restricts access to the current user
    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = FALSE;
    // SDDL: Owner=current user, DACL grants full access only to the creator owner
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:(A;;GA;;;CO)", SDDL_REVISION_1, &sa.lpSecurityDescriptor, nullptr))
    {
        sa.lpSecurityDescriptor = nullptr; // Fall back to default if SDDL fails
    }

    // Create or open shared memory (split size into high/low DWORD for >4GB safety)
    DWORD sizeHigh = (DWORD)(m_BufferSize >> 32);
    DWORD sizeLow = (DWORD)(m_BufferSize & 0xFFFFFFFF);
    m_hMapFile = CreateFileMappingW(
        INVALID_HANDLE_VALUE,
        sa.lpSecurityDescriptor ? &sa : nullptr,
        PAGE_READWRITE,
        sizeHigh,
        sizeLow,
        memName.c_str()
    );

    if (sa.lpSecurityDescriptor)
        LocalFree(sa.lpSecurityDescriptor);

    if (!m_hMapFile)
        return false;

    m_pMapView = MapViewOfFile(
        m_hMapFile,
        FILE_MAP_ALL_ACCESS,
        0, 0,
        m_BufferSize
    );

    if (!m_pMapView)
    {
        CloseHandle(m_hMapFile);
        m_hMapFile = nullptr;
        return false;
    }

    m_pHeader = (DaroFrameHeader*)m_pMapView;
    m_pPixels = (BYTE*)m_pMapView + sizeof(DaroFrameHeader);

    // Initialize header
    m_pHeader->width = width;
    m_pHeader->height = height;
    m_pHeader->stride = m_Stride;
    m_pHeader->frameNumber = 0;
    m_pHeader->locked = 0;
    m_IsLocked = false;

    return true;
}

void DaroFrameBuffer::Shutdown()
{
    // Signal any waiting threads before shutdown
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        m_IsLocked = false;
    }
    m_UnlockCondition.notify_all();

    if (m_pMapView)
    {
        UnmapViewOfFile(m_pMapView);
        m_pMapView = nullptr;
    }

    if (m_hMapFile)
    {
        CloseHandle(m_hMapFile);
        m_hMapFile = nullptr;
    }

    m_pHeader = nullptr;
    m_pPixels = nullptr;
}

void DaroFrameBuffer::Write(const void* pData, int srcStride, long long frameNumber)
{
    if (!m_pPixels || !pData) return;

    std::unique_lock<std::mutex> lock(m_Mutex);

    // Wait for unlock with timeout (max 10ms to avoid blocking render)
    if (m_IsLocked)
    {
        auto timeout = std::chrono::milliseconds(10);
        m_UnlockCondition.wait_for(lock, timeout, [this]() { return !m_IsLocked.load(); });

        // If still locked after timeout, skip this frame write
        if (m_IsLocked)
            return;
    }

    // Copy data - optimized: if strides match, use single memcpy
    const BYTE* src = (const BYTE*)pData;
    BYTE* dst = m_pPixels;

    if (srcStride == m_Stride)
    {
        // Strides match - single copy for entire buffer
        memcpy(dst, src, (size_t)m_Stride * m_Height);
    }
    else
    {
        // Strides differ - row by row copy (use smaller stride to prevent overread)
        int copyStride = (srcStride < m_Stride) ? srcStride : m_Stride;
        for (int y = 0; y < m_Height; y++)
        {
            memcpy(dst, src, copyStride);
            src += srcStride;
            dst += m_Stride;
        }
    }

    m_pHeader->frameNumber = frameNumber;
}

bool DaroFrameBuffer::Lock(void** ppData, int* pWidth, int* pHeight, int* pStride)
{
    if (!m_pHeader || !m_pPixels) return false;

    std::lock_guard<std::mutex> lock(m_Mutex);
    m_IsLocked = true;
    m_pHeader->locked = 1;

    *ppData = m_pPixels;
    *pWidth = m_pHeader->width;
    *pHeight = m_pHeader->height;
    *pStride = m_pHeader->stride;

    return true;
}

void DaroFrameBuffer::Unlock()
{
    {
        std::lock_guard<std::mutex> lock(m_Mutex);
        if (m_pHeader)
        {
            m_pHeader->locked = 0;
        }
        m_IsLocked = false;
    }
    // Notify waiting writers
    m_UnlockCondition.notify_one();
}
