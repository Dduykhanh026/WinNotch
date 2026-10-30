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
    MusicAndTimer
}

public enum NotchTab
{
    Overview = 0,
    System = 1,
    FileShelf = 2
}

public enum HudKind
{
    Volume,
    Brightness,
    Battery,
    TrackChange,
    FaceIdUnlock
}

public enum ShelfItemKind
{
    File,
    Folder,
    ImageClip,
    TextSnippet
}
