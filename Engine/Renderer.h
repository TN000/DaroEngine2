// Engine/Renderer.h
#pragma once

#include <d3d11.h>
#include <d3dcompiler.h>
#include <DirectXMath.h>
#include <wrl/client.h>
#include <wincodec.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <dwrite_1.h>
#include <map>
#include <string>
#include <vector>
#include <unordered_map>
#include "SharedTypes.h"
#include "DaroEngine.h"
#include "Spout/SpoutDX.h"
#include "VideoPlayer.h"

using Microsoft::WRL::ComPtr;
using namespace DirectX;

struct TextureInfo
{
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> srv;
    int width;
    int height;
    std::string path;
};

struct SpoutReceiverInfo
{
    spoutDX receiver;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11ShaderResourceView> srv;
    std::string senderName;
    unsigned int width;
    unsigned int height;
    bool connected;
};

class DaroRenderer
{
public:
    DaroRenderer();
    ~DaroRenderer();
    
    int Initialize(int width, int height);
    void Shutdown();
    
    void BeginFrame();
    void Clear(float r, float g, float b, float a);
    bool IsDeviceLost() const { return m_DeviceLost; }
    bool CheckDeviceLost();
    void RenderLayer(const DaroLayer* layer);
    void RenderWithMasks(const DaroLayer* layers, int layerCount,
                         const std::unordered_map<int, std::vector<int>>& layerToMasks);
    void RenderBoundingBox(const DaroLayer* layer);
    void SetShowBounds(bool show) { m_ShowBounds = show; }
    bool GetShowBounds() const { return m_ShowBounds; }

    // Edge antialiasing (shader-based smooth edges for D3D11 quads)
    void SetEdgeSmoothing(float width) { m_EdgeSmoothWidth = width; }
    float GetEdgeSmoothing() const { return m_EdgeSmoothWidth; }
    
    void CopyToStaging();
    bool MapStaging(void** ppData, int* pRowPitch);
    void UnmapStaging();
    
    // Spout Output
    bool EnableSpout(const char* name);
    void DisableSpout();
    bool IsSpoutEnabled() const { return m_SpoutEnabled; }
    void SendSpout();
    
    // Texture loading
    int LoadTexture(const char* filePath);
    void UnloadTexture(int textureId);
    ID3D11ShaderResourceView* GetTextureSRV(int textureId);
    
    // Spout Input
    int GetSpoutSenderCount();
    bool GetSpoutSenderName(int index, char* buffer, int bufferSize);
    int ConnectSpoutReceiver(const char* senderName);
    void DisconnectSpoutReceiver(int receiverId);
    void UpdateSpoutReceivers();
    ID3D11ShaderResourceView* GetSpoutReceiverSRV(int receiverId);

    // Video playback
    int LoadVideo(const char* filePath);
    void UnloadVideo(int videoId);
    void PlayVideo(int videoId);
    void PauseVideo(int videoId);
    void StopVideo(int videoId);
    void SeekVideo(int videoId, int frame);
    void SeekVideoTime(int videoId, double seconds);
    bool IsVideoPlaying(int videoId);
    int GetVideoFrame(int videoId);
    int GetVideoTotalFrames(int videoId);
    void SetVideoLoop(int videoId, bool loop);
    void SetVideoAlpha(int videoId, bool alpha);
    void UpdateVideos();
    ID3D11ShaderResourceView* GetVideoSRV(int videoId);
    
private:
    bool CreateDevice();
    bool CreateRenderTarget();
    bool CreateMSAARenderTarget();
    void ResolveMSAA();
    bool CreateShaders();
    bool CreateGeometry();
    bool CreateStagingTexture();
    bool InitWIC();
    bool InitDirect2D();
    
    void UpdateConstantBuffer(const DaroLayer* layer, bool hasTexture);
    void RenderRectangle(const DaroLayer* layer);
    void RenderCircle(const DaroLayer* layer);
    void RenderText(const DaroLayer* layer, const DaroLayer* mask = nullptr);
    bool RecreateD2DTarget();

    // Masking
    bool CreateDepthStencil();
    bool CreateDepthStencilStates();
    void RenderMaskToStencil(const DaroLayer* mask);
    
private:
    int m_Width = 1920;
    int m_Height = 1080;
    
    ComPtr<ID3D11Device> m_Device;
    ComPtr<ID3D11DeviceContext> m_Context;

    // MSAA render target (4x multisampling for antialiased edges)
    ComPtr<ID3D11Texture2D> m_MSAARenderTarget;
    ComPtr<ID3D11RenderTargetView> m_MSAARTV;

    // Resolved render target (final 1x output for staging/Spout)
    ComPtr<ID3D11Texture2D> m_RenderTarget;
    ComPtr<ID3D11RenderTargetView> m_RTV;
    ComPtr<ID3D11Texture2D> m_StagingTexture;

    int m_MSAASampleCount = 4;  // 4x MSAA
    
    ComPtr<ID3D11VertexShader> m_VertexShader;
    ComPtr<ID3D11PixelShader> m_PixelShader;
    ComPtr<ID3D11InputLayout> m_InputLayout;
    ComPtr<ID3D11Buffer> m_VertexBuffer;
    ComPtr<ID3D11Buffer> m_IndexBuffer;
    ComPtr<ID3D11Buffer> m_ConstantBuffer;
    
    ComPtr<ID3D11BlendState> m_BlendState;
    ComPtr<ID3D11BlendState> m_BlendState_NoColorWrite;
    ComPtr<ID3D11SamplerState> m_Sampler;
    ComPtr<ID3D11SamplerState> m_SamplerHighQuality;  // Anisotropic filtering

    // Depth-Stencil for masking
    ComPtr<ID3D11Texture2D> m_DepthStencilTexture;
    ComPtr<ID3D11DepthStencilView> m_DepthStencilView;
    ComPtr<ID3D11DepthStencilState> m_DSState_Disabled;
    ComPtr<ID3D11DepthStencilState> m_DSState_WriteMask;
    ComPtr<ID3D11DepthStencilState> m_DSState_TestInner;
    ComPtr<ID3D11DepthStencilState> m_DSState_TestOuter;

    // WIC for image loading
    ComPtr<IWICImagingFactory> m_WICFactory;
    
    // Direct2D / DirectWrite for text
    ComPtr<ID2D1Factory1> m_D2DFactory;
    ComPtr<ID2D1RenderTarget> m_D2DRenderTarget;
    ComPtr<IDWriteFactory> m_DWriteFactory;
    
    // Textures
    std::map<int, TextureInfo> m_Textures;
    int m_NextTextureId = 1;
    
    // Spout Output
    spoutDX m_SpoutSender;
    bool m_SpoutEnabled = false;
    
    // Spout Input
    std::map<int, SpoutReceiverInfo> m_SpoutReceivers;
    int m_NextReceiverId = 1;

    // Edge antialiasing
    float m_EdgeSmoothWidth = 1.0f;  // 0=off, 0.5=low, 1.0=medium, 1.5=high

    // Debug
    bool m_ShowBounds = false;
    ComPtr<ID3D11Buffer> m_LineVertexBuffer;
    ComPtr<ID3D11RasterizerState> m_WireframeRS;

    // GPU sync - using fence for non-blocking sync
    ComPtr<ID3D11Query> m_SyncQuery;
    void WaitForGPU();
    bool m_GPUSyncPending = false;

    // Device lost detection
    bool m_DeviceLost = false;

    // State caching to reduce redundant GPU state changes
    struct CachedState
    {
        ID3D11VertexShader* vertexShader = nullptr;
        ID3D11PixelShader* pixelShader = nullptr;
        ID3D11InputLayout* inputLayout = nullptr;
        ID3D11SamplerState* sampler = nullptr;
        ID3D11BlendState* blendState = nullptr;
        ID3D11DepthStencilState* depthStencilState = nullptr;
        UINT depthStencilRef = 0;
        ID3D11ShaderResourceView* srv = nullptr;
        bool geometryBound = false;
    };
    CachedState m_CachedState;
    void ResetStateCache();
    void BindCommonState();  // Bind VS, PS, IL, sampler once per frame

    // Cached D2D brushes (reused across frames)
    ComPtr<ID2D1SolidColorBrush> m_CachedTextBrush;
    ComPtr<ID2D1SolidColorBrush> m_CachedShapeBrush;
    ComPtr<ID2D1SolidColorBrush> m_CachedBoundsBrush;
    ComPtr<ID2D1SolidColorBrush> m_CachedAnchorBrush;
    D2D1_COLOR_F m_LastTextColor = { -1, -1, -1, -1 };
    D2D1_COLOR_F m_LastShapeColor = { -1, -1, -1, -1 };

    // Cached DirectWrite TextFormat (reused when font properties match)
    ComPtr<IDWriteTextFormat> m_CachedTextFormat;
    std::wstring m_LastFontFamily;
    float m_LastFontSize = -1;
    bool m_LastFontBold = false;
    bool m_LastFontItalic = false;
    int m_LastTextAlignment = -1;
    float m_LastLineHeight = -1;

    // Cached DirectWrite RenderingParams for smooth text
    ComPtr<IDWriteRenderingParams> m_CachedSmoothRenderingParams;

    struct CBLayer
    {
        XMFLOAT4X4 transform;
        XMFLOAT4 color;
        XMFLOAT4 texTransform;
        float texRotation;
        float hasTexture;
        float edgeSmoothWidth;
        float padding;
    };
};
