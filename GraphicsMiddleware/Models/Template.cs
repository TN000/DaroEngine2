using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphicsMiddleware.Models;

// ============================================================================
// PLAYLIST ITEM MODELS
// ============================================================================

/// <summary>
/// Stored playlist item in the database.
/// </summary>
public sealed class StoredPlaylistItem
{
    /// <summary>Unique identifier (GUID).</summary>
    public required string Id { get; init; }

    /// <summary>Display name for this item.</summary>
    public string? Name { get; set; }

    /// <summary>Template ID reference.</summary>
    public required string TemplateId { get; init; }

    /// <summary>Template name.</summary>
    public string? TemplateName { get; set; }

    /// <summary>Path to the template file (.dtemplate).</summary>
    public required string TemplateFilePath { get; init; }

    /// <summary>Path to the linked scene project (.daro).</summary>
    public required string LinkedScenePath { get; init; }

    /// <summary>Filled data: ElementId -> Value.</summary>
    public Dictionary<string, string> FilledData { get; set; } = new();

    /// <summary>Takes from the template (JSON serialized).</summary>
    public string? TakesJson { get; set; }

    /// <summary>Creation timestamp (ISO 8601).</summary>
    public required string CreatedAt { get; init; }
}

/// <summary>
/// Request model for creating a new playlist item.
/// Only TemplateId is required - other info is resolved from template.
/// </summary>
public sealed record CreatePlaylistItemRequest(
    /// <summary>Template ID to use.</summary>
    string TemplateId,
    /// <summary>Filled form data: ElementId -> Value</summary>
    Dictionary<string, string>? FilledData = null,
    /// <summary>Optional custom name for the item.</summary>
    string? Name = null
);

/// <summary>
/// Response after playlist item creation.
/// </summary>
public sealed record CreatePlaylistItemResponse(
    string Id,
    string Name,
    string TemplateId,
    string CreatedAt
);

// ============================================================================
// TEMPLATE MODELS - Compatible with DaroDesigner
// ============================================================================

/// <summary>
/// Template model loaded from .dtemplate files.
/// </summary>
public class TemplateModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? FolderPath { get; set; }
    public double CanvasWidth { get; set; }
    public double CanvasHeight { get; set; }
    public string? BackgroundColor { get; set; }
    public List<TemplateElementModel> Elements { get; set; } = new();
    public List<TemplateTakeModel> Takes { get; set; } = new();
    public string? LinkedScenePath { get; set; }
}

/// <summary>
/// Template element (form field).
/// </summary>
public class TemplateElementModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int ElementType { get; set; } // 0=Label, 1=TextBox, 2=MultilineTextBox
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? DefaultText { get; set; }
    public string? FontFamily { get; set; }
    public double FontSize { get; set; }
    public string? ForegroundColor { get; set; }
    public string? BackgroundColor { get; set; }
    /// <summary>
    /// ID of the linked transfunctioner in the scene.
    /// This is the key for mapping filled data to layer properties.
    /// </summary>
    public string? LinkedTransfunctionerId { get; set; }
    public bool IsRequired { get; set; }
    public int MaxLength { get; set; }
    public string? Placeholder { get; set; }
}

/// <summary>
/// Template take (animation sequence).
/// </summary>
public class TemplateTakeModel
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<TakeActionModel> Actions { get; set; } = new();
    public int TimelineDurationFrames { get; set; } = 250;
}

/// <summary>
/// Take action (play/stop/etc).
/// </summary>
public class TakeActionModel
{
    public string? Id { get; set; }
    public int ActionType { get; set; } // 0=Play, 1=Stop, 2=Cue, 3=Pause, 4=Continue
    public int StartFrame { get; set; }
    public int Duration { get; set; }
    public List<string> TargetAnimationNames { get; set; } = new();
}

// ============================================================================
// TEMPLATE SERVICE RESPONSE MODELS
// ============================================================================

/// <summary>
/// Summary info for template listing.
/// </summary>
public class TemplateInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required string FolderPath { get; init; }
    public int ElementCount { get; init; }
    public int TakeCount { get; init; }
    public string? LinkedScenePath { get; init; }
    public bool HasLinkedScene { get; init; }
}

/// <summary>
/// Response for template list endpoint.
/// </summary>
public class TemplateListResponse
{
    public List<TemplateInfo> Templates { get; set; } = new();
    public int TotalCount { get; set; }
}

/// <summary>
/// Response for template detail endpoint.
/// </summary>
public class TemplateDetailResponse
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public string? LinkedScenePath { get; init; }
    public bool LinkedSceneExists { get; init; }
    public List<TemplateElementInfo> Elements { get; set; } = new();
    public List<TemplateTakeInfo> Takes { get; set; } = new();

    // Canvas dimensions for absolute positioning
    public double CanvasWidth { get; init; }
    public double CanvasHeight { get; init; }
    public string? BackgroundColor { get; init; }
}

/// <summary>
/// Element info for form generation, including layout data for canvas positioning.
/// </summary>
public class TemplateElementInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int ElementType { get; init; }
    public string? DefaultText { get; init; }
    public string? Placeholder { get; init; }
    public bool IsRequired { get; init; }
    public int MaxLength { get; init; }
    public string? LinkedTransfunctionerId { get; init; }

    // Layout properties for canvas positioning (matching Template Maker)
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string? FontFamily { get; init; }
    public double FontSize { get; init; }
    public string? ForegroundColor { get; init; }
    public string? BackgroundColor { get; init; }
}

/// <summary>
/// Take info for display.
/// </summary>
public class TemplateTakeInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int DurationFrames { get; init; }
    public List<string> TargetAnimations { get; init; } = new();
}

// ============================================================================
// MOSART PROTOCOL
// ============================================================================

/// <summary>
/// Mosart command types.
/// </summary>
public enum MosartCommand
{
    Cue = 0,
    Play = 1,
    Stop = 2,
    Continue = 3,
    Pause = 4
}

/// <summary>
/// Parsed Mosart message.
/// Format: GUID|COMMAND\r\n
/// </summary>
public sealed record MosartMessage(string ItemId, MosartCommand Command)
{
    public static bool TryParse(string rawMessage, out MosartMessage? message)
    {
        message = null;

        if (string.IsNullOrWhiteSpace(rawMessage))
            return false;

        var parts = rawMessage.Trim().Split('|');
        if (parts.Length < 2)
            return false;

        var itemId = parts[0].Trim();
        if (!Guid.TryParse(itemId, out _))
            return false;

        if (!int.TryParse(parts[1].Trim(), out var commandValue))
            return false;

        if (!Enum.IsDefined(typeof(MosartCommand), commandValue))
            return false;

        message = new MosartMessage(itemId, (MosartCommand)commandValue);
        return true;
    }
}

// ============================================================================
// ENGINE STATE
// ============================================================================

/// <summary>
/// Current state for status reporting.
/// </summary>
public class PlayoutState
{
    public required string State { get; init; }
    public string? CurrentItemId { get; init; }
    public string? CurrentItemName { get; init; }
    public string? TemplateName { get; init; }
    public int CurrentTakeIndex { get; init; }
    public string? CurrentTakeName { get; init; }
    public int TotalTakes { get; init; }
    public int CurrentFrame { get; init; }
    public int TotalFrames { get; init; }
    public double Fps { get; init; }
    public bool IsPlaying { get; init; }
    public bool IsInitialized { get; init; }
    public string? LastError { get; init; }
}

// ============================================================================
// DARODESIGNER SCENE DATA MODELS
// ============================================================================

/// <summary>
/// Project data structure matching DaroDesigner.
/// </summary>
public class ProjectData
{
    public int Version { get; set; }
    public double TimelineZoom { get; set; }
    public List<AnimationData> Animations { get; set; } = new();
}

public class AnimationData
{
    public string? Name { get; set; }
    public int LengthFrames { get; set; }
    public List<LayerData> Layers { get; set; } = new();
}

public class LayerData
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int LayerType { get; set; }
    public bool IsVisible { get; set; } = true;
    public int ParentId { get; set; } = -1;

    // Transform
    public float PosX { get; set; } = 960;
    public float PosY { get; set; } = 540;
    public float SizeX { get; set; } = 400;
    public float SizeY { get; set; } = 300;
    public float RotX { get; set; }
    public float RotY { get; set; }
    public float RotZ { get; set; }
    public float AnchorX { get; set; } = 0.5f;
    public float AnchorY { get; set; } = 0.5f;
    public bool LockAspectRatio { get; set; }

    // Appearance
    public float Opacity { get; set; } = 1.0f;
    public float ColorR { get; set; } = 1.0f;
    public float ColorG { get; set; } = 1.0f;
    public float ColorB { get; set; } = 1.0f;
    public float ColorA { get; set; } = 1.0f;

    // Texture
    public int TextureSource { get; set; }
    public float TexX { get; set; }
    public float TexY { get; set; }
    public float TexW { get; set; }
    public float TexH { get; set; }
    public float TexRot { get; set; }
    public string? TexturePath { get; set; }
    public string? SpoutSenderName { get; set; }

    // Text
    public string? TextContent { get; set; }
    public string? FontFamily { get; set; }
    public float FontSize { get; set; }
    public bool FontBold { get; set; }
    public bool FontItalic { get; set; }
    public int TextAlignment { get; set; }
    public float LineHeight { get; set; }
    public float LetterSpacing { get; set; }
    public int TextAntialiasMode { get; set; }

    // Mask
    public int MaskMode { get; set; }
    public List<int> MaskedLayerIds { get; set; } = new();

    // Animation tracks
    public List<TrackData> Tracks { get; set; } = new();
    public List<StringTrackData> StringTracks { get; set; } = new();

    // Transfunctioner bindings
    public List<TransfunctionerData> Transfunctioners { get; set; } = new();
}

public class TrackData
{
    public string? PropertyId { get; set; }
    public List<KeyframeData> Keyframes { get; set; } = new();
}

public class KeyframeData
{
    public int Frame { get; set; }
    public float Value { get; set; }
    public float EaseIn { get; set; }
    public float EaseOut { get; set; }
}

public class StringTrackData
{
    public string? PropertyId { get; set; }
    public List<StringKeyframeData> Keyframes { get; set; } = new();
}

public class StringKeyframeData
{
    public int Frame { get; set; }
    public string? Value { get; set; }
}

public class TransfunctionerData
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? TemplateElementId { get; set; }
    public string? TemplateElementName { get; set; }
    public int TargetLayerId { get; set; }
    public string? TargetLayerName { get; set; }
    public string? TargetPropertyId { get; set; }
    public int BindingType { get; set; }
}

// ============================================================================
// JSON OPTIONS
// ============================================================================

/// <summary>
/// JSON serialization options for DaroDesigner compatibility.
/// </summary>
public static class DaroJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
}
