namespace WinNotch.Models;

public enum NotchVisualState
{
    CollapsedIdle,
    CollapsedActive,
    GhostPassthrough,
    HudPop,
    DragReady,
    Expanded,
    Hidden
}

public enum CollapsedContentKind
{
    Idle,
    TimerOnly,
    MusicOnly,
    MusicAndTimer,
    AgentOnly
}

public enum NotchTab
{
    Agents = 0,
    Overview = 1,
    System = 2,
    FileShelf = 3
}

public enum HudKind
{
    Volume,
    Brightness,
    Battery,
    TrackChange,
    FaceIdUnlock,
    Notification,
    AudioDevice,
    Microphone,
    Caffeine,
    CapsLock,
    ColorPicked,
    MemoryOptimized,
    AgentAlert
}

public enum HudShapeKind
{
    CompactPill,
    FaceIdSquare,
    NotificationBanner
}

public enum ShelfItemKind
{
    File,
    Folder,
    ImageClip,
    TextSnippet
}
