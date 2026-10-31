using Dalamud.Configuration;

namespace WrathPrivateHotbar;

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 16;
    // Dalamud game UI only. The old external renderer is not distributed.
    public bool InternalHotbarEnabled { get; set; } = true;
    // NPC dialogue and cutscene visibility are independently configurable.
    public bool HideDuringNpcDialogue { get; set; } = true;
    // Hide our pseudo-hotbar in cinematic, movie and quest/event playback.
    public bool HideDuringCutscenes { get; set; } = true;
    public bool InternalHotbarLocked { get; set; } = false;
    public int InternalHotbarCellSize { get; set; } = 56;
    // Slot spacing is adjustable independently of size.
    public int InternalHotbarCellGap { get; set; } = 2;
    // Native HUD auto-size is intentionally off by default; may be inaccurate.
    public bool InternalHotbarAutoSize { get; set; } = false;
    public float InternalHotbarX { get; set; } = 280f;
    public float InternalHotbarY { get; set; } = 700f;
    public bool OverlayClickActions { get; set; } = true;
    public bool OverlayNativeMirrorEnabled { get; set; } = true;
    public bool OverlayCooldownEnabled { get; set; } = true;
    public bool OverlayCooldownNumbers { get; set; } = true;
    // v2.0.5.4 native-inspired rendering; nothing touches native action execution.
    public bool NativeCooldownProgressEnabled { get; set; } = true;
    // The user explicitly requested bright icons in v2.0.5: optional only.
    public bool NativeCooldownSectorEnabled { get; set; } = false;
    public bool NativePressFeedbackEnabled { get; set; } = true;
    // Show the native resource cost number (MP/CP/GP) at the icon bottom-left.
    public bool NativeActionCostEnabled { get; set; } = true;
    public bool NativeProcHighlightEnabled { get; set; } = true;
    public bool OverlayHideShortRecast { get; set; } = true;
    public int[]? PulseKeyCodes { get; set; } = null;
    public bool WrathScopedGateEnabled { get; set; } = false;
    // Native hotbar separation starts after HUD readiness checks.
    public bool EnableDelayedNativeHooks { get; set; } = true;
    // Instant startup can be disabled with the safe-startup option.
    public bool FastNativeHookStartup { get; set; } = true;
    // Startup waits for consecutive HUD-ready frames. Native detours may still
    // be unsafe after game or Dalamud updates; preserve crash recovery.
    public bool InstantNativeHookStartup { get; set; } = true;
    public bool NativeTrampolineEnabled { get; set; } = true;
// Keep non-selected hotbar executions native to the game.
    public bool IsolateUnselectedExecution { get; set; } = true;
// Keep non-selected hotbar tooltips native to the game.
    public bool IsolateUnselectedTooltip { get; set; } = true;
    public bool RenderChainTraceEnabled { get; set; } = false;
    public bool ScopedNativeRenderEnabled { get; set; } = false;
    public bool NativeIconRestoreEnabled { get; set; } = false;
    public bool RestoreOverlayEnabled { get; set; } = false;
    public int RestoreHotbar { get; set; } = 2;
    public int SelectedHotbar { get; set; } = 1;
    // Logical hotbar numbers (1-10) that the user asked to keep hidden across logins.
    // This is only a visual UI-node hide. Never change FF14 HUD settings.
    public int[] HiddenOriginalHotbars { get; set; } = [];
    public bool AutoReadKeybinds { get; set; } = true;
    public string OverlayLayout { get; set; } = "12x1";
    public bool OverlayUseAdjustedIds { get; set; } = true;
    public bool OverlayEnabled { get; set; } = false;
    public bool PulseEnabled { get; set; } = true;
    // Native FF14 hotbars to favor for simulated key/click press animations.
    // Empty = legacy automatic matching; otherwise selected bars are preferred
    // in ascending numeric priority, with visible unselected bars as fallback.
    // E.g. {3,4} means HB3, then HB4, then another matching native bar.
    // This affects VISUAL pulse targets only, never skill activation or cooldowns.
    public int[] PulseTargetHotbars { get; set; } = [];

    // The shipped native UI input sound uses ID 12; no sound ID probe UI.
    // Opt-in only: FF14 native SFX gate while an unmodified assigned key is held.
    // Do not enable by default because SE call paths can change with patches.
    public bool SoundSyncNativeOnPulse { get; set; } = false;
    // Optional ReAction-free hold-to-repeat for selected standard hotbar actions.
    // Off by default to prevent unintended/duplicate action requests.
    public bool BuiltInTurboEnabled { get; set; } = false;
    public int BuiltInTurboIntervalMs { get; set; } = 100;
    public int BuiltInTurboStartDelayMs { get; set; } = 0;
    public bool BuiltInTurboOutOfCombat { get; set; } = true;
    public bool RandomPulse { get; set; } = true;
    public int PulseMinMs { get; set; } = 80;
    public int PulseMaxMs { get; set; } = 300;
    public int FixedPulseMs { get; set; } = 160;
}
