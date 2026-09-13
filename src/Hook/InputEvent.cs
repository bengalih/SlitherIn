using SlitherIn.Core.Config;
using SlitherIn.Core.Target;

namespace SlitherIn.Hook
{
    /// <summary>Low-level event kinds the hooks can produce.</summary>
    public enum InputEventKind
    {
        KeyDown, KeyUp, SysKeyDown, SysKeyUp,
        MouseDown, MouseUp, MouseWheel, MouseMove,
    }

    /// <summary>
    /// A normalized hook event: canonical chord + foreground snapshot captured at
    /// event time. Hooks only REPORT; composing this is their whole job. The App
    /// routes it (trigger/toggle/abort/kill vs key-finder logging) and ALWAYS lets
    /// it pass through to the game (AHK `~` semantics — passthrough, memory).
    /// </summary>
    public readonly struct InputEvent
    {
        public readonly InputEventKind Kind;
        public readonly Chord Chord;
        public readonly ForegroundInfo Foreground;

        public InputEvent(InputEventKind kind, Chord chord, ForegroundInfo foreground)
        {
            Kind = kind;
            Chord = chord;
            Foreground = foreground;
        }

        public bool IsMouse => Kind == InputEventKind.MouseDown || Kind == InputEventKind.MouseUp;
        public bool IsKeyDown => Kind == InputEventKind.KeyDown || Kind == InputEventKind.SysKeyDown;
        public bool IsKeyUp => Kind == InputEventKind.KeyUp || Kind == InputEventKind.SysKeyUp;
    }
}