// Engine/Renderer.cpp
#include "Renderer.h"
#include <algorithm>
#include <Windows.h>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")

const char* g_ShaderSource = R"(
cbuffer CBLayer : register(b0)
{
    float4x4 transform;
    float4 color;
    float4 texTransform;
    float texRotation;
    float hasTexture;
    float edgeSmoothWidth;
    float padding;
};

Texture2D tex : register(t0);
SamplerState samp : register(s0);

struct VS_INPUT
{
    float3 pos : POSITION;
    float2 uv : TEXCOORD;
};

struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD;
};

PS_INPUT VS(VS_INPUT input)
{
    PS_INPUT output;
    output.pos = mul(float4(input.pos, 1.0f), transform);
    output.uv = input.uv;
    return output;
}

float4 PS(PS_INPUT input) : SV_Target
{
    float4 result;
    if (hasTexture > 0.5f)
    {
        result = tex.Sample(samp, input.uv) * color;
    }
    else
    {
        result = color;
    }

    // Shader-based edge antialiasing: smooth alpha falloff at quad boundaries
    if (edgeSmoothWidth > 0.0f)
    {
        float2 edgeDist = min(input.uv, 1.0 - input.uv);
        float edge = min(edgeDist.x, edgeDist.y);
        float fw = fwidth(edge);
        result.a *= smoothstep(0.0, fw * edgeSmoothWidth, edge);
    }

    return result;
}
)";

DaroRenderer::DaroRenderer() {}
DaroRenderer::~DaroRenderer() { Shutdown(); }

int DaroRenderer::Initialize(int width, int height)
{
    m_Width = width;
    m_Height = height;

    if (!CreateDevice()) return DARO_ERROR_CREATE_DEVICE;
    if (!CreateRenderTarget()) return DARO_ERROR_CREATE_RT;
    if (!CreateMSAARenderTarget()) return DARO_ERROR_CREATE_RT;
    if (!CreateDepthStencil()) return DARO_ERROR_CREATE_RT;
    if (!CreateShaders()) return DARO_ERROR_CREATE_SHADERS;
    if (!CreateDepthStencilStates()) return DARO_ERROR_CREATE_SHADERS;
    if (!CreateGeometry()) return DARO_ERROR_CREATE_GEOMETRY;
    if (!CreateStagingTexture()) return DARO_ERROR_CREATE_STAGING;
    if (!InitWIC()) return DARO_ERROR_CREATE_DEVICE;
    if (!InitDirect2D()) return DARO_ERROR_CREATE_DEVICE;

    // Initialize Spout sender with device
    m_SpoutSender.OpenDirectX11(m_Device.Get());

    // Create sync query for GPU synchronization
    D3D11_QUERY_DESC queryDesc = {};
    queryDesc.Query = D3D11_QUERY_EVENT;
    if (FAILED(m_Device->CreateQuery(&queryDesc, &m_SyncQuery)))
    {
        OutputDebugStringA("[DaroEngine] Warning: Failed to create sync query, GPU sync disabled\n");
    }

    return DARO_OK;
}

void DaroRenderer::Shutdown()
{
    DisableSpout();

    // Shutdown video manager
    VideoManager::Instance().Shutdown();

    // Disconnect all Spout receivers
    for (auto& pair : m_SpoutReceivers)
    {
        pair.second.receiver.ReleaseReceiver();
        pair.second.receiver.CloseDirectX11();
    }
    m_SpoutReceivers.clear();

    m_SpoutSender.CloseDirectX11();

    // Release Direct2D/DirectWrite
    m_CachedSmoothRenderingParams.Reset();
    m_CachedTextFormat.Reset();
    m_CachedTextBrush.Reset();
    m_CachedShapeBrush.Reset();
    m_CachedBoundsBrush.Reset();
    m_CachedAnchorBrush.Reset();
    m_D2DRenderTarget.Reset();
    m_DWriteFactory.Reset();
    m_D2DFactory.Reset();

    m_Textures.clear();
    m_WICFactory.Reset();
    m_Sampler.Reset();
    m_SamplerHighQuality.Reset();
    m_BlendState.Reset();
    m_BlendState_NoColorWrite.Reset();
    m_DSState_Disabled.Reset();
    m_DSState_WriteMask.Reset();
    m_DSState_TestInner.Reset();
    m_DSState_TestOuter.Reset();
    m_DepthStencilView.Reset();
    m_DepthStencilTexture.Reset();
    m_ConstantBuffer.Reset();
    m_IndexBuffer.Reset();
    m_VertexBuffer.Reset();
    m_InputLayout.Reset();
    m_PixelShader.Reset();
    m_VertexShader.Reset();
    m_StagingTexture.Reset();
    // Note: m_MSAARTV and m_MSAARenderTarget are aliases to m_RTV/m_RenderTarget
    m_MSAARTV.Reset();
    m_MSAARenderTarget.Reset();
    m_RTV.Reset();
    m_RenderTarget.Reset();
    m_Context.Reset();
    m_Device.Reset();
}

bool DaroRenderer::InitWIC()
{
    HRESULT hr = CoCreateInstance(
        CLSID_WICImagingFactory,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&m_WICFactory)
    );
    return SUCCEEDED(hr);
}

bool DaroRenderer::InitDirect2D()
{
    // Create Direct2D factory with high-quality options
    D2D1_FACTORY_OPTIONS options = {};
#ifdef _DEBUG
    options.debugLevel = D2D1_DEBUG_LEVEL_INFORMATION;
#endif
    HRESULT hr = D2D1CreateFactory(
        D2D1_FACTORY_TYPE_SINGLE_THREADED,
        __uuidof(ID2D1Factory1),
        &options,
        reinterpret_cast<void**>(m_D2DFactory.GetAddressOf())
    );
    if (FAILED(hr)) return false;

    // Create DirectWrite factory
    hr = DWriteCreateFactory(
        DWRITE_FACTORY_TYPE_SHARED,
        __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(m_DWriteFactory.GetAddressOf())
    );
    if (FAILED(hr)) return false;

    // D2D doesn't support MSAA directly, use the resolved 1x target
    ComPtr<IDXGISurface> dxgiSurface;
    hr = m_RenderTarget.As(&dxgiSurface);
    if (FAILED(hr)) return false;

    // Create D2D render target with high-quality settings
    D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
        D2D1_RENDER_TARGET_TYPE_DEFAULT,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
        96.0f, 96.0f,  // DPI
        D2D1_RENDER_TARGET_USAGE_NONE,
        D2D1_FEATURE_LEVEL_DEFAULT
    );

    hr = m_D2DFactory->CreateDxgiSurfaceRenderTarget(dxgiSurface.Get(), &props, &m_D2DRenderTarget);
    if (FAILED(hr)) return false;

    // Set high-quality antialiasing mode for all D2D drawing
    m_D2DRenderTarget->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

    // Set high-quality text antialiasing
    m_D2DRenderTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

    return true;
}

bool DaroRenderer::RecreateD2DTarget()
{
    // Release old D2D render target and cached brushes
    m_CachedTextBrush.Reset();
    m_CachedShapeBrush.Reset();
    m_CachedBoundsBrush.Reset();
    m_CachedAnchorBrush.Reset();
    m_CachedSmoothRenderingParams.Reset();
    m_CachedTextFormat.Reset();
    m_D2DRenderTarget.Reset();
    m_LastTextColor = { -1, -1, -1, -1 };
    m_LastShapeColor = { -1, -1, -1, -1 };
    m_LastFontSize = -1;
    m_LastTextAlignment = -1;
    m_LastLineHeight = -1;
    m_LastFontFamily.clear();

    // Recreate D2D render target from existing DXGI surface
    ComPtr<IDXGISurface> dxgiSurface;
    HRESULT hr = m_RenderTarget.As(&dxgiSurface);
    if (FAILED(hr)) return false;

    D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
        D2D1_RENDER_TARGET_TYPE_DEFAULT,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED),
        96.0f, 96.0f,
        D2D1_RENDER_TARGET_USAGE_NONE,
        D2D1_FEATURE_LEVEL_DEFAULT
    );

    hr = m_D2DFactory->CreateDxgiSurfaceRenderTarget(dxgiSurface.Get(), &props, &m_D2DRenderTarget);
    if (FAILED(hr)) return false;

    m_D2DRenderTarget->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    m_D2DRenderTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

    OutputDebugStringA("[DaroRenderer] D2D render target recreated after device loss\n");
    return true;
}

bool DaroRenderer::CreateDevice()
{
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;

    D3D_FEATURE_LEVEL featureLevels[] = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0,
    };

    D3D_FEATURE_LEVEL featureLevel;
    HRESULT hr = D3D11CreateDevice(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
        featureLevels, ARRAYSIZE(featureLevels), D3D11_SDK_VERSION,
        &m_Device, &featureLevel, &m_Context
    );
    
    if (FAILED(hr))
    {
        OutputDebugStringA("[DaroEngine] Hardware D3D11 device failed, falling back to WARP software renderer\n");
        hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags,
            featureLevels, ARRAYSIZE(featureLevels), D3D11_SDK_VERSION,
            &m_Device, &featureLevel, &m_Context
        );
        if (FAILED(hr)) return false;
        OutputDebugStringA("[DaroEngine] WARNING: Running on WARP software renderer - reduced performance\n");
    }
    
    return true;
}

bool DaroRenderer::CreateRenderTarget()
{
    // Create resolved (1x) render target for final output and Spout
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = m_Width;
    desc.Height = m_Height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

    if (FAILED(m_Device->CreateTexture2D(&desc, nullptr, &m_RenderTarget))) return false;
    if (FAILED(m_Device->CreateRenderTargetView(m_RenderTarget.Get(), nullptr, &m_RTV))) return false;

    D3D11_VIEWPORT vp = {};
    vp.Width = (float)m_Width;
    vp.Height = (float)m_Height;
    vp.MaxDepth = 1.0f;
    m_Context->RSSetViewports(1, &vp);

    return true;
}

bool DaroRenderer::CreateMSAARenderTarget()
{
    // For broadcast 2D graphics, D2D's built-in antialiasing provides better quality
    // than MSAA. Use the same 1x target for both D3D11 and D2D rendering.
    // This simplifies the rendering pipeline and ensures all content is visible.
    m_MSAASampleCount = 1;
    m_MSAARenderTarget = m_RenderTarget;
    m_MSAARTV = m_RTV;

    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), nullptr);

    return true;
}

void DaroRenderer::ResolveMSAA()
{
    // No-op: using single 1x target for everything
    // D2D provides high-quality antialiasing for shapes and text
}

bool DaroRenderer::CreateShaders()
{
    ComPtr<ID3DBlob> vsBlob, psBlob, errorBlob;
    
    if (FAILED(D3DCompile(g_ShaderSource, strlen(g_ShaderSource), nullptr, nullptr, nullptr,
        "VS", "vs_5_0", 0, 0, &vsBlob, &errorBlob)))
    {
        if (errorBlob) OutputDebugStringA((const char*)errorBlob->GetBufferPointer());
        return false;
    }

    if (FAILED(m_Device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(),
        nullptr, &m_VertexShader))) return false;

    errorBlob.Reset();
    if (FAILED(D3DCompile(g_ShaderSource, strlen(g_ShaderSource), nullptr, nullptr, nullptr,
        "PS", "ps_5_0", 0, 0, &psBlob, &errorBlob)))
    {
        if (errorBlob) OutputDebugStringA((const char*)errorBlob->GetBufferPointer());
        return false;
    }
    
    if (FAILED(m_Device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(),
        nullptr, &m_PixelShader))) return false;
    
    D3D11_INPUT_ELEMENT_DESC layout[] = {
        { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0 },
    };
    
    if (FAILED(m_Device->CreateInputLayout(layout, 2, vsBlob->GetBufferPointer(),
        vsBlob->GetBufferSize(), &m_InputLayout))) return false;
    
    D3D11_BUFFER_DESC cbDesc = {};
    cbDesc.ByteWidth = sizeof(CBLayer);
    cbDesc.Usage = D3D11_USAGE_DYNAMIC;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    
    if (FAILED(m_Device->CreateBuffer(&cbDesc, nullptr, &m_ConstantBuffer))) return false;
    
    D3D11_BLEND_DESC blendDesc = {};
    blendDesc.RenderTarget[0].BlendEnable = TRUE;
    blendDesc.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
    blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
    blendDesc.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
    blendDesc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    blendDesc.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    
    if (FAILED(m_Device->CreateBlendState(&blendDesc, &m_BlendState))) return false;

    // Create no-color-write blend state (for stencil-only rendering)
    D3D11_BLEND_DESC noColorDesc = {};
    noColorDesc.RenderTarget[0].BlendEnable = FALSE;
    noColorDesc.RenderTarget[0].RenderTargetWriteMask = 0;  // No color output
    if (FAILED(m_Device->CreateBlendState(&noColorDesc, &m_BlendState_NoColorWrite))) return false;

    // Create standard linear sampler
    D3D11_SAMPLER_DESC sampDesc = {};
    sampDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampDesc.ComparisonFunc = D3D11_COMPARISON_NEVER;
    sampDesc.MinLOD = 0;
    sampDesc.MaxLOD = D3D11_FLOAT32_MAX;

    if (FAILED(m_Device->CreateSamplerState(&sampDesc, &m_Sampler))) return false;

    // Create high-quality anisotropic sampler for textures
    D3D11_SAMPLER_DESC anisoDesc = {};
    anisoDesc.Filter = D3D11_FILTER_ANISOTROPIC;
    anisoDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    anisoDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    anisoDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    anisoDesc.MaxAnisotropy = 16;  // Maximum quality
    anisoDesc.ComparisonFunc = D3D11_COMPARISON_NEVER;
    anisoDesc.MinLOD = 0;
    anisoDesc.MaxLOD = D3D11_FLOAT32_MAX;

    if (FAILED(m_Device->CreateSamplerState(&anisoDesc, &m_SamplerHighQuality))) return false;

    return true;
}

bool DaroRenderer::CreateGeometry()
{
    struct Vertex { float x, y, z, u, v; };
    Vertex vertices[] = {
        { -0.5f, -0.5f, 0.0f, 0.0f, 1.0f },
        { -0.5f,  0.5f, 0.0f, 0.0f, 0.0f },
        {  0.5f,  0.5f, 0.0f, 1.0f, 0.0f },
        {  0.5f, -0.5f, 0.0f, 1.0f, 1.0f },
    };
    
    D3D11_BUFFER_DESC vbDesc = {};
    vbDesc.ByteWidth = sizeof(vertices);
    vbDesc.Usage = D3D11_USAGE_IMMUTABLE;
    vbDesc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    D3D11_SUBRESOURCE_DATA vbData = { vertices };
    if (FAILED(m_Device->CreateBuffer(&vbDesc, &vbData, &m_VertexBuffer))) return false;
    
    UINT indices[] = { 0, 1, 2, 0, 2, 3 };
    D3D11_BUFFER_DESC ibDesc = {};
    ibDesc.ByteWidth = sizeof(indices);
    ibDesc.Usage = D3D11_USAGE_IMMUTABLE;
    ibDesc.BindFlags = D3D11_BIND_INDEX_BUFFER;
    D3D11_SUBRESOURCE_DATA ibData = { indices };
    if (FAILED(m_Device->CreateBuffer(&ibDesc, &ibData, &m_IndexBuffer))) return false;
    
    return true;
}

bool DaroRenderer::CreateStagingTexture()
{
    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = m_Width;
    desc.Height = m_Height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

    return SUCCEEDED(m_Device->CreateTexture2D(&desc, nullptr, &m_StagingTexture));
}

bool DaroRenderer::CreateDepthStencil()
{
    D3D11_TEXTURE2D_DESC dsDesc = {};
    dsDesc.Width = m_Width;
    dsDesc.Height = m_Height;
    dsDesc.MipLevels = 1;
    dsDesc.ArraySize = 1;
    dsDesc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
    dsDesc.SampleDesc.Count = m_MSAASampleCount;  // Match MSAA sample count
    dsDesc.SampleDesc.Quality = 0;
    dsDesc.Usage = D3D11_USAGE_DEFAULT;
    dsDesc.BindFlags = D3D11_BIND_DEPTH_STENCIL;

    if (FAILED(m_Device->CreateTexture2D(&dsDesc, nullptr, &m_DepthStencilTexture)))
        return false;

    D3D11_DEPTH_STENCIL_VIEW_DESC dsvDesc = {};
    dsvDesc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT;
    dsvDesc.ViewDimension = (m_MSAASampleCount > 1) ? D3D11_DSV_DIMENSION_TEXTURE2DMS : D3D11_DSV_DIMENSION_TEXTURE2D;
    dsvDesc.Texture2D.MipSlice = 0;

    if (FAILED(m_Device->CreateDepthStencilView(m_DepthStencilTexture.Get(), &dsvDesc, &m_DepthStencilView)))
        return false;

    return true;
}

bool DaroRenderer::CreateDepthStencilStates()
{
    D3D11_DEPTH_STENCIL_DESC dsDesc = {};

    // State 1: Disabled (normal rendering, no stencil test)
    dsDesc.DepthEnable = FALSE;
    dsDesc.StencilEnable = FALSE;
    if (FAILED(m_Device->CreateDepthStencilState(&dsDesc, &m_DSState_Disabled)))
        return false;

    // State 2: Write mask (write 1 to stencil where mask shape is drawn)
    dsDesc = {};
    dsDesc.DepthEnable = FALSE;
    dsDesc.StencilEnable = TRUE;
    dsDesc.StencilReadMask = 0xFF;
    dsDesc.StencilWriteMask = 0xFF;
    dsDesc.FrontFace.StencilFailOp = D3D11_STENCIL_OP_KEEP;
    dsDesc.FrontFace.StencilDepthFailOp = D3D11_STENCIL_OP_KEEP;
    dsDesc.FrontFace.StencilPassOp = D3D11_STENCIL_OP_REPLACE;
    dsDesc.FrontFace.StencilFunc = D3D11_COMPARISON_ALWAYS;
    dsDesc.BackFace = dsDesc.FrontFace;
    if (FAILED(m_Device->CreateDepthStencilState(&dsDesc, &m_DSState_WriteMask)))
        return false;

    // State 3: Test Inner (pass where stencil == ref, i.e., inside mask)
    dsDesc = {};
    dsDesc.DepthEnable = FALSE;
    dsDesc.StencilEnable = TRUE;
    dsDesc.StencilReadMask = 0xFF;
    dsDesc.StencilWriteMask = 0x00;
    dsDesc.FrontFace.StencilFailOp = D3D11_STENCIL_OP_KEEP;
    dsDesc.FrontFace.StencilDepthFailOp = D3D11_STENCIL_OP_KEEP;
    dsDesc.FrontFace.StencilPassOp = D3D11_STENCIL_OP_KEEP;
    dsDesc.FrontFace.StencilFunc = D3D11_COMPARISON_EQUAL;
    dsDesc.BackFace = dsDesc.FrontFace;
    if (FAILED(m_Device->CreateDepthStencilState(&dsDesc, &m_DSState_TestInner)))
        return false;

    // State 4: Test Outer (pass where stencil != ref, i.e., outside mask)
    dsDesc.FrontFace.StencilFunc = D3D11_COMPARISON_NOT_EQUAL;
    dsDesc.BackFace = dsDesc.FrontFace;
    if (FAILED(m_Device->CreateDepthStencilState(&dsDesc, &m_DSState_TestOuter)))
        return false;

    return true;
}

void DaroRenderer::ResetStateCache()
{
    m_CachedState = CachedState();
}

void DaroRenderer::BindCommonState()
{
    // Bind shaders, input layout ONCE per frame
    if (m_CachedState.vertexShader != m_VertexShader.Get())
    {
        m_Context->VSSetShader(m_VertexShader.Get(), nullptr, 0);
        m_CachedState.vertexShader = m_VertexShader.Get();
    }
    if (m_CachedState.pixelShader != m_PixelShader.Get())
    {
        m_Context->PSSetShader(m_PixelShader.Get(), nullptr, 0);
        m_CachedState.pixelShader = m_PixelShader.Get();
    }
    if (m_CachedState.inputLayout != m_InputLayout.Get())
    {
        m_Context->IASetInputLayout(m_InputLayout.Get());
        m_CachedState.inputLayout = m_InputLayout.Get();
    }

    // Use high-quality anisotropic sampler for better texture filtering
    if (m_CachedState.sampler != m_SamplerHighQuality.Get())
    {
        m_Context->PSSetSamplers(0, 1, m_SamplerHighQuality.GetAddressOf());
        m_CachedState.sampler = m_SamplerHighQuality.Get();
    }

    // Bind constant buffers (these don't change)
    m_Context->VSSetConstantBuffers(0, 1, m_ConstantBuffer.GetAddressOf());
    m_Context->PSSetConstantBuffers(0, 1, m_ConstantBuffer.GetAddressOf());

    // Bind geometry once
    if (!m_CachedState.geometryBound)
    {
        UINT stride = 20, offset = 0;
        m_Context->IASetVertexBuffers(0, 1, m_VertexBuffer.GetAddressOf(), &stride, &offset);
        m_Context->IASetIndexBuffer(m_IndexBuffer.Get(), DXGI_FORMAT_R32_UINT, 0);
        m_Context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        m_CachedState.geometryBound = true;
    }
}

bool DaroRenderer::CheckDeviceLost()
{
    if (m_DeviceLost) return true;
    HRESULT reason = m_Device->GetDeviceRemovedReason();
    if (reason != S_OK)
    {
        m_DeviceLost = true;
        char buf[256];
        sprintf_s(buf, "[DaroRenderer] GPU device lost! Reason: 0x%08X\n", (unsigned int)reason);
        OutputDebugStringA(buf);
    }
    return m_DeviceLost;
}

void DaroRenderer::BeginFrame()
{
    // Check for GPU device lost at start of each frame
    if (CheckDeviceLost()) return;

    // Reset state cache at start of frame
    ResetStateCache();

    // Use single render target with depth-stencil
    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), m_DepthStencilView.Get());

    // Cache blend and depth-stencil state
    if (m_CachedState.blendState != m_BlendState.Get())
    {
        m_Context->OMSetBlendState(m_BlendState.Get(), nullptr, 0xFFFFFFFF);
        m_CachedState.blendState = m_BlendState.Get();
    }
    if (m_CachedState.depthStencilState != m_DSState_Disabled.Get())
    {
        m_Context->OMSetDepthStencilState(m_DSState_Disabled.Get(), 0);
        m_CachedState.depthStencilState = m_DSState_Disabled.Get();
        m_CachedState.depthStencilRef = 0;
    }

    // Bind common state once per frame
    BindCommonState();

    // Update Spout receivers
    UpdateSpoutReceivers();

    // Update video frames
    UpdateVideos();
}

void DaroRenderer::Clear(float r, float g, float b, float a)
{
    float color[4] = { r, g, b, a };
    m_Context->ClearRenderTargetView(m_RTV.Get(), color);
    m_Context->ClearDepthStencilView(m_DepthStencilView.Get(), D3D11_CLEAR_STENCIL, 0.0f, 0);
}

void DaroRenderer::RenderLayer(const DaroLayer* layer)
{
    if (!layer || !layer->active) return;

    // Dispatch based on layer type
    switch (layer->layerType)
    {
        case DARO_TYPE_TEXT:
            RenderText(layer);
            return;
        case DARO_TYPE_CIRCLE:
            RenderCircle(layer);
            return;
        case DARO_TYPE_MASK:
        case DARO_TYPE_GROUP:
            // Masks and groups are not rendered directly
            return;
        default:
            // Rectangle, Image, Video - render as textured quad
            RenderRectangle(layer);
            return;
    }
}

void DaroRenderer::RenderRectangle(const DaroLayer* layer)
{
    // Get texture if layer has one
    ID3D11ShaderResourceView* srv = nullptr;
    bool hasTexture = false;

    if (layer->sourceType == 2 && layer->textureId > 0) // ImageFile
    {
        srv = GetTextureSRV(layer->textureId);
        hasTexture = (srv != nullptr);
    }
    else if (layer->sourceType == 1 && layer->spoutReceiverId > 0) // SpoutInput
    {
        srv = GetSpoutReceiverSRV(layer->spoutReceiverId);
        hasTexture = (srv != nullptr);
    }
    else if (layer->sourceType == 3 && layer->textureId > 0) // VideoFile (textureId = videoId)
    {
        srv = GetVideoSRV(layer->textureId);
        hasTexture = (srv != nullptr);
        // Debug: log first few frames of video rendering
        static int sVideoRenderLog = 0;
        if (sVideoRenderLog < 5)
        {
            char dbg[256];
            sprintf_s(dbg, "[DaroVideo] RenderRect: videoId=%d, srv=%p, hasTexture=%d\n",
                layer->textureId, srv, hasTexture ? 1 : 0);
            OutputDebugStringA(dbg);
            sVideoRenderLog++;
        }
    }

    UpdateConstantBuffer(layer, hasTexture);

    // Use cached state - only set SRV if changed
    if (m_CachedState.srv != srv)
    {
        if (srv)
        {
            m_Context->PSSetShaderResources(0, 1, &srv);
        }
        else
        {
            ID3D11ShaderResourceView* nullSRV = nullptr;
            m_Context->PSSetShaderResources(0, 1, &nullSRV);
        }
        m_CachedState.srv = srv;
    }

    m_Context->DrawIndexed(6, 0, 0);
}

void DaroRenderer::RenderCircle(const DaroLayer* layer)
{
    if (!m_D2DRenderTarget) return;

    // Unbind depth-stencil for D2D interop (D2D and depth-stencil don't mix)
    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), nullptr);
    m_Context->Flush();

    // Use cached brush - create only if needed, update color if changed
    D2D1_COLOR_F color = D2D1::ColorF(
        layer->colorR,
        layer->colorG,
        layer->colorB,
        layer->colorA * layer->opacity
    );

    if (!m_CachedShapeBrush)
    {
        HRESULT hr = m_D2DRenderTarget->CreateSolidColorBrush(color, &m_CachedShapeBrush);
        if (FAILED(hr) || !m_CachedShapeBrush) return;
        m_LastShapeColor = color;
    }
    else if (color.r != m_LastShapeColor.r || color.g != m_LastShapeColor.g ||
             color.b != m_LastShapeColor.b || color.a != m_LastShapeColor.a)
    {
        m_CachedShapeBrush->SetColor(color);
        m_LastShapeColor = color;
    }

    // Calculate circle center and radius
    float radius = (std::min)(layer->sizeX, layer->sizeY) * 0.5f;
    D2D1_ELLIPSE ellipse = D2D1::Ellipse(
        D2D1::Point2F(layer->posX, layer->posY),
        radius,
        radius
    );

    m_D2DRenderTarget->BeginDraw();

    // High-quality antialiasing for smooth edges
    m_D2DRenderTarget->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

    m_D2DRenderTarget->FillEllipse(ellipse, m_CachedShapeBrush.Get());

    HRESULT hrEnd = m_D2DRenderTarget->EndDraw();
    if (hrEnd == D2DERR_RECREATE_TARGET)
    {
        OutputDebugStringA("[DaroRenderer] D2D device lost in RenderCircle, recreating target\n");
        RecreateD2DTarget();
    }
    else if (FAILED(hrEnd))
    {
        OutputDebugStringA("[DaroRenderer] D2D EndDraw failed in RenderCircle\n");
    }

    // Flush D2D content to shared surface
    m_Context->Flush();

    // Restore render target with depth-stencil
    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), m_DepthStencilView.Get());
}

void DaroRenderer::RenderText(const DaroLayer* layer, const DaroLayer* mask)
{
    if (!m_D2DRenderTarget || !m_DWriteFactory || !m_D2DFactory) return;
    if (!layer->textContent[0]) return; // No text to render

    // Unbind depth-stencil for D2D interop
    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), nullptr);
    m_Context->Flush();

    float fontSize = layer->fontSize > 0 ? layer->fontSize : 48.0f;
    bool fontBold = layer->fontBold != 0;
    bool fontItalic = layer->fontItalic != 0;
    std::wstring fontFamily(layer->fontFamily, wcsnlen(layer->fontFamily, DARO_MAX_FONTNAME));

    // Use cached TextFormat if font properties match, otherwise create new
    bool needNewFormat = !m_CachedTextFormat ||
                         m_LastFontFamily != fontFamily ||
                         m_LastFontSize != fontSize ||
                         m_LastFontBold != fontBold ||
                         m_LastFontItalic != fontItalic ||
                         m_LastTextAlignment != layer->textAlignment ||
                         m_LastLineHeight != layer->lineHeight;

    HRESULT hr = S_OK;

    if (needNewFormat)
    {
        DWRITE_FONT_WEIGHT fontWeight = fontBold ? DWRITE_FONT_WEIGHT_BOLD : DWRITE_FONT_WEIGHT_NORMAL;
        DWRITE_FONT_STYLE fontStyle = fontItalic ? DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE_NORMAL;

        m_CachedTextFormat.Reset();
        hr = m_DWriteFactory->CreateTextFormat(
            layer->fontFamily,
            nullptr,
            fontWeight,
            fontStyle,
            DWRITE_FONT_STRETCH_NORMAL,
            fontSize,
            L"",
            &m_CachedTextFormat
        );

        if (FAILED(hr) || !m_CachedTextFormat) return;

        // Set alignment
        switch (layer->textAlignment)
        {
            case DARO_ALIGN_LEFT:
                m_CachedTextFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
                break;
            case DARO_ALIGN_CENTER:
                m_CachedTextFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
                break;
            case DARO_ALIGN_RIGHT:
                m_CachedTextFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_TRAILING);
                break;
        }
        m_CachedTextFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

        // Set line spacing (affects multi-line text)
        if (layer->lineHeight > 0)
        {
            float lineSpacing = fontSize * layer->lineHeight;
            float baseline = fontSize * 0.8f; // Standard baseline ratio
            m_CachedTextFormat->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, lineSpacing, baseline);
        }

        // Cache the font properties
        m_LastFontFamily = fontFamily;
        m_LastFontSize = fontSize;
        m_LastFontBold = fontBold;
        m_LastFontItalic = fontItalic;
        m_LastTextAlignment = layer->textAlignment;
        m_LastLineHeight = layer->lineHeight;
    }

    // Ensure shape antialiasing is enabled for mask geometry
    m_D2DRenderTarget->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);

    // Set text antialiasing mode based on layer settings
    if (layer->textAntialiasMode == 1)
    {
        m_D2DRenderTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_ALIASED);
        m_D2DRenderTarget->SetTextRenderingParams(nullptr);
    }
    else
    {
        // Use high-quality grayscale antialiasing (best for transparent backgrounds)
        m_D2DRenderTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

        // Use cached high-quality rendering params
        if (!m_CachedSmoothRenderingParams)
        {
            ComPtr<IDWriteRenderingParams> defaultParams;
            m_DWriteFactory->CreateRenderingParams(&defaultParams);
            if (defaultParams)
            {
                // Create custom params with enhanced quality
                m_DWriteFactory->CreateCustomRenderingParams(
                    defaultParams->GetGamma(),
                    defaultParams->GetEnhancedContrast() + 0.3f,  // Slightly enhanced contrast
                    0.0f,  // No ClearType level (works better on colored backgrounds)
                    DWRITE_PIXEL_GEOMETRY_FLAT,  // No subpixel (avoids color fringing)
                    DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,  // Best quality for moving text
                    &m_CachedSmoothRenderingParams
                );
            }
        }
        if (m_CachedSmoothRenderingParams)
            m_D2DRenderTarget->SetTextRenderingParams(m_CachedSmoothRenderingParams.Get());
    }

    // Use cached brush - create only if needed, update color if changed
    D2D1_COLOR_F color = D2D1::ColorF(
        layer->colorR,
        layer->colorG,
        layer->colorB,
        layer->colorA * layer->opacity
    );

    if (!m_CachedTextBrush)
    {
        hr = m_D2DRenderTarget->CreateSolidColorBrush(color, &m_CachedTextBrush);
        if (FAILED(hr) || !m_CachedTextBrush) return;
        m_LastTextColor = color;
    }
    else if (color.r != m_LastTextColor.r || color.g != m_LastTextColor.g ||
             color.b != m_LastTextColor.b || color.a != m_LastTextColor.a)
    {
        m_CachedTextBrush->SetColor(color);
        m_LastTextColor = color;
    }

    // Calculate text rectangle
    float left = layer->posX - layer->sizeX * 0.5f;
    float top = layer->posY - layer->sizeY * 0.5f;
    D2D1_RECT_F layoutRect = D2D1::RectF(left, top, left + layer->sizeX, top + layer->sizeY);

    // Create mask geometry if mask is provided
    ComPtr<ID2D1Geometry> maskGeometry;
    if (mask)
    {
        float maskLeft = mask->posX - mask->sizeX * 0.5f;
        float maskTop = mask->posY - mask->sizeY * 0.5f;
        D2D1_RECT_F maskRect = D2D1::RectF(maskLeft, maskTop, maskLeft + mask->sizeX, maskTop + mask->sizeY);

        if (mask->layerType == DARO_TYPE_CIRCLE)
        {
            ComPtr<ID2D1EllipseGeometry> ellipse;
            D2D1_ELLIPSE ellipseData = D2D1::Ellipse(
                D2D1::Point2F(mask->posX, mask->posY),
                mask->sizeX * 0.5f,
                mask->sizeY * 0.5f
            );
            if (SUCCEEDED(m_D2DFactory->CreateEllipseGeometry(ellipseData, &ellipse)))
                maskGeometry = ellipse;
        }
        else
        {
            ComPtr<ID2D1RectangleGeometry> rect;
            if (SUCCEEDED(m_D2DFactory->CreateRectangleGeometry(maskRect, &rect)))
                maskGeometry = rect;
        }
    }

    // Draw text
    m_D2DRenderTarget->BeginDraw();

    // Apply mask using D2D layer if mask geometry exists
    ComPtr<ID2D1PathGeometry> outerMaskGeometry;  // Keep alive for Outer mode
    bool useMaskLayer = (maskGeometry && mask);

    if (useMaskLayer)
    {
        D2D1_LAYER_PARAMETERS layerParams = D2D1::LayerParameters();
        layerParams.maskAntialiasMode = D2D1_ANTIALIAS_MODE_PER_PRIMITIVE;
        layerParams.opacity = 1.0f;

        // For Outer mode, we need to invert the mask - create geometry with a hole
        if (mask->maskMode == 1)  // Outer
        {
            float maskCenterX = mask->posX;
            float maskCenterY = mask->posY;
            float maskRadiusX = mask->sizeX * 0.5f;
            float maskRadiusY = mask->sizeY * 0.5f;

            // Create path geometry with outer rect and inner hole
            hr = m_D2DFactory->CreatePathGeometry(&outerMaskGeometry);
            ComPtr<ID2D1GeometrySink> sink;
            if (SUCCEEDED(hr) && outerMaskGeometry)
            {
                hr = outerMaskGeometry->Open(&sink);
            }

            if (SUCCEEDED(hr) && sink)
            {
                sink->SetFillMode(D2D1_FILL_MODE_WINDING);

                // Outer rectangle (clockwise winding = +1)
                sink->BeginFigure(D2D1::Point2F(0, 0), D2D1_FIGURE_BEGIN_FILLED);
                sink->AddLine(D2D1::Point2F((float)m_Width, 0));
                sink->AddLine(D2D1::Point2F((float)m_Width, (float)m_Height));
                sink->AddLine(D2D1::Point2F(0, (float)m_Height));
                sink->EndFigure(D2D1_FIGURE_END_CLOSED);

                // Inner hole (counter-clockwise winding = -1, cancels out with outer)
                if (mask->layerType == DARO_TYPE_CIRCLE)
                {
                    // Ellipse hole using arcs (counter-clockwise)
                    sink->BeginFigure(D2D1::Point2F(maskCenterX, maskCenterY - maskRadiusY), D2D1_FIGURE_BEGIN_FILLED);
                    D2D1_ARC_SEGMENT arc1 = {};
                    arc1.point = D2D1::Point2F(maskCenterX, maskCenterY + maskRadiusY);
                    arc1.size = D2D1::SizeF(maskRadiusX, maskRadiusY);
                    arc1.rotationAngle = 0.0f;
                    arc1.sweepDirection = D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE;
                    arc1.arcSize = D2D1_ARC_SIZE_LARGE;
                    sink->AddArc(arc1);
                    D2D1_ARC_SEGMENT arc2 = {};
                    arc2.point = D2D1::Point2F(maskCenterX, maskCenterY - maskRadiusY);
                    arc2.size = D2D1::SizeF(maskRadiusX, maskRadiusY);
                    arc2.rotationAngle = 0.0f;
                    arc2.sweepDirection = D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE;
                    arc2.arcSize = D2D1_ARC_SIZE_LARGE;
                    sink->AddArc(arc2);
                    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
                }
                else
                {
                    // Rectangle hole (counter-clockwise)
                    float maskLeft = maskCenterX - maskRadiusX;
                    float maskTop = maskCenterY - maskRadiusY;
                    float maskRight = maskCenterX + maskRadiusX;
                    float maskBottom = maskCenterY + maskRadiusY;
                    sink->BeginFigure(D2D1::Point2F(maskLeft, maskTop), D2D1_FIGURE_BEGIN_FILLED);
                    sink->AddLine(D2D1::Point2F(maskLeft, maskBottom));
                    sink->AddLine(D2D1::Point2F(maskRight, maskBottom));
                    sink->AddLine(D2D1::Point2F(maskRight, maskTop));
                    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
                }

                sink->Close();
                layerParams.geometricMask = outerMaskGeometry.Get();
            }
            else
            {
                useMaskLayer = false;  // Geometry creation failed, skip mask
            }
        }
        else
        {
            // Inner mode - use mask geometry directly
            layerParams.geometricMask = maskGeometry.Get();
        }

        if (useMaskLayer)
            m_D2DRenderTarget->PushLayer(layerParams, nullptr);
    }

    // Create text layout for advanced formatting (letter spacing)
    UINT32 textLength = (UINT32)wcsnlen(layer->textContent, DARO_MAX_TEXT);
    ComPtr<IDWriteTextLayout> textLayout;
    hr = m_DWriteFactory->CreateTextLayout(
        layer->textContent,
        textLength,
        m_CachedTextFormat.Get(),
        layer->sizeX,
        layer->sizeY,
        &textLayout
    );

    if (SUCCEEDED(hr) && textLayout)
    {
        // Apply letter spacing if set (value is in pixels)
        if (layer->letterSpacing != 0)
        {
            ComPtr<IDWriteTextLayout1> textLayout1;
            if (SUCCEEDED(textLayout.As(&textLayout1)))
            {
                DWRITE_TEXT_RANGE range = { 0, textLength };
                // Apply spacing evenly before and after each character
                float spacing = layer->letterSpacing;
                textLayout1->SetCharacterSpacing(
                    spacing,  // leading spacing
                    spacing,  // trailing spacing
                    0,        // minimum advance width
                    range
                );
            }
        }

        // Draw using text layout
        m_D2DRenderTarget->DrawTextLayout(
            D2D1::Point2F(left, top),
            textLayout.Get(),
            m_CachedTextBrush.Get()
        );
    }
    else
    {
        // Fallback to simple DrawText if layout creation fails
        m_D2DRenderTarget->DrawText(
            layer->textContent,
            textLength,
            m_CachedTextFormat.Get(),
            layoutRect,
            m_CachedTextBrush.Get()
        );
    }

    // Pop the mask layer if we pushed one
    if (useMaskLayer)
    {
        m_D2DRenderTarget->PopLayer();
    }

    HRESULT hrEnd = m_D2DRenderTarget->EndDraw();
    if (hrEnd == D2DERR_RECREATE_TARGET)
    {
        OutputDebugStringA("[DaroRenderer] D2D device lost in RenderText, recreating target\n");
        RecreateD2DTarget();
    }
    else if (FAILED(hrEnd))
    {
        OutputDebugStringA("[DaroRenderer] D2D EndDraw failed in RenderText\n");
    }

    // Flush D2D content to shared surface
    m_Context->Flush();

    // Restore render target with depth-stencil
    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), m_DepthStencilView.Get());
}

void DaroRenderer::RenderBoundingBox(const DaroLayer* layer)
{
    if (!m_D2DRenderTarget || !m_D2DFactory) return;

    // Unbind depth-stencil for D2D interop
    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), nullptr);
    m_Context->Flush();

    // Create wireframe brush (green color)
    ComPtr<ID2D1SolidColorBrush> brush;
    D2D1_COLOR_F color = D2D1::ColorF(0.0f, 1.0f, 0.0f, 0.8f);  // Green with alpha
    HRESULT hr = m_D2DRenderTarget->CreateSolidColorBrush(color, &brush);
    if (FAILED(hr) || !brush) return;

    // Calculate bounding box rectangle
    float left = layer->posX - layer->sizeX * 0.5f;
    float top = layer->posY - layer->sizeY * 0.5f;
    D2D1_RECT_F rect = D2D1::RectF(left, top, left + layer->sizeX, top + layer->sizeY);

    // Draw anchor point indicator
    ComPtr<ID2D1SolidColorBrush> anchorBrush;
    D2D1_COLOR_F anchorColor = D2D1::ColorF(1.0f, 0.0f, 0.0f, 1.0f);  // Red
    m_D2DRenderTarget->CreateSolidColorBrush(anchorColor, &anchorBrush);

    float anchorX = layer->posX + (layer->anchorX - 0.5f) * layer->sizeX;
    float anchorY = layer->posY + (layer->anchorY - 0.5f) * layer->sizeY;

    m_D2DRenderTarget->BeginDraw();

    // Draw bounding rectangle
    m_D2DRenderTarget->DrawRectangle(rect, brush.Get(), 2.0f);

    // Draw anchor point (cross)
    if (anchorBrush)
    {
        float crossSize = 8.0f;
        m_D2DRenderTarget->DrawLine(
            D2D1::Point2F(anchorX - crossSize, anchorY),
            D2D1::Point2F(anchorX + crossSize, anchorY),
            anchorBrush.Get(), 2.0f);
        m_D2DRenderTarget->DrawLine(
            D2D1::Point2F(anchorX, anchorY - crossSize),
            D2D1::Point2F(anchorX, anchorY + crossSize),
            anchorBrush.Get(), 2.0f);
    }

    HRESULT hrEnd = m_D2DRenderTarget->EndDraw();
    if (hrEnd == D2DERR_RECREATE_TARGET)
    {
        OutputDebugStringA("[DaroRenderer] D2D device lost in RenderBoundingBox, recreating target\n");
        RecreateD2DTarget();
    }
    else if (FAILED(hrEnd))
    {
        OutputDebugStringA("[DaroRenderer] D2D EndDraw failed in RenderBoundingBox\n");
    }

    // Flush D2D content
    m_Context->Flush();

    // Restore render target with depth-stencil
    m_Context->OMSetRenderTargets(1, m_RTV.GetAddressOf(), m_DepthStencilView.Get());
}

void DaroRenderer::RenderMaskToStencil(const DaroLayer* mask)
{
    // Set stencil write state (write 1 to stencil, don't write color) - with caching
    if (m_CachedState.depthStencilState != m_DSState_WriteMask.Get() || m_CachedState.depthStencilRef != 1)
    {
        m_Context->OMSetDepthStencilState(m_DSState_WriteMask.Get(), 1);
        m_CachedState.depthStencilState = m_DSState_WriteMask.Get();
        m_CachedState.depthStencilRef = 1;
    }
    if (m_CachedState.blendState != m_BlendState_NoColorWrite.Get())
    {
        m_Context->OMSetBlendState(m_BlendState_NoColorWrite.Get(), nullptr, 0xFFFFFFFF);
        m_CachedState.blendState = m_BlendState_NoColorWrite.Get();
    }

    // Create temp layer with full opacity for stencil write
    DaroLayer tempMask = *mask;
    tempMask.colorR = tempMask.colorG = tempMask.colorB = tempMask.colorA = 1.0f;
    tempMask.opacity = 1.0f;

    // Note: D2D circles don't write to D3D11 stencil buffer, so we use
    // the bounding rectangle for stencil masks. For perfect circle masking,
    // use D2D geometry masking (which is used for text layers).
    RenderRectangle(&tempMask);

    // Restore blend state - with caching
    if (m_CachedState.blendState != m_BlendState.Get())
    {
        m_Context->OMSetBlendState(m_BlendState.Get(), nullptr, 0xFFFFFFFF);
        m_CachedState.blendState = m_BlendState.Get();
    }
}

void DaroRenderer::RenderWithMasks(const DaroLayer* layers, int layerCount,
                                   const std::unordered_map<int, std::vector<int>>& layerToMasks)
{
    // Set default state (no stencil) - with caching
    if (m_CachedState.depthStencilState != m_DSState_Disabled.Get())
    {
        m_Context->OMSetDepthStencilState(m_DSState_Disabled.Get(), 0);
        m_CachedState.depthStencilState = m_DSState_Disabled.Get();
        m_CachedState.depthStencilRef = 0;
    }

    for (int i = 0; i < layerCount; i++)
    {
        const DaroLayer* layer = &layers[i];
        if (!layer->active) continue;

        // Render mask layers visually when active (for preview)
        if (layer->layerType == DARO_TYPE_MASK)
        {
            RenderRectangle(layer);
            if (m_ShowBounds)
                RenderBoundingBox(layer);
            continue;
        }

        // Skip group layers - they don't render visually
        if (layer->layerType == DARO_TYPE_GROUP)
            continue;

        // Check if this layer is masked
        auto it = layerToMasks.find(layer->id);
        if (it != layerToMasks.end() && !it->second.empty())
        {
            int maskIndex = it->second[0];
            // Bounds check to prevent memory corruption
            if (maskIndex < 0 || maskIndex >= layerCount)
                continue;
            const DaroLayer* mask = &layers[maskIndex];

            // Text layers use D2D geometry masking instead of stencil buffer
            if (layer->layerType == DARO_TYPE_TEXT)
            {
                RenderText(layer, mask);
            }
            else
            {
                // Non-text layers use stencil buffer masking
                m_Context->ClearDepthStencilView(m_DepthStencilView.Get(), D3D11_CLEAR_STENCIL, 0.0f, 0);
                RenderMaskToStencil(mask);

                // Set depth-stencil state with caching
                ID3D11DepthStencilState* targetState = (mask->maskMode == 0) ?
                    m_DSState_TestInner.Get() : m_DSState_TestOuter.Get();

                if (m_CachedState.depthStencilState != targetState || m_CachedState.depthStencilRef != 1)
                {
                    m_Context->OMSetDepthStencilState(targetState, 1);
                    m_CachedState.depthStencilState = targetState;
                    m_CachedState.depthStencilRef = 1;
                }

                RenderLayer(layer);

                // Restore disabled state with caching
                if (m_CachedState.depthStencilState != m_DSState_Disabled.Get())
                {
                    m_Context->OMSetDepthStencilState(m_DSState_Disabled.Get(), 0);
                    m_CachedState.depthStencilState = m_DSState_Disabled.Get();
                    m_CachedState.depthStencilRef = 0;
                }
            }
        }
        else
        {
            // No masking, render normally
            RenderLayer(layer);
        }

        // Draw bounding box if enabled
        if (m_ShowBounds)
            RenderBoundingBox(layer);
    }

    // Flush D2D/D3D content - required for surface synchronization
    m_Context->Flush();
    WaitForGPU();
}

void DaroRenderer::UpdateConstantBuffer(const DaroLayer* layer, bool hasTexture)
{
    // Anchor offset for rotation: anchor is 0-1, where 0.5 is center
    float anchorOffsetX = (layer->anchorX - 0.5f) * layer->sizeX;
    float anchorOffsetY = (layer->anchorY - 0.5f) * layer->sizeY;

    // Scale from center (position adjustment happens in Designer when size changes)
    XMMATRIX scale = XMMatrixScaling(layer->sizeX, layer->sizeY, 1.0f);

    // Rotation around anchor point
    XMMATRIX toAnchor = XMMatrixTranslation(-anchorOffsetX, anchorOffsetY, 0.0f);
    XMMATRIX rotZ = XMMatrixRotationZ(XMConvertToRadians(layer->rotZ));
    XMMATRIX rotY = XMMatrixRotationY(XMConvertToRadians(layer->rotY));
    XMMATRIX rotX = XMMatrixRotationX(XMConvertToRadians(layer->rotX));
    XMMATRIX fromAnchor = XMMatrixTranslation(anchorOffsetX, -anchorOffsetY, 0.0f);

    // Translate to world position
    XMMATRIX translation = XMMatrixTranslation(
        layer->posX - m_Width * 0.5f,
        -(layer->posY - m_Height * 0.5f),
        0.0f
    );
    XMMATRIX projection = XMMatrixOrthographicLH((float)m_Width, (float)m_Height, 0.0f, 1.0f);

    // Transform: scale from center, then rotate around anchor, then translate
    XMMATRIX wvp = scale * toAnchor * rotZ * rotY * rotX * fromAnchor * translation * projection;
    
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (SUCCEEDED(m_Context->Map(m_ConstantBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
    {
        CBLayer* cb = (CBLayer*)mapped.pData;
        XMStoreFloat4x4(&cb->transform, XMMatrixTranspose(wvp));
        cb->color = XMFLOAT4(layer->colorR * layer->opacity, layer->colorG * layer->opacity,
            layer->colorB * layer->opacity, layer->opacity);
        cb->texTransform = XMFLOAT4(layer->texX, layer->texY, layer->texW, layer->texH);
        cb->texRotation = layer->texRot;
        cb->hasTexture = hasTexture ? 1.0f : 0.0f;
        cb->edgeSmoothWidth = m_EdgeSmoothWidth;
        m_Context->Unmap(m_ConstantBuffer.Get(), 0);
    }
}

void DaroRenderer::CopyToStaging()
{
    m_Context->CopyResource(m_StagingTexture.Get(), m_RenderTarget.Get());
}

bool DaroRenderer::MapStaging(void** ppData, int* pRowPitch)
{
    if (!ppData || !pRowPitch || !m_Context) return false;
    D3D11_MAPPED_SUBRESOURCE mapped;
    if (FAILED(m_Context->Map(m_StagingTexture.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    *ppData = mapped.pData;
    *pRowPitch = mapped.RowPitch;
    return true;
}

void DaroRenderer::UnmapStaging() { m_Context->Unmap(m_StagingTexture.Get(), 0); }

void DaroRenderer::WaitForGPU()
{
    if (!m_SyncQuery) return;

    // Issue an event query and wait for GPU to complete
    m_Context->End(m_SyncQuery.Get());

    BOOL done = FALSE;
    int spinCount = 0;
    const int MAX_SPINS_BEFORE_YIELD = 100;
    const int MAX_SPINS_BEFORE_SLEEP = 1000;

    while (!done)
    {
        HRESULT hr = m_Context->GetData(m_SyncQuery.Get(), &done, sizeof(done), D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if (hr == S_OK) break;
        if (FAILED(hr)) break;

        spinCount++;

        // Adaptive backoff: spin first, then yield, then sleep
        if (spinCount < MAX_SPINS_BEFORE_YIELD)
        {
            // Busy spin - fastest but uses CPU
            YieldProcessor();
        }
        else if (spinCount < MAX_SPINS_BEFORE_SLEEP)
        {
            // Yield to other threads
            SwitchToThread();
        }
        else
        {
            // Actually sleep - gives up time slice
            Sleep(1);
        }
    }
}

// ============== Spout Output ==============

bool DaroRenderer::EnableSpout(const char* name)
{
    if (m_SpoutEnabled) return true;
    if (!m_Device) return false;
    if (!name || name[0] == '\0') return false;

    // Spout sender names are limited to 256 chars (SpoutSenderNames.h uses fixed buffers)
    size_t nameLen = strlen(name);
    if (nameLen > 255)
    {
        OutputDebugStringA("[DaroEngine] Spout sender name too long (max 255 chars)\n");
        return false;
    }

    m_SpoutSender.SetSenderName(name);
    m_SpoutEnabled = true;
    return true;
}

void DaroRenderer::DisableSpout()
{
    if (!m_SpoutEnabled) return;
    m_SpoutSender.ReleaseSender();
    m_SpoutEnabled = false;
}

void DaroRenderer::SendSpout()
{
    if (!m_SpoutEnabled || !m_RenderTarget) return;
    m_SpoutSender.SendTexture(m_RenderTarget.Get());
}

// ============== Texture Loading ==============

int DaroRenderer::LoadTexture(const char* filePath)
{
    if (!m_WICFactory || !m_Device) return -1;
    if (!filePath || filePath[0] == '\0') return -1;

    // Security: Basic path traversal protection
    // Reject paths containing ".." to prevent directory traversal attacks
    if (strstr(filePath, "..") != nullptr)
    {
        OutputDebugStringA("[DaroEngine] Security: Path traversal attempt blocked in LoadTexture\n");
        return -1;
    }

    // Check if already loaded
    for (auto& pair : m_Textures)
    {
        if (pair.second.path == filePath)
            return pair.first;
    }

    // Convert path to wide string
    int wideLen = MultiByteToWideChar(CP_UTF8, 0, filePath, -1, nullptr, 0);
    if (wideLen <= 0) return -1;
    std::wstring widePath(wideLen, 0);
    MultiByteToWideChar(CP_UTF8, 0, filePath, -1, &widePath[0], wideLen);
    
    // Load image with WIC
    ComPtr<IWICBitmapDecoder> decoder;
    HRESULT hr = m_WICFactory->CreateDecoderFromFilename(
        widePath.c_str(),
        nullptr,
        GENERIC_READ,
        WICDecodeMetadataCacheOnDemand,
        &decoder
    );
    if (FAILED(hr)) return -1;
    
    ComPtr<IWICBitmapFrameDecode> frame;
    hr = decoder->GetFrame(0, &frame);
    if (FAILED(hr)) return -1;
    
    UINT width = 0, height = 0;
    hr = frame->GetSize(&width, &height);
    if (FAILED(hr)) return -1;

    // Security: Limit texture dimensions to prevent memory exhaustion
    // Max 8192x8192 = 256MB uncompressed BGRA (reasonable for broadcast graphics)
    const UINT MAX_TEXTURE_DIMENSION = 8192;
    if (width > MAX_TEXTURE_DIMENSION || height > MAX_TEXTURE_DIMENSION || width == 0 || height == 0)
    {
        OutputDebugStringA("[DaroEngine] Security: Texture dimensions exceed limit or invalid\n");
        return -1;
    }

    // Convert to BGRA
    ComPtr<IWICFormatConverter> converter;
    hr = m_WICFactory->CreateFormatConverter(&converter);
    if (FAILED(hr)) return -1;
    
    hr = converter->Initialize(
        frame.Get(),
        GUID_WICPixelFormat32bppBGRA,
        WICBitmapDitherTypeNone,
        nullptr,
        0.0,
        WICBitmapPaletteTypeMedianCut
    );
    if (FAILED(hr)) return -1;
    
    // Read pixels
    std::vector<BYTE> pixels((size_t)width * height * 4);
    hr = converter->CopyPixels(nullptr, width * 4, (UINT)pixels.size(), pixels.data());
    if (FAILED(hr)) return -1;
    
    // Create D3D11 texture
    D3D11_TEXTURE2D_DESC texDesc = {};
    texDesc.Width = width;
    texDesc.Height = height;
    texDesc.MipLevels = 1;
    texDesc.ArraySize = 1;
    texDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    texDesc.SampleDesc.Count = 1;
    texDesc.Usage = D3D11_USAGE_DEFAULT;
    texDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    
    D3D11_SUBRESOURCE_DATA initData = {};
    initData.pSysMem = pixels.data();
    initData.SysMemPitch = width * 4;
    
    TextureInfo info;
    hr = m_Device->CreateTexture2D(&texDesc, &initData, &info.texture);
    if (FAILED(hr)) return -1;
    
    // Create SRV
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = texDesc.Format;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;
    
    hr = m_Device->CreateShaderResourceView(info.texture.Get(), &srvDesc, &info.srv);
    if (FAILED(hr)) return -1;
    
    info.width = width;
    info.height = height;
    info.path = filePath;
    
    int id = m_NextTextureId++;
    if (id <= 0) m_NextTextureId = id = 1; // Wraparound protection
    m_Textures[id] = std::move(info);
    
    return id;
}

void DaroRenderer::UnloadTexture(int textureId)
{
    m_Textures.erase(textureId);
}

ID3D11ShaderResourceView* DaroRenderer::GetTextureSRV(int textureId)
{
    auto it = m_Textures.find(textureId);
    if (it != m_Textures.end())
        return it->second.srv.Get();
    return nullptr;
}

// ============== Spout Input ==============

int DaroRenderer::GetSpoutSenderCount()
{
    return m_SpoutSender.GetSenderCount();
}

bool DaroRenderer::GetSpoutSenderName(int index, char* buffer, int bufferSize)
{
    if (!buffer || bufferSize <= 0 || index < 0) return false;

    char name[256] = {};
    if (m_SpoutSender.GetSender(index, name, 256))
    {
        strncpy_s(buffer, bufferSize, name, _TRUNCATE);
        return true;
    }
    return false;
}

int DaroRenderer::ConnectSpoutReceiver(const char* senderName)
{
    if (!m_Device || !senderName || senderName[0] == '\0') return -1;

    // Validate sender name length (Spout uses fixed 256-char buffers)
    size_t nameLen = strlen(senderName);
    if (nameLen > 255)
    {
        OutputDebugStringA("[DaroEngine] Spout receiver name too long (max 255 chars)\n");
        return -1;
    }

    SpoutReceiverInfo info;
    info.senderName = senderName;
    info.connected = false;
    info.width = 0;
    info.height = 0;
    
    info.receiver.OpenDirectX11(m_Device.Get());
    info.receiver.SetReceiverName(senderName);
    
    int id = m_NextReceiverId++;
    if (id <= 0) m_NextReceiverId = id = 1; // Wraparound protection
    m_SpoutReceivers[id] = std::move(info);
    
    return id;
}

void DaroRenderer::DisconnectSpoutReceiver(int receiverId)
{
    auto it = m_SpoutReceivers.find(receiverId);
    if (it != m_SpoutReceivers.end())
    {
        it->second.receiver.ReleaseReceiver();
        it->second.receiver.CloseDirectX11();
        m_SpoutReceivers.erase(it);
    }
}

void DaroRenderer::UpdateSpoutReceivers()
{
    for (auto& pair : m_SpoutReceivers)
    {
        auto& info = pair.second;
        
        if (info.receiver.ReceiveTexture())
        {
            // Get new texture if size changed or first connect
            if (!info.connected || info.receiver.IsUpdated())
            {
                info.width = info.receiver.GetSenderWidth();
                info.height = info.receiver.GetSenderHeight();

                info.texture.Reset();
                info.srv.Reset();
                info.connected = false;

                // Validate dimensions before creating texture
                if (info.width == 0 || info.height == 0)
                    continue;

                // Create texture to copy into
                D3D11_TEXTURE2D_DESC texDesc = {};
                texDesc.Width = info.width;
                texDesc.Height = info.height;
                texDesc.MipLevels = 1;
                texDesc.ArraySize = 1;
                texDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                texDesc.SampleDesc.Count = 1;
                texDesc.Usage = D3D11_USAGE_DEFAULT;
                texDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

                if (SUCCEEDED(m_Device->CreateTexture2D(&texDesc, nullptr, &info.texture)))
                {
                    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
                    srvDesc.Format = texDesc.Format;
                    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
                    srvDesc.Texture2D.MipLevels = 1;

                    if (FAILED(m_Device->CreateShaderResourceView(info.texture.Get(), &srvDesc, &info.srv)))
                    {
                        info.texture.Reset();
                    }
                }

                // Only mark connected if both texture and SRV were created
                info.connected = (info.texture && info.srv);
            }
            
            // Copy received texture (validate dimensions match to prevent D3D error)
            if (info.texture)
            {
                ID3D11Texture2D* srcTex = info.receiver.GetSenderTexture();
                if (srcTex)
                {
                    D3D11_TEXTURE2D_DESC srcDesc, dstDesc;
                    srcTex->GetDesc(&srcDesc);
                    info.texture->GetDesc(&dstDesc);
                    if (srcDesc.Width == dstDesc.Width && srcDesc.Height == dstDesc.Height)
                    {
                        m_Context->CopyResource(info.texture.Get(), srcTex);
                    }
                }
            }
        }
    }
}

ID3D11ShaderResourceView* DaroRenderer::GetSpoutReceiverSRV(int receiverId)
{
    auto it = m_SpoutReceivers.find(receiverId);
    if (it != m_SpoutReceivers.end() && it->second.connected)
        return it->second.srv.Get();
    return nullptr;
}

// ============================================================================
// Video Playback
// ============================================================================

int DaroRenderer::LoadVideo(const char* filePath)
{
    // Initialize video manager on first use
    VideoManager::Instance().Initialize(m_Device.Get(), m_Context.Get());
    return VideoManager::Instance().LoadVideo(filePath);
}

void DaroRenderer::UnloadVideo(int videoId)
{
    VideoManager::Instance().UnloadVideo(videoId);
}

void DaroRenderer::PlayVideo(int videoId)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        player->Play();
}

void DaroRenderer::PauseVideo(int videoId)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        player->Pause();
}

void DaroRenderer::StopVideo(int videoId)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        player->Stop();
}

void DaroRenderer::SeekVideo(int videoId, int frame)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        player->SeekToFrame(frame);
}

void DaroRenderer::SeekVideoTime(int videoId, double seconds)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        player->SeekToTime(seconds);
}

bool DaroRenderer::IsVideoPlaying(int videoId)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        return player->IsPlaying();
    return false;
}

int DaroRenderer::GetVideoFrame(int videoId)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        return player->GetCurrentFrame();
    return 0;
}

int DaroRenderer::GetVideoTotalFrames(int videoId)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        return player->GetTotalFrames();
    return 0;
}

void DaroRenderer::SetVideoLoop(int videoId, bool loop)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        player->SetLoop(loop);
}

void DaroRenderer::SetVideoAlpha(int videoId, bool alpha)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
        player->SetVideoAlpha(alpha);
}

void DaroRenderer::UpdateVideos()
{
    VideoManager::Instance().UpdateAll();
}

ID3D11ShaderResourceView* DaroRenderer::GetVideoSRV(int videoId)
{
    if (auto* player = VideoManager::Instance().GetPlayer(videoId))
    {
        auto* srv = player->GetSRV();
        if (!srv)
        {
            // Log once per second to avoid spam
            static int sLogCounter = 0;
            if ((sLogCounter++ % 50) == 0)
            {
                char dbg[256];
                sprintf_s(dbg, "[DaroVideo] GetVideoSRV(%d): player found but SRV is null (loaded=%d, frameCopied=%d)\n",
                    videoId, player->IsLoaded(), player->HasFrameData());
                OutputDebugStringA(dbg);
            }
        }
        return srv;
    }
    // Log once per second to avoid spam
    static int sLogCounter2 = 0;
    if ((sLogCounter2++ % 50) == 0)
    {
        char dbg[256];
        sprintf_s(dbg, "[DaroVideo] GetVideoSRV(%d): player NOT FOUND\n", videoId);
        OutputDebugStringA(dbg);
    }
    return nullptr;
}
