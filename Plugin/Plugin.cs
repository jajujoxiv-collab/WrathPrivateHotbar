using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Collections.Generic;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using Dalamud.Utility;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.JobGauge.Types;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Enums;

namespace WrathPrivateHotbar;

/// <summary>
/// In-process configurable hotbar companion for WrathCombo.
/// Preserves native game hotbar handling outside the selected bar, with a
/// clickable Dalamud UI and readiness-gated startup hooks.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly PluginConfig config;
    private readonly IFramework framework;
    private readonly ITextureProvider textureProvider;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPlayerState playerState;
    private readonly IJobGauges jobGauges;
    private readonly IObjectTable objects;
    private bool grandImpactReady;
    private string grandImpactStatus = "未確認";
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly IGameInteropProvider gameInterop;

    // alpha51: PURE ACTION EXECUTION ISOLATION PROBE (opt-in, OFF by default).
    // Identify the native hotbar execution call first. Our GetAdjustedActionId
    // detour will only bypass WrathCombo while executing an unselected hotbar's
    // ORIGINAL Action slot. No hotbar contents or game input are rewritten.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ExecuteHotbarSlotByIdDelegate(RaptureHotbarModule* module, uint hotbarId, uint slotId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ExecuteHotbarSlotDelegate(RaptureHotbarModule* module, RaptureHotbarModule.HotbarSlot* slot);
    private Hook<ExecuteHotbarSlotByIdDelegate>? executeByIdHook;
    private Hook<ExecuteHotbarSlotDelegate>? executeSlotHook;
    [ThreadStatic] private static int executingPureBar;
    [ThreadStatic] private static uint executingPureStoredId;
    [ThreadStatic] private static int executingPureDepth;
    private bool executeIsolationHooksActive;
    private string executeIsolationStatus = "未設定（OFF）";
    private long executeIsolationCalls;
    private long executeIsolationScopedAdjustments;
    private long executeIsolationScopeMisses;
    private long executeIsolationFailures;
    private uint executeIsolationLastNative;

    private byte OnExecuteHotbarSlotById(RaptureHotbarModule* module, uint hotbarId, uint slotId)
    {
        if (Volatile.Read(ref nativeObservationArmed) == 0 ||
            !config.IsolateUnselectedExecution || module == null ||
            hotbarId >= 10 || slotId >= 12 || hotbarId == (uint)(config.SelectedHotbar - 1))
            return executeByIdHook!.Original(module, hotbarId, slotId);
        var slot = module->GetSlotById(hotbarId, slotId);
        if (slot == null || slot->CommandType != RaptureHotbarModule.HotbarSlotType.Action || slot->CommandId == 0)
            return executeByIdHook!.Original(module, hotbarId, slotId);
        Interlocked.Increment(ref executeIsolationCalls);
        var oldBar = executingPureBar;
        var oldId = executingPureStoredId;
        executingPureBar = (int)hotbarId;
        executingPureStoredId = slot->CommandId;
        executingPureDepth++;
        try { return executeByIdHook!.Original(module, hotbarId, slotId); }
        finally
        {
            executingPureDepth--;
            executingPureBar = oldBar;
            executingPureStoredId = oldId;
        }
    }

    private byte OnExecuteHotbarSlot(RaptureHotbarModule* module, RaptureHotbarModule.HotbarSlot* slot)
    {
        if (Volatile.Read(ref nativeObservationArmed) == 0 ||
            !config.IsolateUnselectedExecution || module == null || slot == null ||
            slot->CommandType != RaptureHotbarModule.HotbarSlotType.Action || slot->CommandId == 0)
            return executeSlotHook!.Original(module, slot);
        var match = -1;
        // A slot pointer, not an action ID, differentiates HB1 from HB2.
        for (var hb = 0; hb < 10 && match < 0; hb++)
        {
            if (hb == config.SelectedHotbar - 1) continue;
            for (var si = 0; si < 12; si++)
                if (module->GetSlotById((uint)hb, (uint)si) == slot)
                {
                    match = hb;
                    break;
                }
        }
        if (match < 0) return executeSlotHook!.Original(module, slot);
        Interlocked.Increment(ref executeIsolationCalls);
        var oldBar = executingPureBar;
        var oldId = executingPureStoredId;
        executingPureBar = match;
        executingPureStoredId = slot->CommandId;
        executingPureDepth++;
        try { return executeSlotHook!.Original(module, slot); }
        finally
        {
            executingPureDepth--;
            executingPureBar = oldBar;
            executingPureStoredId = oldId;
        }
    }

    private void UpdateExecuteIsolationHooks()
    {
        bool shouldEnable = config.IsolateUnselectedExecution &&
                            Volatile.Read(ref nativeObservationArmed) != 0 &&
                            adjustedActionHook != null && nativeRenderHooksActive &&
                            wrathNativeOriginal != null;
        if (!shouldEnable)
        {
            if (executeIsolationHooksActive)
            {
                executeIsolationHooksActive = false;
                try { executeSlotHook?.Disable(); } catch { }
                try { executeByIdHook?.Disable(); } catch { }
            }
            executeIsolationStatus = config.IsolateUnselectedExecution
                ? "待機中: フック準備・WrathCombo標準処理の取得が必要"
                : "OFF（従来の実行処理）";
            return;
        }
        if (executeIsolationHooksActive) return;
        try
        {
            executeByIdHook ??= gameInterop.HookFromAddress<ExecuteHotbarSlotByIdDelegate>(
                (nint)RaptureHotbarModule.MemberFunctionPointers.ExecuteSlotById,
                OnExecuteHotbarSlotById);
            executeSlotHook ??= gameInterop.HookFromAddress<ExecuteHotbarSlotDelegate>(
                (nint)RaptureHotbarModule.MemberFunctionPointers.ExecuteSlot,
                OnExecuteHotbarSlot);
            executeByIdHook.Enable();
            executeSlotHook.Enable();
            executeIsolationHooksActive = true;
            executeIsolationStatus = "実行フックON（対象外バーのActionスロットに限定）";
        }
        catch (Exception ex)
        {
            executeIsolationStatus = $"利用不可: {ex.GetType().Name} ({ex.Message})";
            Interlocked.Increment(ref executeIsolationFailures);
            try { executeByIdHook?.Disable(); } catch { }
            try { executeSlotHook?.Disable(); } catch { }
        }
    }

    // alpha52: Experimental hover-only isolation. Avoid touching the stored
    // hotbar command, global WrathCombo settings or normal action execution.
    // HandleActionHover is called by the game when the tooltip is requested.
    // The ONLY scoped change is to use the FF14 adjustment trampoline while
    // the game's own tooltip-building call is active for an unselected slot.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HandleActionHoverDelegate(AgentActionDetail* agent,
        DetailKind kind, uint actionId, int flag, [MarshalAs(UnmanagedType.U1)] bool isLovm,
        int a5, int a6);
    private Hook<HandleActionHoverDelegate>? actionHoverHook;
    private bool tooltipIsolationHookActive;
    private string tooltipIsolationStatus = "OFF（純正ツールチップ分離なし）";
    private long tooltipHoverCalls, tooltipHoverScoped, tooltipHoverNotFound, tooltipHoverFailures;
    private long tooltipNativeBypasses;
    private string tooltipHoverLast = "未検証";

    private bool TryFindWrathHoveredPureSlot(out int bar, out uint storedId)
    {
        bar = -1;
        storedId = 0;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "WrathCombo") continue;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var pluginType = asm.GetType("WrathCombo.WrathCombo");
                var instance = pluginType?.GetField("P", flags)?.GetValue(null);
                var setup = instance?.GetType().GetField("CustomActions", flags)?.GetValue(instance)
                    ?? instance?.GetType().GetProperty("CustomActions", flags)?.GetValue(instance);
                var hover = setup?.GetType().GetField("HoveredSlot", flags)?.GetValue(setup);
                if (hover is not ValueTuple<int, int> pair || pair.Item1 < 0 || pair.Item1 >= 10 ||
                    pair.Item2 < 0 || pair.Item2 >= 12 || pair.Item1 == config.SelectedHotbar - 1)
                    return false;
                var module = RaptureHotbarModule.Instance();
                if (module == null || !module->ModuleReady) return false;
                var slot = module->GetSlotById((uint)pair.Item1, (uint)pair.Item2);
                if (slot == null || slot->CommandType != RaptureHotbarModule.HotbarSlotType.Action ||
                    slot->CommandId == 0) return false;
                bar = pair.Item1;
                storedId = slot->CommandId;
                return true;
            }
        }
        catch { /* Hover diagnostics must never interrupt native input. */ }
        return false;
    }

    private void OnHandleActionHover(AgentActionDetail* agent, DetailKind kind,
        uint actionId, int flag, bool isLovm, int a5, int a6)
    {
        Interlocked.Increment(ref tooltipHoverCalls);
        if (!config.IsolateUnselectedTooltip || Volatile.Read(ref nativeObservationArmed) == 0 ||
            !clientState.IsLoggedIn || kind != DetailKind.Action || agent == null ||
            !TryFindWrathHoveredPureSlot(out var bar, out var stored))
        {
            actionHoverHook!.Original(agent, kind, actionId, flag, isLovm, a5, a6);
            return;
        }
        // The native hover argument might already be a WrathCombo result. The
        // game must receive the hotbar's stored base ID here; FF14's own combo
        // adjustment remains available via the regular underlying trampoline.
        var oldBar = executingPureBar;
        var oldId = executingPureStoredId;
        executingPureBar = bar;
        executingPureStoredId = stored;
        executingPureDepth++;
        Interlocked.Increment(ref tooltipHoverScoped);
        tooltipHoverLast = $"HB{bar + 1}, Stored={stored}, Incoming={actionId}";
        try
        {
            actionHoverHook!.Original(agent, kind, stored, flag, isLovm, a5, a6);
        }
        finally
        {
            executingPureDepth--;
            executingPureBar = oldBar;
            executingPureStoredId = oldId;
        }
    }

    private void UpdateTooltipIsolationHook()
    {
        bool ready = config.IsolateUnselectedTooltip &&
                     Volatile.Read(ref nativeObservationArmed) != 0 &&
                     clientState.IsLoggedIn && adjustedActionHook != null &&
                     nativeRenderHooksActive && wrathNativeOriginal != null;
        if (!ready)
        {
            if (tooltipIsolationHookActive)
            {
                tooltipIsolationHookActive = false;
                try { actionHoverHook?.Disable(); } catch { }
            }
            tooltipIsolationStatus = config.IsolateUnselectedTooltip
                ? "待機中（純正表示フック・HUD準備・Wrath標準経路が必要）"
                : "OFF（従来のツールチップ）";
            return;
        }
        if (tooltipIsolationHookActive) return;
        try
        {
            actionHoverHook ??= gameInterop.HookFromAddress<HandleActionHoverDelegate>(
                (nint)AgentActionDetail.MemberFunctionPointers.HandleActionHover,
                OnHandleActionHover);
            actionHoverHook.Enable();
            tooltipIsolationHookActive = true;
            tooltipIsolationStatus = "ON（選択外の純正Action説明だけを分離）";
        }
        catch (Exception ex)
        {
            tooltipIsolationStatus = $"利用不可: {ex.GetType().Name}: {ex.Message}";
            Interlocked.Increment(ref tooltipHoverFailures);
            try { actionHoverHook?.Disable(); } catch { }
        }
    }

    // Read-only alpha51 hover diagnostic via the loaded WrathCombo instance.
    // No game UI or tooltip fields are changed here.
    private string hoverSlotProbeStatus = "WrathComboのホバー枠: 未取得";
    private long lastHoverSlotProbeTick;
    private void RefreshHoverSlotProbe()
    {
        var now = Environment.TickCount64;
        if (now - lastHoverSlotProbeTick < 250) return;
        lastHoverSlotProbeTick = now;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "WrathCombo") continue;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var pluginType = assembly.GetType("WrathCombo.WrathCombo");
                var instance = pluginType?.GetField("P", flags)?.GetValue(null);
                var customActions = instance?.GetType().GetField("CustomActions", flags)?.GetValue(instance)
                                   ?? instance?.GetType().GetProperty("CustomActions", flags)?.GetValue(instance);
                var hovered = customActions?.GetType().GetField("HoveredSlot", flags)?.GetValue(customActions);
                if (hovered is ValueTuple<int, int> pair)
                {
                    hoverSlotProbeStatus = $"WrathComboホバー: HB{pair.Item1 + 1} スロット{pair.Item2 + 1}";
                    return;
                }
                hoverSlotProbeStatus = "WrathComboホバー: 無し / メンバー未検出";
                return;
            }
            hoverSlotProbeStatus = "WrathComboは未ロード";
        }
        catch (Exception ex) { hoverSlotProbeStatus = $"ホバー情報取得失敗: {ex.GetType().Name}"; }
    }

    // Render-only interception. Never changes CommandId or calls UseAction.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PrepareSlotDelegate(RaptureHotbarModule* module,
        RaptureHotbarModule.HotbarSlot* slot,
        RaptureHotbarModule.HotbarUIIntermediate* intermediate);
    private Hook<PrepareSlotDelegate>? iconRenderHook;
    // EXPERIMENT: limit adjusted-action suppression to a native hotbar render
    // call on the current thread. No WrathCombo file or global config changes.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetAdjustedActionDelegate(ActionManager* self, uint actionId);
    private Hook<GetAdjustedActionDelegate>? adjustedActionHook;
    [ThreadStatic] private static int nativeRenderScopeDepth;
    private long scopedNativeCalls;
    private long nativeMeleeAdjustments;
    private long nativeJoltAdjustments;
    private long scopedRenderCalls;
    // Alpha21: observe, do not modify, the adjusted-ID chain in the
    // specific hotbar slot render. Counters are diagnostics, not proof of
    // which plugin owns a given hook.
    [ThreadStatic] private static int tracedBar;
    [ThreadStatic] private static int tracedSlot;
    [ThreadStatic] private static int tracedDepth;
    private readonly uint[,] chainInput = new uint[10,12];
    private readonly uint[,] chainOutput = new uint[10,12];
    private readonly long[,] chainCalls = new long[10,12];
    private string scopedRenderStatus = "未初期化";
    // alpha25: bypass only WrathCombo's action-replacement detour, not the
    // native FFXIV adjustment. The official plugin's OriginalHook calls
    // its Hook<GetActionDelegate>.Original trampoline.
    private Func<uint, uint>? wrathNativeOriginal;
    private string wrathOriginalStatus = "未検出";
    private long wrathOriginalLastDiscoveryTick;
    private long nativeTrampolineCalls;
    private long nativeTrampolineDifferences;
    private long nativeTrampolineFailures;
    private long nativeTrampolineMisses;
    private uint lastTrampolineInput;
    private uint lastTrampolineWrath;
    private uint lastTrampolineNative;

    private void DiscoverWrathNativeOriginal()
    {
        if (wrathNativeOriginal != null) return;
        if (Environment.TickCount64 - wrathOriginalLastDiscoveryTick < 3000) return;
        wrathOriginalLastDiscoveryTick = Environment.TickCount64;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "WrathCombo") continue;
                var service = asm.GetType("WrathCombo.Services.Service");
                var replacerType = asm.GetType("WrathCombo.Core.ActionReplacer");
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var instance = service?.GetProperty("ActionReplacer", flags)?.GetValue(null)
                    ?? service?.GetField("ActionReplacer", flags)?.GetValue(null);
                var method = replacerType?.GetMethod("OriginalHook",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, [typeof(uint)], null);
                if (instance == null || method == null || method.ReturnType != typeof(uint))
                {
                    wrathOriginalStatus = "本家OriginalHook/Service.ActionReplacerが見つからない";
                    return;
                }
                // Bind once; avoid reflection invocation inside render hot path.
                wrathNativeOriginal = (Func<uint,uint>)method.CreateDelegate(typeof(Func<uint,uint>), instance);
                wrathOriginalStatus = "WrathCombo.OriginalHook取得OK（FF14標準トランポリン）";
                return;
            }
            wrathOriginalStatus = "WrathComboアセンブリ未ロード";
        }
        catch (Exception ex)
        {
            wrathOriginalStatus = $"取得失敗: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // Alpha22: opt-in scoped override of WrathCombo's own "ActionChanging"
    // hotbar-walk option. Unlike alpha19 this never returns a raw action ID,
    // so the native FFXIV action-adjustment chain remains intact.
    private object? wrathConfiguration;
    private FieldInfo? wrathActionChangingField;
    private string wrathGateStatus = "未検出";
    private long wrathGateApplied;
    private long wrathGateSkipped;
    private long wrathGateErrors;
    private long lastWrathDiscoveryTick;
    private readonly object wrathGateSync = new();

    private void DiscoverWrathActionChanging()
    {
        if (wrathActionChangingField != null && wrathConfiguration != null) return;
        if (Environment.TickCount64 - lastWrathDiscoveryTick < 3000) return;
        lastWrathDiscoveryTick = Environment.TickCount64;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name is not "WrathCombo") continue;
                var configType = assembly.GetType("WrathCombo.Core.Configuration");
                var actionChanging = configType?.GetField("ActionChanging",
                    BindingFlags.Instance | BindingFlags.Public);
                if (actionChanging?.FieldType != typeof(bool)) continue;

                // The official plugin references Service.Configuration. Resolve
                // that exact singleton by reflection, without linking its DLL.
                foreach (var type in assembly.GetTypes())
                {
                    if (type.Name != "Service") continue;
                    var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    var property = type.GetProperty("Configuration", flags);
                    var field = type.GetField("Configuration", flags);
                    object? instance = property?.GetValue(null) ?? field?.GetValue(null);
                    if (instance == null || !configType!.IsInstanceOfType(instance)) continue;
                    wrathConfiguration = instance;
                    wrathActionChangingField = actionChanging;
                    wrathGateStatus = "WrathCombo.ActionChangingを検出";
                    return;
                }
            }
            wrathGateStatus = "WrathComboの設定参照が未検出（抑制は未実施）";
        }
        catch (Exception ex)
        {
            wrathGateStatus = $"検出失敗: {ex.GetType().Name}";
        }
    }


    private (nint Pointer, uint Icon)[] restoreSnapshot = Array.Empty<(nint, uint)>();
    private long renderCalls;
    // Read-only post-render observations. No ActionId, IconId or UI state is modified.
    private readonly uint[,] observedRenderAction = new uint[10,12];
    private readonly uint[,] observedRenderIcon = new uint[10,12];
    private readonly long[,] observedRenderCount = new long[10,12];
    private long renderMatches;
    private long nativeCorrections;
    private long nativeSkipped;
    private long renderWrites;
    private long renderSkippedMismatch;
    private long lastProcUpdateTick;
    private string nativeRestoreStatus = "未初期化";
    // alpha39: a separate, session-random IPC channel for deliberate mouse clicks.
    // Requests are read on the Dalamud framework thread only. Never invoke
    // game functions from the external WinForms process.
    private const int ClickMagic = 0x314B4C43; // CLK1
    private readonly string clickMapName = @"Local\WPHClick_" + Guid.NewGuid().ToString("N");
    private readonly MemoryMappedFile clickMap;
    private readonly MemoryMappedViewAccessor clickView;
    private int lastHandledClickSequence;
    private long lastAcceptedClickTick;
    private string clickStatus = "待機中（初期設定OFF）";
    private string clickPulseStatus = "クリック発光: 未実行";
    private int clickPulseEvents;
    private int clickPulseTargets;

    private void ProcessOverlayClick(bool hudReady)
    {
        // Always refresh the permission and selection for the overlay UI.
        clickView.Write(32, config.OverlayClickActions ? 1 : 0);
        clickView.Write(36, config.SelectedHotbar);
        var seq = clickView.ReadInt32(4);
        if (seq <= 0 || seq == lastHandledClickSequence) return;
        var slotIndex = clickView.ReadInt32(8);
        var requestedBar = clickView.ReadInt32(12);
        var utcTicks = clickView.ReadInt64(16);
        if (seq != clickView.ReadInt32(4)) return; // writer not finished
        lastHandledClickSequence = seq; // consume even invalid/expired requests
        int result = -1;
        try
        {
            var age = DateTime.UtcNow.Ticks - utcTicks;
            var now = Environment.TickCount64;
            if (!hudReady || !(config.OverlayEnabled || config.InternalHotbarEnabled) || !config.OverlayClickActions ||
                !clientState.IsLoggedIn || !IsGameForeground() ||
                Volatile.Read(ref hideForScreenEvent) != 0 ||
                requestedBar != config.SelectedHotbar ||
                slotIndex < 0 || slotIndex >= 12 ||
                age < 0 || age > TimeSpan.FromSeconds(1).Ticks ||
                now - lastAcceptedClickTick < 150)
            {
                clickStatus = "操作無効または古いクリックを破棄";
            }
            else
            {
                var module = RaptureHotbarModule.Instance();
                if (module != null && module->ModuleReady)
                {
                    var nativeSlot = module->GetSlotById((uint)(requestedBar - 1), (uint)slotIndex);
                    if (nativeSlot != null && nativeSlot->CommandId != 0)
                    {
                        lastAcceptedClickTick = now;
                        // Snapshot the native slot BEFORE dispatch. Combo state can change
                        // immediately when ExecuteSlotById processes an action; resolving
                        // after dispatch may light the *next* skill instead of the clicked one.
                        var clickSlotType = nativeSlot->CommandType;
                        uint clickStored = nativeSlot->CommandId;
                        uint clickRequested = 0;
                        if (config.PulseEnabled)
                        {
                            try { clickRequested = ResolveRequestedPulseAction(nativeSlot, slotIndex); }
                            catch (Exception resolveEx)
                            {
                                // A display-only failure must never block a manual click.
                                log.Debug($"Click pulse resolve: {resolveEx.GetType().Name}");
                            }
                        }

                        // Preserve FF14's native slot execution; do not synthesize keypresses
                        // or call use-action twice. One mouse click = one native request.
                        byte nativeResult = module->ExecuteSlotById((uint)(requestedBar - 1), (uint)slotIndex);
                        result = nativeResult;
                        clickStatus = $"バー{requestedBar} スロット{slotIndex + 1}: 実行要求 / 戻り値 {nativeResult}";

                        // alpha40: A mouse click is also an explicit pulse source.
                        // The key-held auto pulse remains independent, and won't be
                        // reset or replaced by the mouse. Pulse only the matching native
                        // action/item on *other* bars using alpha26's trusted matcher.
                        if (config.PulseEnabled)
                        {
                            try
                            {
                                int count = PulseMatchingOtherHotbars(clickSlotType, clickStored, clickRequested);
                                pulseCount += count;
                                clickPulseEvents++;
                                clickPulseTargets += count;
                                clickPulseStatus = $"クリック発光 HB{requestedBar}-{slotIndex + 1}: {count} 枠 / " +
                                                   $"登録ID {clickStored} / 推奨ID {clickRequested} / " +
                                                   $"合計 {clickPulseTargets} 枠（{clickPulseEvents} 回）";
                            }
                            catch (Exception pulseEx)
                            {
                                clickPulseStatus = $"クリック発光失敗: {pulseEx.GetType().Name}";
                                log.Debug($"Click pulse dispatch: {pulseEx.Message}");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            clickStatus = $"クリック操作失敗: {ex.GetType().Name}";
        }
        clickView.Write(28, result);
        clickView.Write(24, seq); // acknowledge once processed
    }

    private const string OverlayMapName = @"Local\WrathPrivateHotbarV03";
    private const int OverlayMapBytes = 2048;
    // PUL2 at 1760. NHM1 occupies [1152..1759], and the file map has 2048 bytes.
    private const int PulseDisplayBase = 1760;
    private const int PulseDisplayMagic = 0x324C5550; // 'PUL2'
    private long lastPulseVisualTick;
    private int pulseVisualCounter;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    private static bool IsGameForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    // Visual only: no input injection. The overlay animates the physically-held
    // slot and latest real pulse. Outside FFXIV the source always becomes -1.
    private void PublishOverlayKeyFeedback()
    {
        int source = -1;
        if (clientState.IsLoggedIn && Volatile.Read(ref earlyHotbarReady) != 0 &&
            config.PulseEnabled && IsGameForeground() &&
            activePulseSource >= 0 && activePulseSource < 12 &&
            previousKeyDown[activePulseSource] && pulseKeyEligible[activePulseSource])
            source = activePulseSource;
        overlayView.Write(PulseDisplayBase + 4, source);
        overlayView.Write(PulseDisplayBase + 8, source >= 0 ? lastPulseVisualTick : 0L);
        overlayView.Write(PulseDisplayBase + 16, source >= 0 ? pulseVisualCounter : 0);
        overlayView.Write(PulseDisplayBase + 20, source >= 0 ? 1 : 0);
        overlayView.Write(PulseDisplayBase, PulseDisplayMagic); // signature last
    }

    private const int NativeMirrorMagic = 0x314D484E; // NHM1
    private const int NativeMirrorHeader = 1152;
    private const int NativeMirrorSlotBase = 1184;
    private const int NativeMirrorSlotBytes = 48;
    private const long NativeMirrorStaleMs = 1500;

    // The game's render pipeline computes these for a HotbarUIIntermediate.
    // Each slot is indexed by the *logical* hotbar ID (0..9), never by addon name.
    // Store managed values only; never keep an unmanaged UI pointer across frames.
    private struct NativeMirrorSample
    {
        public long Tick;
        public uint StoredCommandId;
        public uint ActionId;
        public uint IconId;
        public uint CooldownMode;
        public uint CooldownPercent;
        public uint CooldownSeconds;
        public uint ChargePercent;
        public uint CurrentCharges;
        public uint CostValue;
        public uint Flags;
    }
    private readonly NativeMirrorSample[,] nativeMirrorSlots = new NativeMirrorSample[10, 12];
    private long nativeMirrorFrames;
    private int nativeMirrorFreshSlots;
    private string nativeMirrorStatus = "FF14描画情報を待機中";

    // alpha42b: Keep native detours entirely UNINSTALLED during character login.
    // Earlier alphas installed both hooks in the constructor and only gated their
    // callback bodies.  That still redirected native calls during world loading.
    // We now wait for a stable logged-in player, then a stable ModuleReady HUD,
    // and ONLY THEN attach the detours.  On logout/unstable HUD they are disarmed.
    // alpha44: A short readiness-gated path for regular hotbar snapshots and
    // the in-process ImGui window. NEVER install hooks on this path.
    // The proven alpha42c 10s + 4s hook delay is intentionally unchanged.
    private const long EarlyHotbarReadDelayMs = 2000;
    private const long LoginSafetyDelayMs = 10000;
    // Optional fast test. Never shorten the proven safe path by default.
    // Enter native module only after ordinary in-process UI is already ready.
    private const long FastLoginSafetyDelayMs = 3000;
    private const long FastNativeObservationWarmupMs = 1500;
    // For alpha50 instant mode, do not use a timer: verify ModuleReady on
    // consecutive Framework.Update frames. This usually completes in <100ms
    // after world/HUD readiness, but cannot guarantee zero delay or no crash.
    private const int InstantHudReadyFramesRequired = 3;
    private int instantHudReadyFrames;
    private nint lastInstantHudModulePointer;
    private const long NativeObservationWarmupMs = 4000;
    private int earlyHotbarReady;
    private string earlyHotbarStatus = "ログイン待機中";
    private int nativeObservationArmed;
    private long loginSafetySince;
    private long nativeObservationReadySince;
    private long lastHookInstallAttemptTick;
    private bool nativeRenderHooksActive;
    private string nativeObservationStatus = "キャラクターのログイン待機中";
    private string startupPhase = "";
    private string fastHookRecoveryStatus = "";
    private long fastHookProbeStartTick;
    private bool fastHookProbeMarkerActive;

    private static string FastHookMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WrathPrivateHotbar", "alpha50_instant_native_hook_pending.marker");

    // A marker left by a silent game exit disables the *experimental* early
    // hook path automatically next startup. Regular 14s safe path remains.
    private void CheckPreviousFastHookProbe()
    {
        try
        {
            if (!File.Exists(FastHookMarkerPath)) return;
            config.FastNativeHookStartup = false;
            config.InstantNativeHookStartup = false;
            // A prior FF14 exit could have occurred inside the native hook.
            // Preserve the settings, but force manual opt-in after restart.
            pluginInterface.SavePluginConfig(config);
            File.Delete(FastHookMarkerPath);
            fastHookRecoveryStatus = "前回の高速/即時フック検証が正常終了していないため、早期適用を自動OFFにしました。";
        }
        catch (Exception ex)
        {
            log.Warning($"Fast hook marker check failed: {ex.Message}");
        }
    }

    private void BeginFastHookProbe()
    {
        if (!config.FastNativeHookStartup || fastHookProbeMarkerActive) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FastHookMarkerPath)!);
            File.WriteAllText(FastHookMarkerPath, DateTime.Now.ToString("O"));
            fastHookProbeMarkerActive = true;
            fastHookProbeStartTick = Environment.TickCount64;
            RecordStartupPhase(config.InstantNativeHookStartup ? "alpha50 instant hook probe marker written before attach" : "alpha50 fast hook probe marker written before attach");
        }
        catch (Exception ex) { log.Warning($"Fast hook marker write failed: {ex.Message}"); }
    }

    private void EndFastHookProbe()
    {
        if (!fastHookProbeMarkerActive) return;
        fastHookProbeMarkerActive = false;
        fastHookProbeStartTick = 0;
        try { File.Delete(FastHookMarkerPath); } catch { }
    }


    // Crash-safe state trace: one short line per transition, not every frame.
    // If the game dies without a Dalamud exception the last stage still exists.
    private void RecordStartupPhase(string phase)
    {
        if (startupPhase == phase) return;
        startupPhase = phase;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WrathPrivateHotbar");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "startup_alpha50.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {phase}{Environment.NewLine}");
        }
        catch { /* diagnostics must never affect game startup */ }
    }

    private void SuspendNativeRenderHooks()
    {
        if (executeIsolationHooksActive)
        {
            executeIsolationHooksActive = false;
            try { executeByIdHook?.Disable(); } catch { }
            try { executeSlotHook?.Disable(); } catch { }
        }
        var wasArmed = Volatile.Read(ref nativeObservationArmed) != 0;
        Volatile.Write(ref nativeObservationArmed, 0);
        if (wasArmed)
        {
            Array.Clear(nativeMirrorSlots);
            nativeMirrorFreshSlots = 0;
        }
        if (!nativeRenderHooksActive) return;
        nativeRenderHooksActive = false;
        try { adjustedActionHook?.Disable(); } catch (Exception ex) { log.Warning($"Native adjusted detour suspend: {ex.Message}"); }
        try { iconRenderHook?.Disable(); } catch (Exception ex) { log.Warning($"Native render detour suspend: {ex.Message}"); }
        RecordStartupPhase("native render hooks suspended");
    }

    private void EnableNativeRenderHooksAfterLogin()
    {
        if (nativeRenderHooksActive) return;
        var now = Environment.TickCount64;
        if (now - lastHookInstallAttemptTick < 5000) return;
        lastHookInstallAttemptTick = now;
        // Do not allow the callbacks to read native HUD data while installing.
        Volatile.Write(ref nativeObservationArmed, 0);
        RecordStartupPhase("attaching native render hooks after stable login");
        try
        {
            iconRenderHook ??= gameInterop.HookFromAddress<PrepareSlotDelegate>(
                (nint)RaptureHotbarModule.MemberFunctionPointers.PrepareSlotForRender,
                OnPrepareSlotForRender);
            iconRenderHook.Enable();
            nativeRenderHooksActive = true;
            nativeRestoreStatus = "純正描画フック有効（ログイン安定後）";
        }
        catch (Exception ex)
        {
            nativeRestoreStatus = $"純正描画フック利用不可: {ex.GetType().Name}";
            log.Warning($"Delayed native render hook unavailable: {ex.Message}");
            RecordStartupPhase("native render hook installation failed");
            return;
        }
        try
        {
            adjustedActionHook ??= gameInterop.HookFromAddress<GetAdjustedActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.GetAdjustedActionId,
                OnGetAdjustedActionId);
            adjustedActionHook.Enable();
            scopedRenderStatus = "スコープ付きActionIdフック有効（ログイン安定後）";
        }
        catch (Exception ex)
        {
            scopedRenderStatus = $"ActionIdフック利用不可: {ex.GetType().Name}";
            log.Warning($"Delayed native adjusted hook unavailable: {ex.Message}");
        }
        RecordStartupPhase("native hooks attached after safe delay");
    }

    // Independent fast path: render and click once the ordinary RaptureHotbar
    // module reports ModuleReady, without touching PrepareSlotForRender or
    // GetAdjustedActionId hook installation. Runs from Framework.Update only.
    // Delays the *first* module pointer access 2s after player load to avoid
    // touching the login transition itself. This is experimental and separate
    // from the longer native-detour safety window.
    private void UpdateEarlyHotbarReadiness()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            if (Interlocked.Exchange(ref earlyHotbarReady, 0) != 0)
                RecordStartupPhase("early UI disabled: logout/player unloaded");
            earlyHotbarStatus = "キャラクター読込待機中";
            return;
        }

        // In alpha50's opt-in instant mode, do not impose the alpha44 2s
        // delay on the first HUD readiness probe. We still demand both the
        // Dalamud world/player state and actual native ModuleReady state.
        var instant = config.EnableDelayedNativeHooks && config.FastNativeHookStartup && config.InstantNativeHookStartup;
        if (instant && objects.LocalPlayer == null)
        {
            Volatile.Write(ref earlyHotbarReady, 0);
            earlyHotbarStatus = "即時適用: プレイヤーの生成待ち";
            return;
        }
        var elapsed = Environment.TickCount64 - loginSafetySince;
        var readDelay = instant ? 0 : EarlyHotbarReadDelayMs;
        if (loginSafetySince == 0 || elapsed < readDelay)
        {
            Volatile.Write(ref earlyHotbarReady, 0);
            earlyHotbarStatus = $"高速表示準備中 ({Math.Max(0, (readDelay - Math.Max(0, elapsed) + 999) / 1000)}秒)";
            return;
        }
        try
        {
            var module = RaptureHotbarModule.Instance();
            if (module == null || !module->ModuleReady)
            {
                Volatile.Write(ref earlyHotbarReady, 0);
                earlyHotbarStatus = "FF14ホットバー初期化待機中 (フックは停止中)";
                return;
            }
            if (Interlocked.Exchange(ref earlyHotbarReady, 1) == 0)
                RecordStartupPhase("early hotbar ModuleReady; ImGui/slot snapshots enabled; native hooks still off");
            earlyHotbarStatus = Volatile.Read(ref nativeObservationArmed) == 0
                ? "ホットバー表示・スロット取得OK (描画フックは安全待機中)"
                : "表示・ネイティブ描画取得ともに準備完了";
        }
        catch (Exception ex)
        {
            Volatile.Write(ref earlyHotbarReady, 0);
            earlyHotbarStatus = $"高速表示の準備待ち ({ex.GetType().Name})";
        }
    }

    private void UpdateNativeObservationReadiness()
    {
        // Checking player and login state here must precede ALL native module reads.
        // Never query RaptureHotbarModule.Instance() immediately after IsLoggedIn
        // first becomes true: that is precisely the vulnerable login transition.
        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            EndFastHookProbe(); // clean logout is not a crash
            SuspendNativeRenderHooks();
            loginSafetySince = 0;
            nativeObservationReadySince = 0;
            instantHudReadyFrames = 0;
            lastInstantHudModulePointer = 0;
            nativeObservationStatus = "ログイン・キャラクター初期化待機中（フック停止）";
            RecordStartupPhase("character not ready; hooks off");
            return;
        }
        var now = Environment.TickCount64;
        if (loginSafetySince == 0)
        {
            loginSafetySince = now;
            RecordStartupPhase("player loaded; fast UI pending, native detours disabled by default");
        }
        // alpha48: The 10s + 4s safety window must NOT be a readiness gate
        // for the in-process UI or native pulse. The detours themselves are
        // unnecessary for ordinary display, clicking and secondary bar pulse.
        // Keeping them disabled also provides an A/B test for the reported
        // Jolt -> Veraero III change precisely when the delayed hooks attach.
        // Explicit opt-in only, and ONLY after the proven alpha42c safety wait.
        if (!config.EnableDelayedNativeHooks)
        {
            EndFastHookProbe();
            SuspendNativeRenderHooks();
            nativeObservationReadySince = 0;
            nativeObservationStatus = "高速・低干渉モード: ネイティブ描画/ActionIdフックOFF（待機なし）";
            RecordStartupPhase("fast safe mode; native hooks deliberately disabled");
            return;
        }
        if (!config.FastNativeHookStartup) EndFastHookProbe();
        var instant = config.FastNativeHookStartup && config.InstantNativeHookStartup;
        var loginDelay = instant ? 0 : (config.FastNativeHookStartup ? FastLoginSafetyDelayMs : LoginSafetyDelayMs);
        var warmup = instant ? 0 : (config.FastNativeHookStartup ? FastNativeObservationWarmupMs : NativeObservationWarmupMs);
        // Still never read/attach hooks before the ordinary UI and local
        // player have appeared, even if instant mode is selected.
        if (now - loginSafetySince < loginDelay ||
            (config.FastNativeHookStartup && (Volatile.Read(ref earlyHotbarReady) == 0 ||
                                               (instant && objects.LocalPlayer == null))))
        {
            SuspendNativeRenderHooks();
            nativeObservationReadySince = 0;
            instantHudReadyFrames = 0;
            lastInstantHudModulePointer = 0;
            var remain = (loginDelay - (now - loginSafetySince) + 999) / 1000;
            nativeObservationStatus = instant
                ? "即時適用: キャラクター・HUD準備待ち（固定時間待機なし）"
                : config.FastNativeHookStartup
                    ? $"高速フック: 通常UI準備とログイン安定待ち（残り約{Math.Max(0, remain)}秒）"
                    : $"ログイン保護中（純正HUDアクセスまで {Math.Max(0, remain)}秒）";
            return;
        }

        // The only unsafe readiness probe is now after a full player delay.
        bool ready;
        nint hudModulePointer = 0;
        try
        {
            var module = RaptureHotbarModule.Instance();
            ready = module != null && module->ModuleReady;
            if (ready) hudModulePointer = (nint)module;
        }
        catch (Exception ex)
        {
            ready = false;
            log.Debug($"HUD readiness: {ex.GetType().Name}");
        }
        if (!ready)
        {
            SuspendNativeRenderHooks();
            nativeObservationReadySince = 0;
            instantHudReadyFrames = 0;
            lastInstantHudModulePointer = 0;
            nativeObservationStatus = "FF14ホットバーのModuleReady待ち（フック停止）";
            RecordStartupPhase("waiting for native HUD ModuleReady");
            return;
        }

        if (instant)
        {
            // Time-independent readiness gate. Consecutive framework frames
            // avoid attaching to a transient single-frame ModuleReady state.
            // If enabled mid-session this takes only a few frames. This does
            // NOT guarantee safety on every Dalamud/FF14 version.
            if (lastInstantHudModulePointer != hudModulePointer)
            {
                instantHudReadyFrames = 0;
                lastInstantHudModulePointer = hudModulePointer;
            }
            instantHudReadyFrames = Math.Min(InstantHudReadyFramesRequired, instantHudReadyFrames + 1);
            if (instantHudReadyFrames < InstantHudReadyFramesRequired)
            {
                SuspendNativeRenderHooks();
                nativeObservationStatus = $"即時適用: HUD連続確認 {instantHudReadyFrames}/{InstantHudReadyFramesRequired} フレーム";
                return;
            }
        }
        else
        {
            instantHudReadyFrames = 0;
            lastInstantHudModulePointer = 0;
            if (nativeObservationReadySince == 0)
            {
                nativeObservationReadySince = now;
                RecordStartupPhase("native HUD ModuleReady; stability delay started");
            }
            var stable = now - nativeObservationReadySince;
            if (stable < warmup)
            {
                SuspendNativeRenderHooks();
                nativeObservationStatus = $"HUD安定化待ち {Math.Max(0, (warmup - stable + 999) / 1000)}秒";
                return;
            }
        }

        if (config.FastNativeHookStartup && !nativeRenderHooksActive)
            BeginFastHookProbe();
        EnableNativeRenderHooksAfterLogin();
        // Once an early hook survived at least 12s, deem this session stable.
        // Keep the marker while the installation is new so a silent crash on
        // the login transition will revert to the proven normal timing.
        if (fastHookProbeMarkerActive && nativeRenderHooksActive &&
            now - fastHookProbeStartTick >= 12000)
        {
            EndFastHookProbe();
            RecordStartupPhase("alpha50 early/instant native hooks stable 12s; marker cleared");
        }
        // Even if a hook is unavailable, the guarded 250ms snapshot, native
        // clicks and pulse chain can work through the original fallback path.
        Volatile.Write(ref nativeObservationArmed, 1);
        nativeObservationStatus = nativeRenderHooksActive
            ? "HUD準備完了（純正描画フック後付け済み）"
            : "HUD準備完了（ネイティブ情報はフォールバック）";
        RecordStartupPhase(nativeRenderHooksActive ? "ready, native hooks active" : "ready, fallback only");
    }


    private bool TryGetNativeMirror(int bar, int index, uint commandId, out NativeMirrorSample sample)
    {
        sample = default;
        if (bar is < 0 or > 9 || index is < 0 or > 11) return false;
        var candidate = nativeMirrorSlots[bar, index];
        var age = Environment.TickCount64 - candidate.Tick;
        if (candidate.Tick == 0 || age < 0 || age > NativeMirrorStaleMs ||
            candidate.StoredCommandId != commandId) return false;
        sample = candidate;
        return true;
    }

    private void CaptureNativeMirror(int bar, int index,
        RaptureHotbarModule.HotbarSlot* slot,
        RaptureHotbarModule.HotbarUIIntermediate* intermediate)
    {
        if (bar is < 0 or > 9 || index is < 0 or > 11 || slot == null || intermediate == null) return;
        var flags = 1u;
        if (intermediate->DrawAnts) flags |= 2u;
        if (intermediate->ActionAvailable1) flags |= 4u;
        if (intermediate->ActionAvailable2) flags |= 8u;
        if (intermediate->ActionTargetSatisfied) flags |= 16u;
        nativeMirrorSlots[bar, index] = new NativeMirrorSample
        {
            Tick = Environment.TickCount64,
            StoredCommandId = slot->CommandId,
            ActionId = intermediate->ActionId,
            IconId = intermediate->IconId,
            CooldownMode = intermediate->CooldownMode,
            CooldownPercent = intermediate->CooldownPercent,
            CooldownSeconds = intermediate->CooldownSeconds,
            ChargePercent = intermediate->ChargePercent,
            CurrentCharges = intermediate->CurrentCharges,
            CostValue = intermediate->CostValue,
            Flags = flags
        };
        Interlocked.Increment(ref nativeMirrorFrames);
    }

    private void PublishNativeMirror(RaptureHotbarModule* module)
    {
        var hb = Math.Clamp(config.SelectedHotbar - 1, 0, 9);
        var freshCount = 0;
        var currentTick = Environment.TickCount64;
        for (int i = 0; i < 12; i++)
        {
            var baseOffset = NativeMirrorSlotBase + i * NativeMirrorSlotBytes;
            var slot = module->GetSlotById((uint)hb, (uint)i);
            var command = slot == null ? 0u : slot->CommandId;
            bool fresh = TryGetNativeMirror(hb, i, command, out var sample);
            if (fresh) freshCount++;
            // Mark stale slots invalid. Native render calls can stop when a HUD
            // root is hidden. Do NOT replay stale icons or last-known glow.
            overlayView.Write(baseOffset + 0, fresh ? sample.ActionId : 0u);
            overlayView.Write(baseOffset + 4, fresh ? sample.IconId : 0u);
            overlayView.Write(baseOffset + 8, fresh ? sample.CooldownMode : 0u);
            overlayView.Write(baseOffset + 12, fresh ? sample.CooldownPercent : 0u);
            overlayView.Write(baseOffset + 16, fresh ? sample.CooldownSeconds : 0u);
            overlayView.Write(baseOffset + 20, fresh ? sample.ChargePercent : 0u);
            overlayView.Write(baseOffset + 24, fresh ? sample.CurrentCharges : 0u);
            overlayView.Write(baseOffset + 28, fresh ? sample.Flags : 0u);
            overlayView.Write(baseOffset + 32, fresh ? (int)Math.Clamp(currentTick - sample.Tick, 0, 1000000) : -1);
            overlayView.Write(baseOffset + 36, command);
            overlayView.Write(baseOffset + 40, fresh ? sample.CostValue : 0u);
            overlayView.Write(baseOffset + 44, slot == null ? -1 : (int)slot->CommandType); // read-only slot kind
        }
        nativeMirrorFreshSlots = freshCount;
        nativeMirrorStatus = freshCount > 0
            ? $"描画フック情報 {freshCount}/12（未描画スロットは従来方式にフォールバック）"
            : "描画データなし（非表示・未描画・フック未対応の可能性）。従来方式で動作中";
        // Write the signature LAST so a legacy/partial map cannot look valid.
        overlayView.Write(NativeMirrorHeader + 4, hb + 1);
        overlayView.Write(NativeMirrorHeader + 8, freshCount);
        overlayView.Write(NativeMirrorHeader + 12, (int)Math.Clamp(Interlocked.Read(ref nativeMirrorFrames), 0L, int.MaxValue));
        overlayView.Write(NativeMirrorHeader + 16, currentTick);
        overlayView.Write(NativeMirrorHeader + 24, config.OverlayNativeMirrorEnabled ? 1 : 0);
        overlayView.Write(NativeMirrorHeader, NativeMirrorMagic);
    }

    private const int OverlayMagic = 0x32504857;
    private readonly MemoryMappedFile overlayMap;
    private readonly MemoryMappedViewAccessor overlayView;
    private readonly HashSet<uint> exportedIcons = new();
    private int overlaySequence;
    private readonly uint[] lastOverlayPublishedIds = new uint[12];
    private int liveChangedSlots;
    private string liveOverlayStatus = "未取得";
    private int iconExportFailures;
    private string iconStatus = "未取得";
    private long lastScan;
    private long nextPulseAt;
    private readonly bool[] previousKeyDown = new bool[12];
    // Only key presses that began WITHOUT Shift/Ctrl/Alt may become plain-key
    // pulse sources. Preserve eligibility for keys already held when Shift is
    // pressed, so that holding 1 resumes after a manual modified hotkey.
    private readonly bool[] pulseKeyEligible = new bool[12];
    private readonly long[] pulseUnmodifiedPressTicks = new long[12];
    private int activePulseSource = -1;
    private long lastPulseKeyPressAt;
    private int pulseCount;
    private string pulseStatus = "未実行";
    // Optional original FF14 UI-sound synchronization while holding a hotbar key.
    // Reuses the genuine sound path (ID 12); it does not alter actual key inputs.
    // The sound interception may need updating after FF14 patches.
    // nint is used for the ABI return as the native function is not yet verified
    // on the user's current FF14 build (void callers ignore RAX; pointer callers
    // require RAX to be preserved). Treat this as a deliberately opt-in test.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OriginalUiSoundDelegate(uint soundId, nint soundDataA, nint soundDataB, byte flag);
    private Hook<OriginalUiSoundDelegate>? originalUiSoundHook;
    private bool originalUiSoundHookInstalled;
    private int audioSoundHookEnabled;
    private int audioHeldKey;
    private const uint NativeInputSoundId = 12u;
    // Keep the intercepted-call guard: do not replay until actual input sound
    // was observed on this client's current build.
    private int audioSeen;
    private string audioSyncStatus = "OFF（通常のFF14サウンド）";
    [ThreadStatic] private static bool ownNativeAudioReplay;

    private nint OnOriginalUiSound(uint id, nint a, nint b, byte flag)
    {
        // Never intercept our own synthetic (yet genuine FF14) game UI sound.
        // Avoid locks/logging/FF14 API calls in the native audio callback.
        if (!ownNativeAudioReplay &&
            Volatile.Read(ref audioSoundHookEnabled) != 0 &&
            Volatile.Read(ref audioHeldKey) != 0 &&
            id == NativeInputSoundId)
        {
            Interlocked.Increment(ref audioSeen);
            // Skipping this one sound call is intentional. An ID unrelated to
            // the Turbo keypress MUST NOT be configured here.
            return 0;
        }
        return originalUiSoundHook!.Original(id, a, b, flag);
    }

    // Called on Dalamud's game/framework thread, never from the native detour.
    // Lazily installed after hotbar readiness, and disabled immediately in the
    // next frame if the user unchecks the option.
    private void UpdateOriginalUiSoundHook(bool uiReady)
    {
        if (!config.SoundSyncNativeOnPulse || !uiReady || !clientState.IsLoggedIn)
        {
            Volatile.Write(ref audioSoundHookEnabled, 0);
            Volatile.Write(ref audioHeldKey, 0);
            if (originalUiSoundHookInstalled && originalUiSoundHook is not null)
            {
                try { originalUiSoundHook.Disable(); } catch { }
                originalUiSoundHookInstalled = false;
            }
            if (!config.SoundSyncNativeOnPulse) audioSyncStatus = "OFF（通常のFF14サウンド）";
            return;
        }
        try
        {
            // Static game function address resolved by FFXIVClientStructs.
            // No hardcoded signatures, no probing at plugin load.
            if (originalUiSoundHook is null)
            {
                var address = (nint)UIGlobals.MemberFunctionPointers.PlaySoundEffect;
                if (address == 0)
                {
                    audioSyncStatus = "音声フックのアドレス未取得（通常音を維持）";
                    return;
                }
                originalUiSoundHook = gameInterop.HookFromAddress<OriginalUiSoundDelegate>(address, OnOriginalUiSound);
            }
            if (!originalUiSoundHookInstalled)
            {
                originalUiSoundHook.Enable();
                originalUiSoundHookInstalled = true;
            }
            Volatile.Write(ref audioSoundHookEnabled, 1);
            audioSyncStatus = "純正操作音の同期処理が有効です";
        }
        catch (Exception ex)
        {
            Volatile.Write(ref audioSoundHookEnabled, 0);
            Volatile.Write(ref audioHeldKey, 0);
            audioSyncStatus = $"音声フックを有効化できません: {ex.GetType().Name}";
            log.Warning($"Audio synchronization hook unavailable: {ex.Message}");
        }
    }

    private void ReplayNativeSoundAtPulse()
    {
        // Do not add a second sound unless we have actually intercepted the
        // target game sound. Otherwise another native audio path may be in use.
        if (Volatile.Read(ref audioSoundHookEnabled) == 0 ||
            Volatile.Read(ref audioHeldKey) == 0 ||
            Volatile.Read(ref audioSeen) == 0) return;
        ownNativeAudioReplay = true;
        try
        {
            UIGlobals.PlaySoundEffect(NativeInputSoundId);
        }
        catch (Exception ex)
        {
            audioSyncStatus = $"純正ゲーム音の再生失敗: {ex.GetType().Name}";
            log.Debug($"Audio pulse replay unavailable: {ex.Message}");
        }
        finally { ownNativeAudioReplay = false; }
    }

    private static readonly int[] DefaultKeyCodes = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30, 0xBD, 0xBB };
    // Windows virtual-key codes; user-editable, not automatic game keybind capture.
    private static readonly (string Label, int Code)[] KeyChoices = BuildKeyChoices();
    private static (string Label, int Code)[] BuildKeyChoices()
    {
        var result = new List<(string, int)> { ("なし（無効）", 0) };
        for (var i = 0; i <= 9; i++) result.Add(($"{i}", 0x30 + i));
        for (var i = 0; i < 26; i++) result.Add(($"{(char)('A' + i)}", 0x41 + i));
        for (var i = 1; i <= 12; i++) result.Add(($"F{i}", 0x6F + i));
        for (var i = 0; i <= 9; i++) result.Add(($"テンキー{i}", 0x60 + i));
        result.AddRange(new (string, int)[] {
            ("-", 0xBD), ("=", 0xBB), ("[", 0xDB), ("]", 0xDD),
            ("\\", 0xDC), (";", 0xBA), (",", 0xBC), (".", 0xBE), ("/", 0xBF),
            ("Space", 0x20), ("Tab", 0x09), ("Insert", 0x2D), ("Home", 0x24),
            ("PageUp", 0x21), ("PageDown", 0x22), ("Delete", 0x2E),
            ("^（日本語キーボード）", 0xDE), ("-（日本語キーボード）", 0xBD),
            ("テンキー+", 0x6B), ("テンキー-", 0x6D), ("テンキー*", 0x6A), ("テンキー/", 0x6F)
        });
        return result.ToArray();
    }
    private int[] EffectiveKeyCodes => config.PulseKeyCodes is { Length: 12 } codes ? codes : DefaultKeyCodes;
    private int lastPulseBar = -1;
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private bool settingsVisible;
    private Process? overlayProcess;
    private Process? restoreProcess;
    private string restoreStatus = "停止中";
    private string overlayStatus = "停止中";
    private long lastOverlayCheck;

    private readonly string[] layouts = ["12x1", "6x2", "4x3", "3x4", "2x6", "1x12"];
    // Persist only user-requested UI visibility overrides; never modify FF14's
    // saved HUD/character settings. The target addon can be recreated on login.
    private readonly HashSet<int> locallyHiddenHotbars = new();
    private long lastVisibilityApplyTick;
    private string hotbarVisibilityStatus = "未操作";

    private AddonActionBarBase* GetHotbarAddon(int bar)
    {
        if (bar < 1 || bar > 10) return null;
        var addonName = bar == 1 ? "_ActionBar" : $"_ActionBar{bar - 1:00}";
        // The addon name maps to a UI instance, not necessarily the current
        // logical hotbar number (players can reassign displayed hotbars).
        // Always match RaptureHotbarId rather than assuming addon index == bar.
        for (var i = 0; i < 10; i++)
        {
            var name = i == 0 ? "_ActionBar" : $"_ActionBar{i:00}";
            var addon = gameGui.GetAddonByName<AddonActionBarBase>(name);
            if (addon != null && (int)addon->RaptureHotbarId == bar - 1)
                return addon;
        }
        return null;
    }

    private bool ApplyHotbarNodeVisibility(int bar, bool visible)
    {
        var addon = GetHotbarAddon(bar);
        if (addon == null || addon->AtkUnitBase.RootNode == null) return false;
        // Avoid redundant UI writes every frame; the game may reconstruct this
        // node during login, so compare the *current* runtime visibility.
        if (addon->AtkUnitBase.RootNode->IsVisible() != visible)
            addon->AtkUnitBase.RootNode->ToggleVisibility(visible);
        return true;
    }

    private void ToggleSelectedHotbarVisibility()
    {
        var bar = config.SelectedHotbar;
        if (locallyHiddenHotbars.Remove(bar))
        {
            // Clear the persisted request *before* restoring the native node.
            config.HiddenOriginalHotbars = locallyHiddenHotbars.OrderBy(x => x).ToArray();
            Save();
            var ok = ApplyHotbarNodeVisibility(bar, true);
            hotbarVisibilityStatus = ok ? $"ホットバー{bar}を再表示しました（保存済み）" : $"ホットバー{bar}の非表示設定を解除しました";
        }
        else
        {
            // Queue the user's choice even when the game is rebuilding its HUD.
            // After login, MaintainHiddenHotbars will find the new addon node.
            locallyHiddenHotbars.Add(bar);
            config.HiddenOriginalHotbars = locallyHiddenHotbars.OrderBy(x => x).ToArray();
            Save();
            var ok = clientState.IsLoggedIn && playerState.IsLoaded &&
                     Volatile.Read(ref earlyHotbarReady) != 0 &&
                     ApplyHotbarNodeVisibility(bar, false);
            hotbarVisibilityStatus = ok
                ? $"ホットバー{bar}を非表示にしました（次回ログイン後も維持）"
                : $"ホットバー{bar}の非表示を予約しました（HUD準備後に適用）";
        }
    }

    private void MaintainHiddenHotbars()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded ||
            Volatile.Read(ref earlyHotbarReady) == 0 || locallyHiddenHotbars.Count == 0) return;
        // Keep HUD reconstruction / switching layouts from undoing the choice,
        // but avoid scanning all ten addon pointers on every rendered frame.
        var now = Environment.TickCount64;
        if (now - lastVisibilityApplyTick < 150) return;
        lastVisibilityApplyTick = now;
        foreach (var bar in locallyHiddenHotbars)
        {
            try { ApplyHotbarNodeVisibility(bar, false); }
            catch (Exception ex) { log.Debug($"Hotbar {bar} visibility retry: {ex.GetType().Name}"); }
        }
    }


    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework, IClientState clientState, ICondition condition, IPlayerState playerState, IJobGauges jobGauges, IObjectTable objects, IGameGui gameGui, IDataManager data, IPluginLog log, IGameInteropProvider gameInterop, ITextureProvider textureProvider)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        this.textureProvider = textureProvider;
        this.clientState = clientState;
        this.condition = condition;
        this.playerState = playerState;
        this.jobGauges = jobGauges;
        this.objects = objects;
        this.gameGui = gameGui;
        this.data = data;
        this.log = log;
        this.gameInterop = gameInterop;
        clickMap = MemoryMappedFile.CreateOrOpen(clickMapName, 64, MemoryMappedFileAccess.ReadWrite);
        clickView = clickMap.CreateViewAccessor(0, 64, MemoryMappedFileAccess.ReadWrite);
        clickView.Write(0, ClickMagic);
        clickView.Write(32, 0); // default deny until settings published
        clickView.Write(36, 1);
        overlayMap = MemoryMappedFile.CreateOrOpen(OverlayMapName, OverlayMapBytes, MemoryMappedFileAccess.ReadWrite);
        overlayView = overlayMap.CreateViewAccessor(0, OverlayMapBytes, MemoryMappedFileAccess.ReadWrite);
        config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        UpgradeSettingsForRelease();
        // Load persisted hide choices; addon instances themselves cannot survive
        // a character logout, so visibility will be re-applied after UI ready.
        foreach (var bar in config.HiddenOriginalHotbars ?? [])
            if (bar is >= 1 and <= 10) locallyHiddenHotbars.Add(bar);
        // An interrupted attach forces the safer startup path on the next launch.
        CheckPreviousFastHookProbe();
        config.SelectedHotbar = Math.Clamp(config.SelectedHotbar, 1, 10);
        // alpha41: Existing users may have OverlayEnabled=true from alpha40.
        // Disable any external overlay in the new in-process renderer by default.
        if (config.InternalHotbarEnabled && config.OverlayEnabled)
        {
            config.OverlayEnabled = false;
            pluginInterface.SavePluginConfig(config);
        }
        config.InternalHotbarCellSize = Math.Clamp(config.InternalHotbarCellSize, 24, 200);
        overlayView.Write(0, OverlayMagic);
        overlayView.Write(888, 0x31524443); // CDR1: cooldown extension
        overlayView.Write(1000, 0x31474843); // CHG1: charge extension
        overlayView.Write(1120, 0x31544949); // IIT1: inventory counts + slot types
        overlayView.Write(892, config.OverlayCooldownEnabled ? 1 : 0);
        overlayView.Write(896, config.OverlayCooldownNumbers ? 1 : 0);
        overlayView.Write(1004, config.OverlayHideShortRecast ? 1 : 0);
        overlayView.Write(1008, Math.Max(0, Array.IndexOf(layouts, config.OverlayLayout)));
        overlayView.Write(512, 0x31424856);
        framework.Update += OnUpdate;
        // Discard alpha19/20/22 risky experiments, including persisted settings.
        config.WrathScopedGateEnabled = false;
        config.ScopedNativeRenderEnabled = false;
        config.NativeIconRestoreEnabled = false;
        commands.AddHandler("/wprivate", new CommandInfo(OnCommand)
        {
            HelpMessage = "config: 設定を開く / unlock: 編集 / lock: 固定 / togglelock: 切替"
        });
        pluginInterface.UiBuilder.Draw += DrawSettings;
        pluginInterface.UiBuilder.Draw += DrawInternalHotbar;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi += OpenSettings;
        // alpha42b: Crucial difference from alpha42b: DO NOT install native
        // detours here. Login and UI reconstruction must complete first.
        nativeRestoreStatus = "純正描画フック待機中（ログイン後に遅延登録）";
        scopedRenderStatus = "GetAdjustedActionId フック待機中";
        RecordStartupPhase("plugin constructed; native hooks not installed");
    }

    // One-time migration from development builds. Preserve visual calibration,
    // custom keys, manual lock, layout and cooldown preferences. Standard
    // hotbar separation and native tooltip/click routing form one feature.
    private void UpgradeSettingsForRelease()
    {
        if (config.Version >= 15) return;
        if (config.Version < 13)
        {
            // Preserve the release 2.0.0 migration for older alpha users.
            config.EnableDelayedNativeHooks = true;
            config.FastNativeHookStartup = true;
            config.InstantNativeHookStartup = true;
            config.NativeTrampolineEnabled = true;
            config.IsolateUnselectedExecution = true;
            config.IsolateUnselectedTooltip = true;
            config.InternalHotbarAutoSize = false; // retain the user's measured size
            config.OverlayEnabled = false;
            config.RestoreOverlayEnabled = false;
            config.RenderChainTraceEnabled = false;
            config.NativeIconRestoreEnabled = false;
            config.WrathScopedGateEnabled = false;
            config.ScopedNativeRenderEnabled = false;
        }
        // Version 13 did not save native UI visibility, so there is no old
        // hidden-state preference that can be inferred. New users opt in once.
        config.HiddenOriginalHotbars ??= [];
        config.Version = 15;
        pluginInterface.SavePluginConfig(config);
    }

    // Read-only probe: the game's original stored slot ID vs the adjusted action ID.
    // Do not hook WrathCombo, alter hotbar content, or execute any action.
    private void OnUpdate(IFramework _)
    {
        // Check UI readiness before optional native hook activation. In
        // alpha50's explicit instant mode both may become ready in the same
        // framework update, subject to consecutive ready-frame checks.
        UpdateEarlyHotbarReadiness();
        UpdateNativeObservationReadiness();
        var hudReady = Volatile.Read(ref nativeObservationArmed) != 0;
        var uiReady = Volatile.Read(ref earlyHotbarReady) != 0;
        // UI-only: hide while NPC conversation/choices are visible.
        // Does not block underlying native hotbar or alpha26 hooks.
        UpdateScreenEventVisibility(uiReady);
        if (hudReady) RefreshNativeInternalHotbarSize();
        ProcessOverlayClick(uiReady);
        // Native proc transitions need frame-rate updates after HUD settles.
        // This uses the managed player status list, not an unsafe detour.
        // Keep native RDM proc information up to date in fast UI mode too.
        if (uiReady) RefreshNativeProcStatus();
        UpdateOriginalUiSoundHook(uiReady);
        UpdateNativePulses();
        UpdateBuiltInTurbo();
        PublishOverlayKeyFeedback();
        // Hiding only needs normal HUD readiness, not the later native detours.
        // This ensures the chosen hotbar disappears promptly after login.
        if (uiReady) MaintainHiddenHotbars();
        if (hudReady && (config.NativeTrampolineEnabled || config.IsolateUnselectedExecution || config.IsolateUnselectedTooltip))
            DiscoverWrathNativeOriginal();
        UpdateExecuteIsolationHooks();
        UpdateTooltipIsolationHook();
        if (uiReady) RefreshHoverSlotProbe();
        if (Environment.TickCount64 - lastScan < 250) return;
        lastScan = Environment.TickCount64;
        if (!uiReady)
        {
            // Keep all unmanaged hotbar reads off until the ordinary module
            // reports ready. A 2s fast path must not bypass this check.
            if (!clientState.IsLoggedIn) SyncOverlay();
            Volatile.Write(ref restoreSnapshot, Array.Empty<(nint, uint)>());
            ClearOverlaySlots();
            return;
        }
        SyncOverlay();
        // A second visual overlay is not the requested solution and must not
        // auto-start from an old alpha setting.
        if (config.RestoreOverlayEnabled)
        {
            config.RestoreOverlayEnabled = false;
            Save();
            StopRestoreOverlay();
        }
        try
        {
            if (!clientState.IsLoggedIn) { Volatile.Write(ref restoreSnapshot, Array.Empty<(nint, uint)>()); ClearOverlaySlots(); return; }
            var module = RaptureHotbarModule.Instance();
            if (module == null || !module->ModuleReady)
            {
                Volatile.Write(ref earlyHotbarReady, 0);
                ClearOverlaySlots();
                return;
            }
            if (!hudReady)
            {
                // Pre-hook UI mode: only the selected 12 slots are published.
                // Avoid the 120-slot diagnostics, HUD node geometry, native
                // mirror detours and restore snapshots until full safe startup.
                PublishOverlaySlots(module);
                return;
            }
            // Release: only the selected hotbar snapshot and runtime state are needed.
            // Remove the old 120-slot diagnostic GetAdjustedActionId scan on every tick.
            PublishOverlaySlots(module);
        }
        catch (Exception e) { log.Debug($"Hotbar snapshot unavailable: {e.GetType().Name}: {e.Message}"); }
    }

    // Snapshot the native slot pointers and their ORIGINAL icons for all bars
    // except the selected one. Pointer identity avoids matching the same action
    // on the selected bar and changing its appearance.
    private void RefreshNativeRestoreSnapshot(RaptureHotbarModule* module)
    {
        if (!config.NativeIconRestoreEnabled || module == null || !module->ModuleReady)
        {
            Volatile.Write(ref restoreSnapshot, Array.Empty<(nint, uint)>());
            return;
        }
        var list = new List<(nint Pointer, uint Icon)>(108);
        for (var hb = 0; hb < 10; hb++)
        {
            if (hb == config.SelectedHotbar - 1) continue;
            for (var i = 0; i < 12; i++)
            {
                var slot = module->GetSlotById((uint)hb, (uint)i);
                if (slot == null || slot->CommandId == 0) continue;
                var icon = LookupOriginalIcon(slot);
                if (icon != 0) list.Add(((nint)slot, icon));
            }
        }
        Volatile.Write(ref restoreSnapshot, list.ToArray());
    }

    // Conservative level-only upgrades. This is NOT a complete game upgrade table.
    // All unmapped actions retain the previous behavior until their upgrade chain
    // is verified. No change is made to the hotbar slot, tooltip, or action use.
    private uint ResolveLevelUpgradeOnly(uint baseId)
    {
        var level = playerState.IsLoaded ? playerState.EffectiveLevel : (short)0;
        return baseId switch
        {
            // Red Mage: Jolt (Lv2) -> Jolt II (Lv62) -> Jolt III (Lv84).
            7503 or 7524 when level >= 84 => 37004,
            7503 when level >= 62 => 7524,
            7505 when level >= 82 => 25855, // Verthunder III
            7507 when level >= 82 => 25856, // Veraero III
            _ => baseId
        };
    }

    // Read the native Grand Impact Ready status, not WrathCombo's substituted
    // action ID. A native proc must take priority over our cosmetic restore.
    private void RefreshNativeProcStatus()
    {
        grandImpactReady = false;
        grandImpactStatus = "未付与";
        try
        {
            if (objects.LocalPlayer is not IBattleChara player) return;
            var statuses = data.GetExcelSheet<Lumina.Excel.Sheets.Status>();
            foreach (var effect in player.StatusList)
            {
                if (effect.StatusId == 0) continue;
                var row = statuses?.GetRow(effect.StatusId);
                if (row is not { } status) continue;
                var name = status.Name.ToString();
                if (!name.Contains("Grand Impact Ready", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("ノーブルインパクト実行可", StringComparison.Ordinal)) continue;
                grandImpactReady = true;
                grandImpactStatus = $"実行可 (Status {effect.StatusId})";
                return;
            }
        }
        catch (Exception e) { grandImpactStatus = $"判定失敗: {e.GetType().Name}"; }
    }

    // Keep vanilla Red Mage's three-Mana-Stack finishers visible on the
    // non-selected bars.  The earlier static icon restoration hid this
    // built-in transformation. This is visual-only: no CommandId or action
    // execution changes. We deliberately avoid ActionManager.GetAdjustedActionId
    // here because WrathCombo may hook it and return a rotation replacement.
    private uint ResolveNativeRdmDisplayId(uint storedId)
    {
        var level = playerState.IsLoaded ? playerState.EffectiveLevel : (short)0;
        // Red Mage enchanted melee actions depend on the CURRENT mana gauge.
        // These are native action transformations, not WrathCombo replacements.
        // The previous icon restoration had no mana logic and forced Riposte's
        // normal icon even when the player could use Enchanted Riposte.
        if (playerState.IsLoaded && level >= 50 && storedId is 7504 or 7512 or 7516)
        {
            try
            {
                var gauge = jobGauges.Get<RDMGauge>();
                var requiredMana = storedId == 7504 ? 20 : 15;
                if (gauge.WhiteMana >= requiredMana && gauge.BlackMana >= requiredMana)
                    return storedId switch
                    {
                        7504 => 7527, // Enchanted Riposte
                        7512 => 7528, // Enchanted Zwerchhau
                        7516 => 7529, // Enchanted Redoublement
                        _ => storedId
                    };
            }
            catch (Exception e) { log.Debug($"RDM native melee gauge: {e.Message}"); }
        }
        // Level 96 native proc: Jolt III and Impact both become Grand Impact.
        // Never replace this with the base Jolt icon while the buff is active.
        if (level >= 96 && grandImpactReady && (storedId is 7503 or 7524 or 37004 or 16526 or 12314))
            return 37006;
        if (level >= 68 && playerState.IsLoaded)
        {
            try
            {
                var gauge = jobGauges.Get<RDMGauge>();
                if (gauge.ManaStacks >= 3)
                {
                    // Verthunder / Verthunder III -> Verflare (Lv68)
                    if (storedId is 7505 or 25855) return 7525;
                    // Veraero / Veraero III -> Verholy (Lv70)
                    if (level >= 70 && (storedId is 7507 or 25856)) return 7526;
                }
            }
            catch (Exception e) { log.Debug($"RDM native gauge read: {e.Message}"); }
        }
        return ResolveLevelUpgradeOnly(storedId);
    }

    // Native action-bar pulse, adapted from the preserved v1.1.9 companion.
    // This alpha supports the standard unmodified 1..0,-,= keyboard layout.
    // It does not issue actions or change WrathCombo's execution behavior.
    private void UpdateNativePulses()
    {
        if (!config.PulseEnabled || Volatile.Read(ref earlyHotbarReady) == 0 || !IsGameForeground())
        {
            Volatile.Write(ref audioHeldKey, 0);
            activePulseSource = -1;
            nextPulseAt = 0;
            Array.Clear(previousKeyDown);
            Array.Clear(pulseKeyEligible);
            Array.Clear(pulseUnmodifiedPressTicks);
            return;
        }

        // Changing the selected bar must not keep a pulse stream from the old bar.
        if (lastPulseBar != config.SelectedHotbar)
        {
            lastPulseBar = config.SelectedHotbar;
            activePulseSource = -1;
            nextPulseAt = 0;
            Array.Clear(previousKeyDown);
            Array.Clear(pulseKeyEligible);
            Array.Clear(pulseUnmodifiedPressTicks);
        }
        // Key polling is based on Windows VK, not FF14's actual hotbar bindings.
        // Shift+minus (e.g. Swiftcast on bar 3) must NOT trigger plain minus
        // (e.g. potion on bar 1). Check both left/right modifier variants.
        // Update previousKeyDown with the physical state while modifiers are
        // held so releasing Shift with minus still down cannot create a false edge.
        var modifierHeld = (GetAsyncKeyState(0x10) & 0x8000) != 0 || // SHIFT
                           (GetAsyncKeyState(0x11) & 0x8000) != 0 || // CTRL
                           (GetAsyncKeyState(0x12) & 0x8000) != 0 || // ALT
                           (GetAsyncKeyState(0xA0) & 0x8000) != 0 || // LSHIFT
                           (GetAsyncKeyState(0xA1) & 0x8000) != 0 || // RSHIFT
                           (GetAsyncKeyState(0xA2) & 0x8000) != 0 || // LCTRL
                           (GetAsyncKeyState(0xA3) & 0x8000) != 0 || // RCTRL
                           (GetAsyncKeyState(0xA4) & 0x8000) != 0 || // LALT
                           (GetAsyncKeyState(0xA5) & 0x8000) != 0;   // RALT
        var keyCodes = EffectiveKeyCodes;
        var keyTick = Environment.TickCount64;
        if (modifierHeld)
        {
            Volatile.Write(ref audioHeldKey, 0);
            // Stop emitting while a modifier is held, but keep any plain-key
            // hold that began before the modifier (e.g. the user's held 1).
            // Newly pressed modified keys are NEVER promoted to a plain-key
            // pulse when Shift/Ctrl/Alt is subsequently released.
            activePulseSource = -1;
            nextPulseAt = 0;
            for (var i = 0; i < 12; i++)
            {
                var down = keyCodes[i] != 0 && (GetAsyncKeyState(keyCodes[i]) & 0x8000) != 0;
                if (!down || !previousKeyDown[i])
                    pulseKeyEligible[i] = false;
                previousKeyDown[i] = down;
            }
            pulseStatus = "修飾キー入力中は発光停止（解除後に保持中の1キーへ復帰）";
            return;
        }

        // Edge detection still gives a newly pressed key priority. However,
        // when that key is released, reacquire the most recently pressed
        // eligible key that is STILL DOWN, without requiring a key re-press.
        for (var i = 0; i < 12; i++)
        {
            var down = keyCodes[i] != 0 && (GetAsyncKeyState(keyCodes[i]) & 0x8000) != 0;
            if (!down)
            {
                pulseKeyEligible[i] = false;
            }
            else if (!previousKeyDown[i])
            {
                pulseKeyEligible[i] = true;
                pulseUnmodifiedPressTicks[i] = keyTick;
                activePulseSource = i;
                lastPulseKeyPressAt = keyTick;
                nextPulseAt = 0;
            }
            previousKeyDown[i] = down;
        }
        var sourceStillDown = activePulseSource >= 0 && activePulseSource < 12 &&
            previousKeyDown[activePulseSource] && pulseKeyEligible[activePulseSource];
        if (!sourceStillDown)
        {
            var fallback = -1;
            long lastEligiblePress = long.MinValue;
            for (var i = 0; i < 12; i++)
            {
                if (!previousKeyDown[i] || !pulseKeyEligible[i]) continue;
                if (fallback < 0 || pulseUnmodifiedPressTicks[i] > lastEligiblePress)
                {
                    fallback = i;
                    lastEligiblePress = pulseUnmodifiedPressTicks[i];
                }
            }
            if (fallback >= 0)
            {
                activePulseSource = fallback;
                lastPulseKeyPressAt = keyTick;
                nextPulseAt = 0;
            }
            else if (activePulseSource < 0 || keyTick - lastPulseKeyPressAt > 340)
            {
                // Preserve the old 340ms quick-tap grace window only when
                // there is NO longer-held eligible source to resume.
                Volatile.Write(ref audioHeldKey, 0);
                activePulseSource = -1;
                return;
            }
        }

        // Only a PHYSICALLY held, plain bound key can suppress UI sound 12.
        // The legacy 340ms release grace must not continue muting audio.
        var physicallyHeld = activePulseSource >= 0 && activePulseSource < 12 &&
            previousKeyDown[activePulseSource] && pulseKeyEligible[activePulseSource];
        Volatile.Write(ref audioHeldKey, physicallyHeld ? 1 : 0);

        var now = Environment.TickCount64;
        if (now < nextPulseAt) return;
        var delay = config.RandomPulse
            ? Random.Shared.Next(Math.Max(50, config.PulseMinMs), Math.Max(51, config.PulseMaxMs + 1))
            : Math.Max(50, config.FixedPulseMs);
        nextPulseAt = now + delay;
        try
        {
            var sourceModule = RaptureHotbarModule.Instance();
            var source = sourceModule == null ? null : sourceModule->GetSlotById((uint)(config.SelectedHotbar - 1), (uint)activePulseSource);
            if (source == null || source->CommandId == 0) return;
            // Record the observable pulse before matching slots. This gives the
            // player keypress feedback even when no secondary hotbar slot matches.
            lastPulseVisualTick = Environment.TickCount64;
            unchecked { pulseVisualCounter++; }
            if (config.SoundSyncNativeOnPulse) ReplayNativeSoundAtPulse();
            // The overlay snapshot is only refreshed every 250ms. During an RDM
            // melee chain it can lag one step behind the actual replacement.
            // Read the live Wrath-adjusted action at the moment of each pulse.
            uint action = ResolveRequestedPulseAction(source, activePulseSource);
            var matched = PulseMatchingOtherHotbars(source->CommandType, source->CommandId, action);
            pulseCount += matched;
            pulseStatus = $"発光 {matched} 枠 / 累計 {pulseCount} / キー {activePulseSource + 1} / 登録ID {source->CommandId} / 現在ID {action}";
        }
        catch (Exception ex) { pulseStatus = $"発光失敗: {ex.GetType().Name}"; }
    }

    // v2.0.5.3: OPTIONAL selected-hotbar hold-to-repeat for users without ReAction.
    // The game's original physical key press is always handled by the game.
    // This code supplies *later* presses of the same ORIGINAL slot via the
    // native ExecuteSlotById path already used by our clickable overlay.
    // No Windows SendInput, no modified action IDs and no ReAction source code.
    // Restricted to unmodified, explicitly configured keys + Action slots.
    private readonly bool[] turboKeyWasDown = new bool[12];
    private readonly long[] turboNextRepeatAt = new long[12];
    private int turboActiveSlot = -1;
    private int turboLastBar = -1;
    private long turboLastReactionCheck;
    private bool turboReactionDetected;
    private long turboRequestCount;
    private string turboStatus = "OFF（ReActionなど既存の連打設定はそのまま）";

    private void ResetBuiltInTurbo()
    {
        turboActiveSlot = -1;
        Array.Clear(turboKeyWasDown);
        Array.Clear(turboNextRepeatAt);
    }

    private bool IsReActionLoaded()
    {
        var now = Environment.TickCount64;
        if (now - turboLastReactionCheck < 2000) return turboReactionDetected;
        turboLastReactionCheck = now;
        turboReactionDetected = false;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, "ReAction", StringComparison.OrdinalIgnoreCase)) continue;
                turboReactionDetected = true;
                break;
            }
        }
        catch { turboReactionDetected = true; } // fail closed: prevent duplicate repeat
        return turboReactionDetected;
    }

    private void UpdateBuiltInTurbo()
    {
        if (!config.BuiltInTurboEnabled)
        {
            if (turboActiveSlot >= 0) ResetBuiltInTurbo();
            turboStatus = "OFF（既存のキー操作のみ）";
            return;
        }
        if (IsReActionLoaded())
        {
            ResetBuiltInTurbo();
            turboStatus = "自動停止：ReActionが読み込まれています（二重連打防止）";
            return;
        }
        if (!clientState.IsLoggedIn || Volatile.Read(ref earlyHotbarReady) == 0 ||
            !IsGameForeground() || Volatile.Read(ref hideForScreenEvent) != 0 ||
            (!config.BuiltInTurboOutOfCombat && !condition[ConditionFlag.InCombat]))
        {
            ResetBuiltInTurbo();
            turboStatus = "待機中（ゲーム未準備・会話中・非アクティブ・非戦闘）";
            return;
        }

        // Never generate actions while a player types in FF14 chat or a
        // Dalamud text input. The game-side check is the native UI flag.
        try
        {
            var stage = AtkStage.Instance();
            if ((stage != null && stage->AtkInputManager != null &&
                 stage->AtkInputManager->IsTextInputActive) ||
                ImGui.GetIO().WantTextInput)
            {
                ResetBuiltInTurbo();
                turboStatus = "入力欄で文字入力中のため停止";
                return;
            }
        }
        catch
        {
            ResetBuiltInTurbo();
            turboStatus = "UI入力状態を安全に確認できないため停止";
            return;
        }

        // The binding list is shared with visual pulses, not FF14's native
        // keybind reader. Modified Shift/Ctrl/Alt combinations are intentionally
        // excluded; when a modifier is held, do not synthesize *any* action.
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0 ||
            (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
            (GetAsyncKeyState(0x12) & 0x8000) != 0 ||
            (GetAsyncKeyState(0xA0) & 0x8000) != 0 ||
            (GetAsyncKeyState(0xA1) & 0x8000) != 0 ||
            (GetAsyncKeyState(0xA2) & 0x8000) != 0 ||
            (GetAsyncKeyState(0xA3) & 0x8000) != 0 ||
            (GetAsyncKeyState(0xA4) & 0x8000) != 0 ||
            (GetAsyncKeyState(0xA5) & 0x8000) != 0)
        {
            ResetBuiltInTurbo();
            turboStatus = "修飾キー中は停止（Shift / Ctrl / Alt 非対応）";
            return;
        }

        if (turboLastBar != config.SelectedHotbar)
        {
            turboLastBar = config.SelectedHotbar;
            ResetBuiltInTurbo();
        }
        var keys = EffectiveKeyCodes;
        var tick = Environment.TickCount64;
        var interval = Math.Clamp(config.BuiltInTurboIntervalMs, 50, 1000);
        var delay = Math.Clamp(config.BuiltInTurboStartDelayMs, 0, 1000);
        var newlyDown = -1;
        for (var i = 0; i < 12; i++)
        {
            bool down = keys[i] != 0 && (GetAsyncKeyState(keys[i]) & 0x8000) != 0;
            if (down && !turboKeyWasDown[i])
            {
                newlyDown = i;
                // The game's ORIGINAL keypress already handled the first hit.
                // A 0ms start delay still schedules the first repeat at +interval.
                turboNextRepeatAt[i] = tick + delay + interval;
            }
            if (!down) turboNextRepeatAt[i] = 0;
            turboKeyWasDown[i] = down;
        }
        if (newlyDown >= 0) turboActiveSlot = newlyDown;
        if (turboActiveSlot is < 0 or >= 12 || !turboKeyWasDown[turboActiveSlot])
        {
            turboActiveSlot = -1;
            return;
        }
        var slotIndex = turboActiveSlot;
        if (tick < turboNextRepeatAt[slotIndex])
        {
            turboStatus = $"保持中：HB{config.SelectedHotbar} スロット{slotIndex + 1} / {interval}ms";
            return;
        }
        // Schedule before native execution; one frame may NEVER catch up
        // by issuing a burst of actions after a stall or loading.
        turboNextRepeatAt[slotIndex] = tick + interval;
        try
        {
            var module = RaptureHotbarModule.Instance();
            if (module == null || !module->ModuleReady) { ResetBuiltInTurbo(); return; }
            var slot = module->GetSlotById((uint)(config.SelectedHotbar - 1), (uint)slotIndex);
            // No repeat for macros, items, or empty slots in this first release.
            if (slot == null || slot->CommandId == 0 ||
                slot->CommandType != RaptureHotbarModule.HotbarSlotType.Action)
            {
                turboStatus = "アクション以外（マクロ・アイテム等）は連打対象外";
                return;
            }
            module->ExecuteSlotById((uint)(config.SelectedHotbar - 1), (uint)slotIndex);
            turboRequestCount++;
            turboStatus = $"連打実行要求：HB{config.SelectedHotbar}-{slotIndex + 1} / {interval}ms / 累計{turboRequestCount}回";
        }
        catch (Exception ex)
        {
            ResetBuiltInTurbo();
            turboStatus = $"連打処理停止：{ex.GetType().Name}";
            log.Warning($"Built-in Turbo unavailable: {ex.Message}");
        }
    }

    // Use the same Wrath-adjusted ID for physical keys and overlay clicks.
    // Read it BEFORE a click executes so the pulse refers to the pressed skill,
    // not to the next combo stage. The last published snapshot is only fallback.
    private uint ResolveRequestedPulseAction(RaptureHotbarModule.HotbarSlot* source, int slotIndex)
    {
        if (source == null || source->CommandId == 0 || slotIndex is < 0 or >= 12) return 0;
        uint action = lastOverlayPublishedIds[slotIndex];
        if (source->CommandType == RaptureHotbarModule.HotbarSlotType.Action)
        {
            var manager = ActionManager.Instance();
            if (manager != null)
            {
                var adjusted = manager->GetAdjustedActionId(source->CommandId);
                if (adjusted != 0) action = adjusted;
            }
        }
        return action;
    }

    // Single matching rule for both input paths. This performs a native HUD
    // visual pulse ONLY; it never activates the skill or edits hotbar slots.
    private int PulseMatchingOtherHotbars(RaptureHotbarModule.HotbarSlotType type, uint stored, uint adjusted)
    {
        if (stored == 0) return 0;
        return type == RaptureHotbarModule.HotbarSlotType.Action && !IsSprintSlot(type, stored)
            ? PulseOneMatchingNativeSlot(adjusted, stored)
            : PulseOneMatchingNonActionSlot(type, stored);
    }

    // Match the currently displayed native action, NOT merely the stored ID.
    // In alpha25, `stored == sourceStored` and `nativeTarget == nativeSource`
    // illuminated unrelated combo stages and hid Scorch/Resolution on Jolt.
    // Here Wrath decides the requested action only on the selected bar, while
    // the unmodified game's adjusted ID determines what other bars display.
    private uint GetNativePulseAction(uint stored)
    {
        if (stored == 0) return 0;
        if (config.NativeTrampolineEnabled && wrathNativeOriginal != null)
        {
            try
            {
                var native = wrathNativeOriginal(stored);
                if (native != 0) return native;
            }
            catch (Exception ex)
            {
                pulseNativeErrors++;
                pulseNativeLastError = ex.GetType().Name;
            }
        }
        // Only a fallback for missing native trampoline; this is not a
        // comprehensive FF14 native transform table.
        return ResolveNativeRdmDisplayId(stored);
    }

    private long pulseNativeErrors;
    private string pulseNativeLastError = "なし";
    private uint lastPulseRequested;
    private uint lastPulseTargetNative;
    private int lastPulseTargetBar;
    private int lastPulseTargetSlot;

    // v2.0.5.8: A transformed action can occupy several distinct stored
    // slots (e.g. BLM Fire + Blizzard both display Paradox). Choose exactly
    // ONE target for each pulse; do not make every matching icon depress.
    // When possible prefer the original stored action, otherwise use the first
    // valid native HUD slot. Never change any hotbar assignment or action ID.
    private int PulseOneMatchingNativeSlot(uint requested, uint sourceStored)
    {
        if (sourceStored == 0 || requested == 0) return 0;
        var module = RaptureHotbarModule.Instance();
        if (module == null) return 0;

        var sourceNative = GetNativePulseAction(sourceStored);
        var sameAction = requested == sourceNative || requested == sourceStored;
        lastPulseRequested = requested;
        lastPulseTargetNative = 0;
        lastPulseTargetBar = 0;
        lastPulseTargetSlot = 0;

        AddonActionBarBase* chosenAddon = null;
        int chosenIndex = -1;
        uint chosenNative = 0;
        int chosenBar = -1;
        bool chosenStoredIdMatches = false;
        for (var addonIndex = 0; addonIndex < 10; addonIndex++)
        {
            var addonName = addonIndex == 0 ? "_ActionBar" : $"_ActionBar{addonIndex:00}";
            var addon = gameGui.GetAddonByName<AddonActionBarBase>(addonName);
            if (addon == null || addon->SlotCount == 0) continue;
            var barIndex = (int)addon->RaptureHotbarId;
            if (barIndex < 0 || barIndex >= 10 || barIndex == config.SelectedHotbar - 1) continue;
            var slots = addon->ActionBarSlotVector.AsSpan();
            var limit = Math.Min((int)addon->SlotCount, slots.Length);
            for (var i = 0; i < limit; i++)
            {
                var slot = module->GetSlotById((uint)barIndex, (uint)i);
                if (slot == null || slot->CommandType != RaptureHotbarModule.HotbarSlotType.Action) continue;
                var stored = slot->CommandId;
                if (stored == 0) continue;
                var native = GetNativePulseAction(stored);
                bool matches = native == requested ||
                    (sameAction && stored == sourceStored && native == sourceNative);
                if (!matches) continue;

                bool preferred = stored == sourceStored;
                // Keep the first valid match unless we find a better match
                // whose ORIGINAL stored action matches the pressed source.
                if (chosenAddon != null && (!preferred || chosenStoredIdMatches)) continue;
                chosenAddon = addon;
                chosenIndex = i;
                chosenNative = native;
                chosenBar = barIndex;
                chosenStoredIdMatches = preferred;
            }
        }
        if (chosenAddon == null || chosenIndex < 0) return 0;
        chosenAddon->PulseActionBarSlot(chosenIndex);
        lastPulseTargetNative = chosenNative;
        lastPulseTargetBar = chosenBar + 1;
        lastPulseTargetSlot = chosenIndex + 1;
        return 1;
    }

    // Sprint may be saved as GeneralAction 4 or Action 3 depending on the
    // hotbar slot representation. Compare both as the same native Sprint.
    private static bool IsSprintSlot(RaptureHotbarModule.HotbarSlotType type, uint id) =>
        (type == RaptureHotbarModule.HotbarSlotType.GeneralAction && id == 4) ||
        (type == RaptureHotbarModule.HotbarSlotType.Action && id == 3);

    private int PulseOneMatchingNonActionSlot(RaptureHotbarModule.HotbarSlotType sourceType, uint sourceId)
    {
        var module = RaptureHotbarModule.Instance();
        if (module == null || sourceId == 0) return 0;
        var sourceIsSprint = IsSprintSlot(sourceType, sourceId);
        var count = 0;
        for (var addonIndex = 0; addonIndex < 10; addonIndex++)
        {
            var addonName = addonIndex == 0 ? "_ActionBar" : $"_ActionBar{addonIndex:00}";
            var addon = gameGui.GetAddonByName<AddonActionBarBase>(addonName);
            if (addon == null || addon->SlotCount == 0) continue;
            var barIndex = (int)addon->RaptureHotbarId;
            if (barIndex < 0 || barIndex >= 10 || barIndex == config.SelectedHotbar - 1) continue;
            var slots = addon->ActionBarSlotVector.AsSpan();
            var limit = Math.Min((int)addon->SlotCount, slots.Length);
            for (var i = 0; i < limit; i++)
            {
                var slot = module->GetSlotById((uint)barIndex, (uint)i);
                if (slot == null) continue;
                var match = (slot->CommandType == sourceType && slot->CommandId == sourceId) ||
                    (sourceIsSprint && IsSprintSlot(slot->CommandType, slot->CommandId));
                if (!match) continue;
                addon->PulseActionBarSlot(i);
                count++;
            }
        }
        return count;
    }

    private static bool IsKnownUpgrade(uint original, uint used) =>
        original switch
        {
            7503 => used == 7524 || used == 37004,
            7524 => used == 37004,
            _ => false
        };

    private uint LookupOriginalIcon(RaptureHotbarModule.HotbarSlot* slot)
    {
        var id = slot->CommandId;
        try
        {
            switch (slot->CommandType)
            {
                case RaptureHotbarModule.HotbarSlotType.Action:
                    // Hotbar CommandId stores the base action (e.g. RDM Jolt 7503),
                    // not necessarily the level-upgraded action shown by the game.
                    // Do NOT use ActionManager.GetAdjustedActionId here: WrathCombo
                    // can redirect it to a completely different rotation action.
                    var displayId = ResolveNativeRdmDisplayId(id);
                    if (data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(displayId) is { } action)
                        return action.Icon;
                    return 0;
                case RaptureHotbarModule.HotbarSlotType.Item:
                    if (data.GetExcelSheet<Item>()?.GetRow(id % 1_000_000) is { } item)
                        return item.Icon;
                    return 0;
                case RaptureHotbarModule.HotbarSlotType.GeneralAction:
                    uint icon = 0;
                    if (data.GetExcelSheet<GeneralAction>()?.GetRow(id) is { } general)
                        icon = unchecked((uint)general.Icon);
                    if (icon == 0 && id == 4 &&
                        data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(3) is { } sprint)
                        icon = sprint.Icon;
                    return icon;
                default:
                    return 0;
            }
        }
        catch { return 0; }
    }

    // alpha19 returned the raw hotbar action here, accidentally suppressing
    // FFXIV's OWN action transforms (Enchanted Riposte, Grand Impact, Scorch,
    // Resolution).  We must not do that.  Always invoke the original adjusted
    // action function; the native RDM melee correction below is a guard for
    // cases where another plugin's detour is chained before ours.
    // IMPORTANT: hook order may still permit WrathCombo to replace Jolt.
    private uint OnGetAdjustedActionId(ActionManager* self, uint actionId)
    {
        if (Volatile.Read(ref nativeObservationArmed) == 0)
            return adjustedActionHook!.Original(self, actionId);
        // alpha51: during a verified HB2..10 native slot execution, prefer
        // FF14's original adjustment path. Never change global Wrath settings.
        // Only the exact stored action ID is eligible, so unrelated calls
        // (even on the same thread) are passed on unchanged.
        if ((config.IsolateUnselectedExecution || config.IsolateUnselectedTooltip) && executingPureDepth > 0 &&
            executingPureBar >= 0 && executingPureBar != config.SelectedHotbar - 1 &&
            actionId == executingPureStoredId)
        {
            if (wrathNativeOriginal != null)
            {
                try
                {
                    uint result = wrathNativeOriginal(actionId);
                    executeIsolationLastNative = result;
                    if (config.IsolateUnselectedExecution)
                        Interlocked.Increment(ref executeIsolationScopedAdjustments);
                    if (config.IsolateUnselectedTooltip)
                        Interlocked.Increment(ref tooltipNativeBypasses);
                    return result;
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref executeIsolationFailures);
                    executeIsolationStatus = $"標準経路呼出失敗: {e.GetType().Name}";
                    // Fail open to game normal action pipeline, not a synthetic action.
                }
            }
            else Interlocked.Increment(ref executeIsolationScopeMisses);
        }
        var inOtherBar = nativeRenderScopeDepth > 0 &&
            tracedBar >= 0 && tracedBar != config.SelectedHotbar - 1;
        uint adjusted;
        if (config.NativeTrampolineEnabled && inOtherBar && wrathNativeOriginal != null)
        {
            // Call the trampoline exposed by WrathCombo itself. Never return
            // the raw stored ID; native game transformations are preserved.
            try
            {
                adjusted = wrathNativeOriginal(actionId);
                Interlocked.Increment(ref nativeTrampolineCalls);
                Volatile.Write(ref lastTrampolineInput, actionId);
                Volatile.Write(ref lastTrampolineNative, adjusted);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref nativeTrampolineFailures);
                wrathOriginalStatus = $"実行失敗: {ex.GetType().Name}";
                config.NativeTrampolineEnabled = false;
                adjusted = adjustedActionHook!.Original(self, actionId);
            }
        }
        else
        {
            if (config.NativeTrampolineEnabled && inOtherBar)
                Interlocked.Increment(ref nativeTrampolineMisses);
            adjusted = adjustedActionHook!.Original(self, actionId);
        }
        if (config.RenderChainTraceEnabled && tracedDepth > 0 &&
            tracedBar >= 0 && tracedBar < 10 && tracedSlot >= 0 && tracedSlot < 12)
        {
            chainInput[tracedBar,tracedSlot] = actionId;
            chainOutput[tracedBar,tracedSlot] = adjusted;
            Interlocked.Increment(ref chainCalls[tracedBar,tracedSlot]);
        }
        if (nativeRenderScopeDepth <= 0 || !config.ScopedNativeRenderEnabled)
            return adjusted;

        Interlocked.Increment(ref scopedNativeCalls);
        if (actionId is 7504 or 7512 or 7516)
        {
            // A normal FFXIV gauge-driven transformation, never a WrathCombo
            // rotation recommendation.  Do not force the raw unenchanted ID.
            var native = ResolveNativeRdmDisplayId(actionId);
            if (native != actionId)
            {
                Interlocked.Increment(ref nativeMeleeAdjustments);
                return native;
            }
        }
        if (actionId is 7503 or 7524 or 37004)
        {
            // The game handles Grand Impact / Scorch / Resolution as native
            // action replacements. Preserve its adjusted result, even if the
            // WrathCombo detour ordering prevents isolation in this build.
            if (adjusted != actionId)
                Interlocked.Increment(ref nativeJoltAdjustments);
        }
        return adjusted;
    }

    private void OnPrepareSlotForRender(RaptureHotbarModule* module,
        RaptureHotbarModule.HotbarSlot* slot,
        RaptureHotbarModule.HotbarUIIntermediate* intermediate)
    {
        // During login / UI recreation forward directly to the native render
        // chain. No slot dereference, addon lookup, or UI intermediate reads.
        if (Volatile.Read(ref nativeObservationArmed) == 0 ||
            !clientState.IsLoggedIn || module == null)
        {
            iconRenderHook!.Original(module, slot, intermediate);
            return;
        }
        // Restrict the experiment to a known registered slot outside the
        // selected bar. Do not change the actual slot or action execution.
        // Identify the slot for trace and scope, independently of the
        // experimental transform option. This does not change the slot.
        var scoped = false;
        var traceThis = false;
        var traceBar = -1;
        var traceIndex = -1;
        if ((config.ScopedNativeRenderEnabled || config.RenderChainTraceEnabled || config.WrathScopedGateEnabled || config.NativeTrampolineEnabled) &&
            adjustedActionHook != null && slot != null &&
            slot->CommandType == RaptureHotbarModule.HotbarSlotType.Action)
        {
            var liveModule = RaptureHotbarModule.Instance();
            if (liveModule != null)
                for (var hb = 0; hb < 10 && traceBar < 0; hb++)
                    for (var si = 0; si < 12; si++)
                        if (liveModule->GetSlotById((uint)hb, (uint)si) == slot)
                        {
                            traceBar = hb;
                            traceIndex = si;
                            break;
                        }
            scoped = traceBar >= 0 && traceBar != config.SelectedHotbar - 1 &&
                     (config.ScopedNativeRenderEnabled || config.NativeTrampolineEnabled);
            traceThis = traceBar >= 0 && (config.RenderChainTraceEnabled || config.NativeTrampolineEnabled);
        }
        if (scoped) nativeRenderScopeDepth++;
        var oldBar = tracedBar;
        var oldSlot = tracedSlot;
        if (traceThis)
        {
            tracedBar = traceBar;
            tracedSlot = traceIndex;
            tracedDepth++;
        }
        // Scope is the selected hotbar's complement. Temporarily set the
        // official WrathCombo in-memory ActionChanging flag false while this
        // one native slot is being rendered. Restore it in finally even on
        // exceptions. No file/config save, no patch to WrathCombo assembly.
        // IMPORTANT: this is an experimental compatibility gate: the field
        // may control hotbar walking, not all action replacement detours.
        var gateThis = false && config.WrathScopedGateEnabled && traceBar >= 0 &&
            traceBar != config.SelectedHotbar - 1 &&
            wrathConfiguration != null && wrathActionChangingField != null;
        var gateHeld = false;
        var previousWrathFlag = false;
        try
        {
            if (gateThis)
            {
                Monitor.Enter(wrathGateSync, ref gateHeld);
                previousWrathFlag = (bool)wrathActionChangingField!.GetValue(wrathConfiguration)!;
                wrathActionChangingField.SetValue(wrathConfiguration, false);
                Interlocked.Increment(ref wrathGateApplied);
            }
            else if (config.WrathScopedGateEnabled && traceBar >= 0 &&
                     traceBar != config.SelectedHotbar - 1)
                Interlocked.Increment(ref wrathGateSkipped);
            iconRenderHook!.Original(module, slot, intermediate);
        }
        finally
        {
            if (gateHeld)
            {
                try { wrathActionChangingField!.SetValue(wrathConfiguration, previousWrathFlag); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref wrathGateErrors);
                    wrathGateStatus = $"復元失敗: {ex.GetType().Name}（機能をOFFにしてください）";
                    config.WrathScopedGateEnabled = false;
                }
                Monitor.Exit(wrathGateSync);
            }
            if (traceThis)
            {
                tracedDepth--;
                tracedBar = oldBar;
                tracedSlot = oldSlot;
            }
            if (scoped)
            {
                nativeRenderScopeDepth--;
                Interlocked.Increment(ref scopedRenderCalls);
            }
        }
        Interlocked.Increment(ref renderCalls);
        if (slot == null || intermediate == null || !clientState.IsLoggedIn ||
            Volatile.Read(ref nativeObservationArmed) == 0) return;
        // Match the actual native slot pointer; unlike adjusted action IDs, this
        // identifies which bar/slot was rendered without guessing Wrath output.
        var currentModule = RaptureHotbarModule.Instance();
        if (currentModule == null || !currentModule->ModuleReady) return;
        for (var bar = 0; bar < 10; bar++)
        for (var index = 0; index < 12; index++)
        {
            if (currentModule->GetSlotById((uint)bar, (uint)index) != slot) continue;
            if (bar == config.SelectedHotbar - 1)
                CaptureNativeMirror(bar, index, slot, intermediate);
            observedRenderAction[bar,index] = intermediate->ActionId;
            observedRenderIcon[bar,index] = intermediate->IconId;
            observedRenderCount[bar,index]++;
            // WARNING: icon-only correction cannot repair tooltip or native
            // combo glow. Disabled by default; kept as experimental toggle.
            // Opt-in, fail-closed correction: only a native Action slot with
            // unchanged intermediate ActionId. Never touch the selected bar,
            // ActionId, slot storage, item/general actions or execution.
            if (config.NativeIconRestoreEnabled && !config.ScopedNativeRenderEnabled &&
                bar != config.SelectedHotbar - 1 &&
                slot->CommandType == RaptureHotbarModule.HotbarSlotType.Action &&
                intermediate->ActionId == slot->CommandId)
            {
                var nativeId = ResolveNativeRdmDisplayId(slot->CommandId);
                var row = data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(nativeId);
                if (row is { } native && native.Icon != 0)
                {
                    var targetIcon = native.Icon;
                    if (intermediate->IconId != targetIcon)
                    {
                        intermediate->IconId = targetIcon;
                        Interlocked.Increment(ref nativeCorrections);
                    }
                }
                else Interlocked.Increment(ref nativeSkipped);
            }
            return;
        }
    }

    private void SetNativeRestore(bool enabled)
    {
        config.NativeIconRestoreEnabled = enabled;
        nativeRestoreStatus = enabled ? "ネイティブ描画補正 ON（試験）" : "ネイティブ描画補正 OFF";
        Save();
    }

    // Resolve native ActionManager charge methods at runtime so different
    // FFXIVClientStructs versions can be supported without hardcoding signatures.
    // If a method is missing or has an unfamiliar signature, do not invent charges.
    private delegate int ChargeReader(nint manager, uint actionId, uint level);
    private ChargeReader? readCurrentCharges;
    private ChargeReader? readMaximumCharges;
    private bool chargeReadersResolved;
    private string chargeReadStatus = "未確認";

    private static ChargeReader? ResolveChargeReader(string methodName)
    {
        foreach (var method in typeof(ActionManager).GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                                   BindingFlags.Instance | BindingFlags.Static))
        {
            if (method.Name != methodName || method.ContainsGenericParameters) continue;
            var args = method.GetParameters();
            if (args.Length < 1 || args.Length > 2) continue;
            if (args.Any(a => a.IsOut || a.ParameterType.IsByRef ||
                              (a.ParameterType != typeof(uint) && a.ParameterType != typeof(int) &&
                               a.ParameterType != typeof(byte) && a.ParameterType != typeof(ushort)))) continue;
            if (method.ReturnType != typeof(uint) && method.ReturnType != typeof(int) &&
                method.ReturnType != typeof(byte) && method.ReturnType != typeof(ushort)) continue;
            try
            {
                var dm = new DynamicMethod("WPH_" + methodName, typeof(int),
                    new[] { typeof(nint), typeof(uint), typeof(uint) }, typeof(Plugin).Module, true);
                var il = dm.GetILGenerator();
                if (!method.IsStatic)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Conv_U);
                }
                for (int i = 0; i < args.Length; i++)
                {
                    il.Emit(i == 0 ? OpCodes.Ldarg_1 : OpCodes.Ldarg_2);
                    var t = args[i].ParameterType;
                    if (t == typeof(byte)) il.Emit(OpCodes.Conv_U1);
                    else if (t == typeof(ushort)) il.Emit(OpCodes.Conv_U2);
                    else if (t == typeof(int)) il.Emit(OpCodes.Conv_I4);
                }
                il.Emit(OpCodes.Call, method);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Ret);
                return (ChargeReader)dm.CreateDelegate(typeof(ChargeReader));
            }
            catch { /* A different API signature is not a fatal error. */ }
        }
        return null;
    }

    private void ResolveChargeReaders()
    {
        if (chargeReadersResolved) return;
        chargeReadersResolved = true;
        readCurrentCharges = ResolveChargeReader("GetCurrentCharges");
        readMaximumCharges = ResolveChargeReader("GetMaxCharges");
        chargeReadStatus = $"現在 { (readCurrentCharges != null ? "取得可能" : "未対応") } / 最大 { (readMaximumCharges != null ? "取得可能" : "未対応") }";
    }

    // Probe native HUD root scaling. This is NOT a texture mirror.
    // Do not touch native addon visibility or the alpha26 action/pulse path.
    private float nativeHotbarScale = 0f;
    private bool nativeHotbarVisible;
    private void PublishNativeHudProbe()
    {
        try
        {
            var addon = GetHotbarAddon(config.SelectedHotbar);
            var root = addon == null ? null : addon->AtkUnitBase.RootNode;
            float scale = root == null ? 0f : root->ScaleX;
            if (!float.IsFinite(scale) || scale < .25f || scale > 4f) scale = 0f;
            nativeHotbarScale = scale;
            nativeHotbarVisible = root != null && root->IsVisible();
            overlayView.Write(1124, scale);
            overlayView.Write(1128, root == null ? -1 : (nativeHotbarVisible ? 1 : 0));
            overlayView.Write(1132, config.SelectedHotbar);
            overlayView.Write(1136, 0x31445548); // HUD1 probe signature
        }
        catch
        {
            nativeHotbarScale = 0f;
            overlayView.Write(1136, 0);
        }
    }

    // alpha43: read native *slot interaction geometry*, not the whole addon bounds.
    // GetScaledWidth(true) on _ActionBar contains numbers, the lock icon and HUD
    // decorations. Dividing it by twelve produced the alpha42c over-large clone.
    // We only accept a repeatable cluster of >=10 similarly-sized, approximately
    // square collision nodes. Otherwise we leave manual sizing untouched.
    // No hooks are added and the measurement only runs after the alpha42c
    // delayed-login readiness check; no native nodes are stored across frames.
    private string nativeInternalSizeStatus = "純正ホットバーのスロット寸法取得待ち";
    private long lastNativeInternalSizeTick;
    private int lastNativeInternalSizeBar = -1;
    private string lastNativeInternalSizeLayout = "";
    private bool internalPanelMoveActive;
    private float internalDragRemainder;
    private int lastVerifiedSlotSize;
    private int repeatedVerifiedSamples;

    private static int NativeColumns(string layout) => layout switch
    {
        "6x2" => 6,
        "4x3" => 4,
        "3x4" => 3,
        "2x6" => 2,
        "1x12" => 1,
        _ => 12
    };

    private bool TryReadNativeInternalCellSize(out int measured, out string description,
        out bool verifiedSlotGeometry)
    {
        measured = 0;
        verifiedSlotGeometry = false;
        description = "純正ホットバーのスロット寸法が未取得";
        if (!clientState.IsLoggedIn || Volatile.Read(ref nativeObservationArmed) == 0)
        {
            description = "ログイン・HUD初期化を待機中";
            return false;
        }
        try
        {
            var addon = GetHotbarAddon(config.SelectedHotbar);
            if (addon == null || addon->AtkUnitBase.RootNode == null ||
                !addon->AtkUnitBase.RootNode->IsVisible())
            {
                description = $"ホットバー{config.SelectedHotbar}が未表示。測定中は純正HUDを表示してください";
                return false;
            }

            float addonScale = addon->AtkUnitBase.GetScale();
            if (!float.IsFinite(addonScale) || addonScale < .25f || addonScale > 4f)
            {
                description = "純正HUDの倍率を取得できません";
                return false;
            }

            // The native collision list contains the actual mouse-hit regions
            // for the hotbar's icon slots. Unlike the addon rectangle, they do
            // not include its number and padlock, although the list can also
            // contain extra UI nodes. Check counts/geometry before trusting it.
            var native = addon->AtkUnitBase;
            var nativeNodes = native.CollisionNodeList;
            var count = (int)native.CollisionNodeListCount;
            if (nativeNodes == null || count < 10 || count > 256)
            {
                description = $"純正枠のクリック領域不足 ({count}件)。手動サイズを維持します";
                return false;
            }

            var sizeCounts = new Dictionary<int, int>();
            var plausibleSquares = 0;
            for (var i = 0; i < count; i++)
            {
                var node = nativeNodes[i];
                if (node == null) continue;
                var sx = node->ScaleX;
                var sy = node->ScaleY;
                if (!float.IsFinite(sx) || !float.IsFinite(sy) ||
                    sx < .4f || sx > 3f || sy < .4f || sy > 3f) continue;
                float width = node->Width * sx * addonScale;
                float height = node->Height * sy * addonScale;
                if (!float.IsFinite(width) || !float.IsFinite(height) ||
                    width < 24f || width > 200f || height < 24f || height > 200f) continue;
                if (MathF.Abs(width - height) > MathF.Max(3f, MathF.Max(width, height) * .08f)) continue;
                var px = (int)MathF.Round((width + height) * .5f);
                plausibleSquares++;
                sizeCounts[px] = sizeCounts.GetValueOrDefault(px) + 1;
            }
            int strongestSize = 0;
            int strongestCount = 0;
            // Minor +/-1px rounding differences between adjacent slots are OK.
            foreach (var candidate in sizeCounts.Keys)
            {
                var agreeing = 0;
                foreach (var kv in sizeCounts)
                    if (Math.Abs(kv.Key - candidate) <= 2) agreeing += kv.Value;
                if (agreeing > strongestCount)
                {
                    strongestSize = candidate;
                    strongestCount = agreeing;
                }
            }
            if (strongestCount < 10)
            {
                description = $"純正クリック領域 {count}件 / 正方形 {plausibleSquares}件 / 同寸 {strongestCount}件。" +
                              "12枠を特定できないため自動調整しません（手動設定を維持）";
                return false;
            }
            measured = Math.Clamp(strongestSize, 24, 200);
            verifiedSlotGeometry = true;
            description = $"純正スロット領域 {measured}px / 同寸 {strongestCount}件 / " +
                          $"HUD倍率 {addonScale:0.00} / 総 {count}領域（枠寸法。アイコンの装飾差は残ります）";
            return true;
        }
        catch (Exception ex)
        {
            description = $"純正枠測定エラー: {ex.GetType().Name}。手動サイズを維持";
            return false;
        }
    }

    private void ApplyManualInternalCellSize(int size)
    {
        var next = Math.Clamp(size, 24, 200);
        if (config.InternalHotbarCellSize == next && !config.InternalHotbarAutoSize) return;
        config.InternalHotbarCellSize = next;
        // Manual size ALWAYS wins, including on lock and after relogging.
        config.InternalHotbarAutoSize = false;
        nativeInternalSizeStatus = $"手動設定 {next}px/枠（自動追従 OFF）";
        Save();
    }

    // alpha45: native HUD cell dimensions do not describe the *pitch* between
    // adjacent icons. Keep the inter-slot gap independent of cell size and of
    // the optional experimental native cell-size probe.
    private void ApplyManualInternalCellGap(int gap)
    {
        var next = Math.Clamp(gap, 0, 20);
        if (config.InternalHotbarCellGap == next) return;
        config.InternalHotbarCellGap = next;
        Save();
    }

    private void RefreshNativeInternalHotbarSize()
    {
        if (!config.InternalHotbarEnabled || !config.InternalHotbarAutoSize) return;
        var now = Environment.TickCount64;
        if (now - lastNativeInternalSizeTick < 1500 &&
            lastNativeInternalSizeBar == config.SelectedHotbar &&
            lastNativeInternalSizeLayout == config.OverlayLayout) return;
        lastNativeInternalSizeTick = now;
        lastNativeInternalSizeBar = config.SelectedHotbar;
        lastNativeInternalSizeLayout = config.OverlayLayout;
        if (!TryReadNativeInternalCellSize(out var size, out nativeInternalSizeStatus,
                out var verified) || !verified)
        {
            lastVerifiedSlotSize = 0;
            repeatedVerifiedSamples = 0;
            return;
        }
        if (size != lastVerifiedSlotSize)
        {
            lastVerifiedSlotSize = size;
            repeatedVerifiedSamples = 1;
            nativeInternalSizeStatus += " / 次の一致測定で反映";
            return;
        }
        repeatedVerifiedSamples++;
        if (repeatedVerifiedSamples < 2 || config.InternalHotbarCellSize == size) return;
        config.InternalHotbarCellSize = size;
        Save();
    }

    // alpha46: Retain each equipped item's native/sheet icon independently from
    // Wrath's action replacement icon cache. Only reuse it for the *same* item.
    private readonly uint[] itemIconLastValid = new uint[12];
    private readonly uint[] itemIdLastValid = new uint[12];
    // alpha47: Keep the two independent icon sources for each actual item slot.
    // A HotbarSlot.IconId can be a transient or non-texture value while an item
    // is on recast. An Item-sheet icon must remain available as a fallback.
    private readonly uint[] itemSheetIcon = new uint[12];
    private readonly uint[] itemNativeIcon = new uint[12];
    private readonly uint[] itemLookupId = new uint[12];
    private readonly string[] itemTextureStatus = new string[12];

    // FF14 stores multi-charge recasts as one gauge spanning every charge.
    // The overlay must show the time until the NEXT charge, not until all
    // charges have returned. This is a pure conversion of game-owned data.
    // Does not touch the game slot, ability execution, or startup hooks.
    private static bool TryGetNextChargeRecast(float fullDuration, float elapsed,
        int currentCharges, int maxCharges, out float remaining, out float oneChargeDuration)
    {
        remaining = 0f;
        oneChargeDuration = 0f;
        if (maxCharges < 2 || currentCharges < 0 || currentCharges > maxCharges ||
            !float.IsFinite(fullDuration) || !float.IsFinite(elapsed) ||
            fullDuration <= 0f || elapsed < 0f)
            return false;

        oneChargeDuration = fullDuration / maxCharges;
        if (!float.IsFinite(oneChargeDuration) || oneChargeDuration < 0.1f) return false;
        if (currentCharges >= maxCharges) return true;

        // The game recast gauge includes credit for existing charges.
        // Example: max=2, total=110s, elapsed=30s -> next in 25s.
        // max=2, total=110s, elapsed=80s -> next in 30s.
        var phase = Math.Clamp(elapsed, 0f, fullDuration) % oneChargeDuration;
        remaining = phase < 0.001f ? oneChargeDuration : oneChargeDuration - phase;
        remaining = Math.Clamp(remaining, 0f, oneChargeDuration);
        return true;
    }

    private void PublishOverlaySlots(RaptureHotbarModule* module)
    {
        // Native addon tree reads stay behind the original 14s hook guard;
        // quick start uses only ModuleReady-gated action/slot snapshots.
        if (Volatile.Read(ref nativeObservationArmed) != 0)
            PublishNativeHudProbe();
        var am = ActionManager.Instance();
        var valid = 0;
        var changed = 0;
        for (var i = 0; i < 12; i++)
        {
            uint original = 0, adjusted = 0, icon = 0;
            var slot = module->GetSlotById((uint)(config.SelectedHotbar - 1), (uint)i);
            if (slot != null)
            {
                original = slot->CommandId;
                adjusted = original;
                try
                {
                    switch (slot->CommandType)
                    {
                        case RaptureHotbarModule.HotbarSlotType.Action:
                            if (original != 0)
                            {
                                // Diagnostic option, not a verified WrathCombo final action.
                                if (config.OverlayUseAdjustedIds && am != null)
                                    adjusted = am->GetAdjustedActionId(original);
                                var row = data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(adjusted);
                                if (row is { } a) icon = a.Icon;
                            }
                            break;
                        case RaptureHotbarModule.HotbarSlotType.Item:
                            // alpha47: Sheet icon first, because native HotbarSlot.IconId
                            // sometimes names a render placeholder instead of a loadable
                            // game icon. Preserve BOTH for the draw-time texture fallback.
                            var itemId = original % 1_000_000;
                            var item = data.GetExcelSheet<Item>()?.GetRow(itemId);
                            var sheetIcon = item is { } it ? unchecked((uint)it.Icon) : 0u;
                            itemSheetIcon[i] = sheetIcon;
                            itemNativeIcon[i] = slot->IconId;
                            itemLookupId[i] = original;
                            icon = sheetIcon != 0 ? sheetIcon : slot->IconId;
                            break;
                        case RaptureHotbarModule.HotbarSlotType.GeneralAction:
                            var general = data.GetExcelSheet<GeneralAction>()?.GetRow(original);
                            if (general is { } g) icon = unchecked((uint)g.Icon);
                            if (icon == 0 && original == 4)
                            {
                                var sprint = data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(3);
                                if (sprint is { } sprintAction) icon = sprintAction.Icon;
                            }
                            break;
                    }
                }
                catch (Exception e) { log.Debug($"Icon slot {i + 1} lookup: {e.Message}"); }
            }
            // Alpha46: native action-replacement icons are for ACTION slots.
            // Do not let a stale/native action icon override the real item picture.
            bool isItemSlot = slot != null &&
                slot->CommandType == RaptureHotbarModule.HotbarSlotType.Item && original != 0;
            if (isItemSlot)
            {
                if (icon != 0)
                {
                    itemIconLastValid[i] = icon;
                    itemIdLastValid[i] = original;
                }
                else if (itemIdLastValid[i] == original)
                    icon = itemIconLastValid[i];
            }
            else
            {
                itemIconLastValid[i] = 0;
                itemIdLastValid[i] = 0;
                itemSheetIcon[i] = 0;
                itemNativeIcon[i] = 0;
                itemLookupId[i] = 0;
                itemTextureStatus[i] = "";
            }
            if (!isItemSlot && config.OverlayNativeMirrorEnabled &&
                TryGetNativeMirror(config.SelectedHotbar - 1, i, original, out var nativeSample) &&
                nativeSample.IconId != 0)
            {
                var nativeIcon = nativeSample.IconId;
                if (config.InternalHotbarEnabled || exportedIcons.Contains(nativeIcon) || TryExportIcon(nativeIcon))
                {
                    exportedIcons.Add(nativeIcon);
                    icon = nativeIcon;
                    if (nativeSample.ActionId != 0) adjusted = nativeSample.ActionId;
                }
            }
            if (adjusted != lastOverlayPublishedIds[i])
            {
                if (lastOverlayPublishedIds[i] != 0) changed++;
                lastOverlayPublishedIds[i] = adjusted;
            }
            if (icon != 0)
            {
                valid++;
                if (!config.InternalHotbarEnabled && exportedIcons.Add(icon) && !TryExportIcon(icon)) exportedIcons.Remove(icon);
            }
            // IIT1: item count and slot kind. A -1 count means non-item or unavailable.
            // Item hotbar IDs use a 1,000,000 offset for HQ variants.
            int inventoryCount = -1;
            int overlaySlotKind = 0;
            if (slot != null && original != 0)
            {
                if (slot->CommandType == RaptureHotbarModule.HotbarSlotType.Item)
                {
                    overlaySlotKind = 1;
                    try
                    {
                        var inventory = InventoryManager.Instance();
                        if (inventory != null)
                        {
                            uint itemId = original % 1_000_000;
                            bool isHq = original >= 1_000_000;
                            inventoryCount = Math.Max(0, inventory->GetInventoryItemCount(
                                itemId, isHq, false, false));
                        }
                    }
                    catch (Exception ex)
                    {
                        if (i == 0) log.Debug($"Overlay item count read failed: {ex.Message}");
                    }
                }
                else if (IsSprintSlot(slot->CommandType, original))
                    overlaySlotKind = 2;
            }
            overlayView.Write(1024 + i * 8, inventoryCount);
            overlayView.Write(1028 + i * 8, overlaySlotKind);

            // alpha27: native recast of the displayed action on the selected hotbar.
            // Never touch other hotbars or action execution. The native API
            // determines recast groups, including shared GCD and ability timers.
            float recastRemaining = 0f;
            float recastTotal = 0f;
            float recastElapsedForCharges = 0f;
            if (config.OverlayCooldownEnabled && am != null && slot != null && original != 0)
            {
                try
                {
                    ActionType recastType = ActionType.Action;
                    uint recastId = adjusted;
                    bool supported = true;
                    if (slot->CommandType == RaptureHotbarModule.HotbarSlotType.Item)
                    {
                        recastType = ActionType.Item;
                        recastId = original % 1_000_000;
                    }
                    else if (IsSprintSlot(slot->CommandType, original))
                    {
                        // GeneralAction sprint (ID 4) resolves to native Action ID 3.
                        recastType = ActionType.Action;
                        recastId = 3;
                    }
                    else if (slot->CommandType != RaptureHotbarModule.HotbarSlotType.Action)
                        supported = false;

                    float elapsed = 0f;
                    if (supported && recastId != 0)
                    {
                        recastTotal = am->GetRecastTime(recastType, recastId);
                        elapsed = am->GetRecastTimeElapsed(recastType, recastId);
                        // Some item/general actions are exposed through the native
                        // hotbar cooldown resolver instead of direct recast queries.
                        // Prefer the native active cooldown when direct values are absent.
                        if ((recastTotal <= 0f || elapsed >= recastTotal) && overlaySlotKind != 0)
                        {
                            HotbarCooldownInfo nativeInfo = default;
                            am->GetHotbarCooldownInfo(&nativeInfo, recastType, recastId);
                            if (nativeInfo.CooldownActive && nativeInfo.PrimaryTotalTime > 0f &&
                                nativeInfo.PrimaryElapsedTime < nativeInfo.PrimaryTotalTime)
                            {
                                recastTotal = nativeInfo.PrimaryTotalTime;
                                elapsed = nativeInfo.PrimaryElapsedTime;
                            }
                        }
                    }
                    recastElapsedForCharges = elapsed;
                    if (!float.IsFinite(recastTotal) || !float.IsFinite(elapsed) ||
                        recastTotal <= 0f || elapsed >= recastTotal)
                    {
                        recastRemaining = 0f;
                        recastTotal = 0f;
                    }
                    else
                        recastRemaining = Math.Clamp(recastTotal - Math.Max(0f, elapsed), 0f, 3600f);
                }
                catch (Exception ex)
                {
                    recastRemaining = 0f;
                    recastTotal = 0f;
                    if (i == 0) log.Debug($"Overlay recast read failed: {ex.Message}");
                }
            }
            // CHG1 extension at 904..999: (current,max) int32 per slot.
            // A 0/0 pair means charge information is not available.
            int currentCharges = 0, maxCharges = 0;
            if (am != null && slot != null &&
                slot->CommandType == RaptureHotbarModule.HotbarSlotType.Action && adjusted != 0)
            {
                try
                {
                    ResolveChargeReaders();
                    uint level = playerState.IsLoaded ? (uint)Math.Max(1, (int)playerState.EffectiveLevel) : 0u;
                    if (readMaximumCharges != null)
                        maxCharges = Math.Clamp(readMaximumCharges((nint)am, adjusted, level), 0, 10);
                    if (maxCharges > 1 && readCurrentCharges != null)
                        currentCharges = Math.Clamp(readCurrentCharges((nint)am, adjusted, level), 0, maxCharges);
                    else
                        maxCharges = 0;
                }
                catch (Exception ex)
                {
                    currentCharges = maxCharges = 0;
                    chargeReadStatus = $"取得失敗: {ex.GetType().Name}";
                }
            }
            overlayView.Write(904 + i * 8, currentCharges);
            overlayView.Write(908 + i * 8, maxCharges);

            // Match the game's next-charge countdown. Previous versions used
            // the remaining time until FULL recharge (e.g. ~90s for two spent
            // Acceleration charges), even when the native hotbar showed ~35s.
            // Only normalize known multi-charge ACTION slots. Items, ordinary
            // abilities and Sprint continue on the existing native path.
            if (config.OverlayCooldownEnabled && slot != null &&
                slot->CommandType == RaptureHotbarModule.HotbarSlotType.Action &&
                recastTotal > 0f && maxCharges > 1 &&
                TryGetNextChargeRecast(recastTotal, recastElapsedForCharges,
                    currentCharges, maxCharges, out var nextCharge, out var perCharge))
            {
                recastTotal = perCharge;
                recastRemaining = nextCharge;
            }

            // CDR1 extension: 12 x (remaining,total) float32 pairs at 792..887.
            overlayView.Write(792 + i * 8, recastRemaining);
            overlayView.Write(796 + i * 8, recastTotal);
            var offset = 516 + i * 12;
            overlayView.Write(offset, original);
            overlayView.Write(offset + 4, adjusted);
            overlayView.Write(offset + 8, icon);
        }
        PublishNativeMirror(module);
        liveChangedSlots += changed;
        liveOverlayStatus = config.OverlayUseAdjustedIds
            ? $"ゲーム調整IDで更新中 / 今回変化 {changed} / 累計 {liveChangedSlots} (本家WrathComboの最終結果とは未確認)"
            : "登録元IDを表示中（リアルタイム置換はOFF）";
        iconStatus = $"アイコンID取得 {valid}/12、書き出し失敗 {iconExportFailures} 回";
        // The legacy overlay validates these header fields before reading VHB1.
        var odd = (overlaySequence += 2) - 1;
        overlayView.Write(4, odd);
        overlayView.Write(8, 0u);
        overlayView.Write(12, 0u);
        overlayView.Write(16, DateTime.UtcNow.Ticks);
        overlayView.Write(24, 0);
        overlayView.Write(272, 0u);
        overlayView.Write(276, 0u);
        overlayView.Write(280, 0);
        overlayView.Write(512, 0x31424856);
        overlayView.Write(4, odd + 1);
    }


    // A separate, manually aligned, click-through window covers only one
    // non-selected bar. This does not alter WrathCombo or game action execution.
    private void PublishRestoreSlots(RaptureHotbarModule* module)
    {
        var hb = Math.Clamp(config.RestoreHotbar, 1, 10) - 1;
        for (var i = 0; i < 12; i++)
        {
            uint original = 0, icon = 0;
            var slot = module->GetSlotById((uint)hb, (uint)i);
            if (slot != null)
            {
                original = slot->CommandId;
                try
                {
                    switch (slot->CommandType)
                    {
                        case RaptureHotbarModule.HotbarSlotType.Action:
                            if (data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(original) is { } a) icon = a.Icon;
                            break;
                        case RaptureHotbarModule.HotbarSlotType.Item:
                            if (data.GetExcelSheet<Item>()?.GetRow(original % 1_000_000) is { } it) icon = it.Icon;
                            break;
                        case RaptureHotbarModule.HotbarSlotType.GeneralAction:
                            if (data.GetExcelSheet<GeneralAction>()?.GetRow(original) is { } g) icon = unchecked((uint)g.Icon);
                            if (icon == 0 && original == 4 && data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(3) is { } sprint) icon = sprint.Icon;
                            break;
                    }
                }
                catch (Exception e) { log.Debug($"Restore slot {i+1}: {e.Message}"); }
            }
            if (icon != 0 && exportedIcons.Add(icon) && !TryExportIcon(icon)) exportedIcons.Remove(icon);
            overlayView.Write(680 + i * 8, original);
            overlayView.Write(684 + i * 8, icon);
        }
        overlayView.Write(780, 0x31545352); // RST1
        overlayView.Write(784, hb + 1);
    }

    private void SyncRestoreOverlay()
    {
        if (!config.RestoreOverlayEnabled || !clientState.IsLoggedIn || config.RestoreHotbar == config.SelectedHotbar)
        {
            StopRestoreOverlay();
            return;
        }
        try
        {
            if (restoreProcess is { HasExited: false }) { restoreStatus = $"起動中 (PID {restoreProcess.Id})"; return; }
            restoreProcess?.Dispose();
            restoreProcess = null;
            var path = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName ?? "", "PrivateOverlay.exe");
            if (!File.Exists(path)) { restoreStatus = "PrivateOverlay.exe が見つかりません"; return; }
            restoreProcess = Process.Start(new ProcessStartInfo(path, "--restore")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)!
            });
            restoreStatus = restoreProcess == null ? "起動失敗" : $"起動中 (PID {restoreProcess.Id})";
        }
        catch (Exception e) { restoreStatus = $"起動失敗: {e.Message}"; }
    }

    private void StopRestoreOverlay()
    {
        try
        {
            if (restoreProcess != null)
            {
                if (!restoreProcess.HasExited) restoreProcess.Kill();
                restoreProcess.Dispose();
                restoreProcess = null;
            }
            restoreStatus = "停止中";
        }
        catch (Exception e) { restoreStatus = $"停止失敗: {e.Message}"; }
    }

    private void ClearOverlaySlots()
    {
        for (var i = 0; i < 12; i++)
        {
            var offset = 516 + i * 12;
            overlayView.Write(offset, 0u);
            overlayView.Write(offset + 4, 0u);
            overlayView.Write(offset + 8, 0u);
        }
        for (var i = 0; i < 12; i++)
        {
            overlayView.Write(792 + i * 8, 0f);
            overlayView.Write(796 + i * 8, 0f);
        }
        overlayView.Write(888, 0);
        overlayView.Write(16, 0L);
        // alpha36b: Old renderer samples must not be reused during login.
        overlayView.Write(NativeMirrorHeader, 0);
        overlayView.Write(NativeMirrorHeader + 8, 0);
        overlayView.Write(PulseDisplayBase + 4, -1);
        overlayView.Write(PulseDisplayBase + 8, 0L);
    }

    private bool TryExportIcon(uint iconId)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WrathPrivateHotbar", "icons");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"{iconId}.png");
            if (File.Exists(file)) return true;
            var group = (iconId / 1000) * 1000;
            var tex = data.GetFile<TexFile>($"ui/icon/{group:000000}/{iconId:000000}.tex");
            if (tex == null) { iconExportFailures++; return false; }
            PngWriter.WriteRgba(file, tex.Header.Width, tex.Header.Height, tex.GetRgbaImageData());
            return true;
        }
        catch (Exception e) { iconExportFailures++; log.Debug($"Icon export {iconId}: {e.Message}"); return false; }
    }

    // Manages ONLY the child process launched by this plugin. Never kills an unrelated overlay.
    private void SyncOverlay()
    {
        if (Environment.TickCount64 - lastOverlayCheck < 1000) return;
        lastOverlayCheck = Environment.TickCount64;
        if (config.InternalHotbarEnabled || !config.OverlayEnabled || !clientState.IsLoggedIn)
        {
            StopOverlay();
            return;
        }
        try
        {
            if (overlayProcess is { HasExited: false })
            {
                overlayStatus = $"起動中 (PID {overlayProcess.Id})";
                return;
            }
            overlayProcess?.Dispose();
            overlayProcess = null;
            var path = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName ?? "", "PrivateOverlay.exe");
            if (!File.Exists(path))
            {
                overlayStatus = "PrivateOverlay.exe がDLLと同じフォルダーにありません";
                return;
            }
            overlayProcess = Process.Start(new ProcessStartInfo(path, $"--game-pid {Environment.ProcessId} --click-map {clickMapName}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)!
            });
            overlayStatus = overlayProcess == null ? "起動に失敗" : $"起動中 (PID {overlayProcess.Id})";
        }
        catch (Exception e) { overlayStatus = $"起動失敗: {e.GetType().Name}: {e.Message}"; }
    }

    private void StopOverlay()
    {
        try
        {
            if (overlayProcess != null)
            {
                if (!overlayProcess.HasExited) overlayProcess.Kill();
                overlayProcess.Dispose();
                overlayProcess = null;
            }
        }
        catch (Exception e) { overlayStatus = $"終了失敗: {e.Message}"; return; }
        overlayStatus = "停止中";
    }


    // NPC dialogue, cutscene, and quest/event controls: read-only checks on Framework.Update.
    // Hide ONLY our ImGui hotbar; never mutate FF14's native HUD or event UI.
    // Separate user toggles allow cutscenes to be hidden even when dialogue hiding is off.
    private static readonly string[] NpcDialogueAddons =
    [
        "Talk", "SelectString", "SelectIconString", "SelectYesno",
        "SelectOk", "CutSceneSelectString"
    ];
    private int hideForScreenEvent;
    private long lastNpcDialogueVisibleTick;
    private long lastCutsceneVisibleTick;
    private long lastScreenEventWarningTick;
    // v2.0.5.7: Learn a vanilla hotbar which is actually visible in normal play.
    // Never mirror a bar hidden by WrathPrivateHotbar itself (usually HB1):
    // its RootNode is intentionally kept hidden and would suppress our clone forever.
    // Store a hotbar NUMBER, not an unmanaged addon pointer; the game rebuilds
    // addon instances on teleports and HUD layout changes.
    private int nativeHudVisibilityReferenceBar;

    // -1: no suitable unmodified vanilla hotbar to observe
    //  0: the previously visible native hotbar is now hidden/unavailable
    //  1: the same native hotbar is visible again
    private int ObserveVanillaHudVisibility(bool maySelectReference)
    {
        var previousReference = nativeHudVisibilityReferenceBar;
        try
        {
            if (nativeHudVisibilityReferenceBar > 0)
            {
                if (locallyHiddenHotbars.Contains(nativeHudVisibilityReferenceBar) ||
                    nativeHudVisibilityReferenceBar == config.SelectedHotbar)
                {
                    nativeHudVisibilityReferenceBar = 0;
                }
                else
                {
                    var reference = GetHotbarAddon(nativeHudVisibilityReferenceBar);
                    if (reference != null && reference->AtkUnitBase.RootNode != null &&
                        reference->AtkUnitBase.IsVisible &&
                        reference->AtkUnitBase.RootNode->IsVisible())
                        return 1;
                    if (!maySelectReference) return 0; // Hidden during a transition.
                    // HUD layouts may change or an addon may get replaced on
                    // zone load. During ordinary play, prefer another actually
                    // visible, unmodified reference rather than hiding forever.
                    nativeHudVisibilityReferenceBar = 0;
                }
            }

            if (!maySelectReference) return -1;
            for (var bar = 1; bar <= 10; bar++)
            {
                if (bar == config.SelectedHotbar || locallyHiddenHotbars.Contains(bar))
                    continue;
                var addon = GetHotbarAddon(bar);
                if (addon == null || addon->AtkUnitBase.RootNode == null ||
                    !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.RootNode->IsVisible())
                    continue;
                nativeHudVisibilityReferenceBar = bar;
                return 1;
            }
        }
        catch (Exception ex)
        {
            if (Environment.TickCount64 - lastScreenEventWarningTick > 10000)
            {
                lastScreenEventWarningTick = Environment.TickCount64;
                log.Debug($"Native HUD visibility probe unavailable: {ex.GetType().Name}");
            }
        }
        // If a previously observed native hotbar is now gone/hidden and no
        // replacement is visible, keep following that hidden state.
        if (previousReference > 0 &&
            !locallyHiddenHotbars.Contains(previousReference) &&
            previousReference != config.SelectedHotbar)
        {
            nativeHudVisibilityReferenceBar = previousReference;
            return 0;
        }
        return -1; // No native reference; use event/loading conditions.
    }

    private void UpdateScreenEventVisibility(bool uiReady)
    {
        if (!uiReady || !clientState.IsLoggedIn)
        {
            Volatile.Write(ref hideForScreenEvent, 0);
            lastNpcDialogueVisibleTick = 0;
            lastCutsceneVisibleTick = 0;
            nativeHudVisibilityReferenceBar = 0;
            return;
        }
        // An event may unload the local player briefly without ending the
        // cutscene. Do not show the pseudo-hotbar during that transition.
        if (!playerState.IsLoaded)
        {
            Volatile.Write(ref hideForScreenEvent, 1);
            return;
        }

        var now = Environment.TickCount64;
        bool npcVisible = false;
        if (config.HideDuringNpcDialogue)
        {
            try
            {
                foreach (var name in NpcDialogueAddons)
                {
                    var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
                    if (addon == null || addon->RootNode == null ||
                        !addon->RootNode->IsVisible()) continue;
                    npcVisible = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                if (now - lastScreenEventWarningTick > 10000)
                {
                    lastScreenEventWarningTick = now;
                    log.Debug($"NPC visibility check unavailable: {ex.GetType().Name}");
                }
            }
            if (npcVisible) lastNpcDialogueVisibleTick = now;
        }
        else lastNpcDialogueVisibleTick = 0;

        // The game's cutscene flags cover full-screen movies, non-dialogue
        // event camera sequences, and transitions that have no Talk addon.
        // Dalamud's UiBuilder.CutsceneActive alone does not include flag 58,
        // so check both the official signal and all corresponding flags.
        bool cutsceneVisible = false;
        if (config.HideDuringCutscenes)
        {
            try
            {
                cutsceneVisible = pluginInterface.UiBuilder.CutsceneActive ||
                    condition[ConditionFlag.WatchingCutscene] ||
                    condition[ConditionFlag.WatchingCutscene78] ||
                    condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                    condition[ConditionFlag.OccupiedInEvent] ||
                    condition[ConditionFlag.OccupiedInQuestEvent];
            }
            catch (Exception ex)
            {
                if (now - lastScreenEventWarningTick > 10000)
                {
                    lastScreenEventWarningTick = now;
                    log.Debug($"Cutscene/event visibility check unavailable: {ex.GetType().Name}");
                }
            }
            if (cutsceneVisible) lastCutsceneVisibleTick = now;
        }
        else lastCutsceneVisibleTick = 0;

        // v2.0.5.7: Follow the game's HUD, not a fixed half-second timeout.
        // GameUiHidden covers the actual UI hide control; BetweenAreas flags
        // cover zone-change black screens that cutscene flags often miss.
        // A known-visible, *unmodified* native action bar is an additional
        // read-only reference for FF14's own hide/show behavior. An unobserved
        // reference does not suppress the overlay (fallback to conditions).
        bool gameUiHidden = false;
        bool betweenAreas = false;
        try
        {
            gameUiHidden = gameGui.GameUiHidden;
            betweenAreas = condition[ConditionFlag.BetweenAreas] ||
                condition[ConditionFlag.BetweenAreas51];
        }
        catch (Exception ex)
        {
            if (now - lastScreenEventWarningTick > 10000)
            {
                lastScreenEventWarningTick = now;
                log.Debug($"HUD loading check unavailable: {ex.GetType().Name}");
            }
        }

        // Learn a reference in normal play. Reuse it across the transition,
        // resolving the addon fresh each update because it can be rebuilt.
        var maySelectReference = !cutsceneVisible && !npcVisible &&
            !gameUiHidden && !betweenAreas;
        int nativeHudState = config.HideDuringCutscenes
            ? ObserveVanillaHudVisibility(maySelectReference) : -1;

        // The native bar returning is the earliest useful SHOW signal. Do not
        // wait 500ms when its HUD is already visible. Without a usable reference,
        // keep a tiny 80ms fallback for event-addon transitions.
        bool dialogueHold = lastNpcDialogueVisibleTick != 0 &&
            now - lastNpcDialogueVisibleTick < 200;
        bool cutsceneHold = lastCutsceneVisibleTick != 0 &&
            now - lastCutsceneVisibleTick < 80;
        bool hideForCutscene = config.HideDuringCutscenes &&
            (betweenAreas || nativeHudState == 0 ||
             (nativeHudState != 1 && (cutsceneVisible || cutsceneHold)));
        Volatile.Write(ref hideForScreenEvent,
            gameUiHidden || npcVisible || dialogueHold || hideForCutscene ? 1 : 0);
    }

    // ---- alpha41: in-process Dalamud/ImGui renderer ----
    // No auxiliary window/process or injected keypresses. On every frame the
    // UI reads managed/MMF snapshots already produced by alpha40; the native
    // hotbar is touched exclusively from the existing framework.Update path.
    // Clicks use the SAME alpha40 channel and same guarded ExecuteSlotById
    // dispatch + cross-hotbar pulse logic, rather than duplicating it here.
    private string internalDrawError = "";
    private long lastInternalDrawErrorTick;

    // A real mouse click also creates a short press animation; unlike the
    // automatic key pulse this is tied to ImGui input.
    private readonly long[] internalClickPressTicks = new long[12];

    private void QueueInternalSlotClick(int index)
    {
        if (index is < 0 or >= 12 || !config.InternalHotbarEnabled ||
            !config.OverlayClickActions || !IsGameForeground() ||
            Volatile.Read(ref hideForScreenEvent) != 0) return;
        internalClickPressTicks[index] = Environment.TickCount64;
        // Write data first, commit sequence last. Consume in OnUpdate, not in Draw.
        var current = clickView.ReadInt32(4);
        var next = current is >= int.MaxValue - 2 or < 0 ? 1 : current + 1;
        clickView.Write(8, index);
        clickView.Write(12, config.SelectedHotbar);
        clickView.Write(16, DateTime.UtcNow.Ticks);
        clickView.Write(4, next);
    }

    private void DrawInternalHotbar()
    {
        if (!config.InternalHotbarEnabled || !clientState.IsLoggedIn ||
            Volatile.Read(ref earlyHotbarReady) == 0 || !IsGameForeground() ||
            Volatile.Read(ref hideForScreenEvent) != 0)
            return;
        try
        {
            DrawInternalHotbarCore();
        }
        catch (Exception ex)
        {
            // UI render errors should not crash the game or flood the log.
            if (Environment.TickCount64 - lastInternalDrawErrorTick > 5000)
            {
                lastInternalDrawErrorTick = Environment.TickCount64;
                internalDrawError = ex.GetType().Name + ": " + ex.Message;
                log.Warning("Internal hotbar draw failed: " + internalDrawError);
            }
        }
    }

    private static void DrawPrivateLockGlyph(ImDrawListPtr dl, Vector2 position,
        bool locked, uint color)
    {
        var x = position.X;
        var y = position.Y;
        // Small drawn glyph rather than a platform-dependent emoji font.
        dl.AddRectFilled(new Vector2(x + 7f, y + 13f),
            new Vector2(x + 19f, y + 22f), color);
        var shackleLeft = locked ? x + 9f : x + 11f;
        dl.AddLine(new Vector2(shackleLeft, y + 14f),
            new Vector2(shackleLeft, y + 8f), color, 2f);
        dl.AddLine(new Vector2(shackleLeft, y + 8f),
            new Vector2(x + 17f, y + 8f), color, 2f);
        if (locked)
            dl.AddLine(new Vector2(x + 17f, y + 8f),
                new Vector2(x + 17f, y + 14f), color, 2f);
    }

    // alpha47: Use one outlined, scaled text routine for native-like counters
    // and cooldowns. ImGui's standard font is not the FFXIV font; matching its
    // on-screen bounding box/placement is the goal of this pass.
    private static void DrawOutlinedText(ImDrawListPtr dl, string text,
        Vector2 pos, float fontSize, uint color, float thickness)
    {
        var font = ImGui.GetFont();
        const uint stroke = 0xFF070707u;
        dl.AddText(font, fontSize, pos + new Vector2(-thickness, 0f), stroke, text);
        dl.AddText(font, fontSize, pos + new Vector2(thickness, 0f), stroke, text);
        dl.AddText(font, fontSize, pos + new Vector2(0f, -thickness), stroke, text);
        dl.AddText(font, fontSize, pos + new Vector2(0f, thickness), stroke, text);
        dl.AddText(font, fontSize, pos + new Vector2(-thickness, -thickness), stroke, text);
        dl.AddText(font, fontSize, pos + new Vector2(thickness, thickness), stroke, text);
        dl.AddText(font, fontSize, pos, color, text);
    }

    private static Vector2 MeasureOverlayText(string value, float fontSize)
    {
        return ImGui.CalcTextSize(value) * (fontSize / Math.Max(1f, ImGui.GetFontSize()));
    }

    private static void DrawNativeHotbarCounter(ImDrawListPtr dl, Vector2 slotMax,
        int cell, string text, bool item)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Native skill charges should be much larger than our old .40 scale.
        float fontSize = Math.Clamp(cell * (item ? .37f : .66f), 18f, 110f);
        var bounds = MeasureOverlayText(text, fontSize);
        float rightInset = Math.Max(1f, cell * .018f);
        var pos = new Vector2(slotMax.X - bounds.X - rightInset,
            slotMax.Y - bounds.Y - Math.Max(1f, cell * .005f));
        DrawOutlinedText(dl, text, pos, fontSize, 0xFFF8F8F8u,
            Math.Clamp(cell * .025f, 1.4f, 3.2f));
    }

    private static void DrawNativeCooldownText(ImDrawListPtr dl, Vector2 min,
        Vector2 max, int cell, float remaining)
    {
        // Vanilla displays cooldown as a large CENTERED number; the number
        // of digits changes its width, so fit to a slot rather than clip it.
        var text = remaining >= 10f
            ? Math.Ceiling(remaining).ToString("0")
            : remaining.ToString("0.0");
        float fontSize = Math.Clamp(cell * .75f, 20f, 130f);
        var bounds = MeasureOverlayText(text, fontSize);
        float maxWidth = cell - Math.Max(4f, cell * .07f);
        if (bounds.X > maxWidth && bounds.X > 0f)
        {
            fontSize *= maxWidth / bounds.X;
            bounds = MeasureOverlayText(text, fontSize);
        }
        // The -1px optical Y adjustment keeps the digits optically centered.
        var pos = min + new Vector2((cell - bounds.X) / 2f,
            (cell - bounds.Y) / 2f - Math.Max(0f, cell * .01f));
        DrawOutlinedText(dl, text, pos, fontSize, 0xFFFFFFFFu,
            Math.Clamp(cell * .035f, 1.5f, 4f));
    }

    // An item's image must be resolved independently of its cooldown.
    // Read-only: requests game textures only, never executes an action or changes
    // the original HotbarSlot. False means the requested texture is still pending.
    private bool DrawItemIconWithFallback(ImDrawListPtr dl, int index,
        uint storedId, uint publishedIcon, Vector2 min, Vector2 max)
    {
        uint sheet = itemLookupId[index] == storedId ? itemSheetIcon[index] : 0u;
        uint native = itemLookupId[index] == storedId ? itemNativeIcon[index] : 0u;
        uint cached = itemIdLastValid[index] == storedId ? itemIconLastValid[index] : 0u;
        var candidates = new[] { sheet, native, publishedIcon, cached };
        var hq = storedId >= 1_000_000;
        // HQ lookup first where relevant, then base icon. Some HQ textures have
        // no dedicated texture even when the base icon exists.
        foreach (var candidate in candidates)
        {
            if (candidate == 0) continue;
            for (int variant = 0; variant < (hq ? 2 : 1); variant++)
            {
                var useHq = hq && variant == 0;
                try
                {
                    var shared = textureProvider.GetFromGameIcon(
                        new GameIconLookup { IconId = candidate, ItemHq = useHq });
                    if (shared.TryGetWrap(out var wrap, out _) && wrap != null)
                    {
                        dl.AddImage(wrap.Handle, min + new Vector2(1f), max - new Vector2(1f));
                        itemTextureStatus[index] = $"OK: icon={candidate} HQ={(useHq ? 1 : 0)}";
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    itemTextureStatus[index] = $"ID={storedId} icon={candidate} {ex.GetType().Name}";
                }
            }
            // Last resort: directly request the game's neutral .tex path.
            // This uses a separate API entry point from GameIconLookup; it
            // covers cases where icon variant resolution itself is failing.
            try
            {
                var iconGroup = (candidate / 1000) * 1000;
                var gamePath = $"ui/icon/{iconGroup:000000}/{candidate:000000}.tex";
                var shared = textureProvider.GetFromGame(gamePath);
                if (shared.TryGetWrap(out var wrap, out _) && wrap != null)
                {
                    dl.AddImage(wrap.Handle, min + new Vector2(1f), max - new Vector2(1f));
                    itemTextureStatus[index] = $"OK direct: icon={candidate}";
                    return true;
                }
            }
            catch { /* retain diagnosis from lookup attempts */ }
        }
        if (string.IsNullOrEmpty(itemTextureStatus[index]) || itemTextureStatus[index].StartsWith("OK"))
            itemTextureStatus[index] = $"未読込: ID={storedId}, sheet={sheet}, native={native}, shared={publishedIcon}";
        return false;
    }


    // v2.0.5.4: Native-inspired press behavior. These effects are drawn solely
    // in ImGui and never change game action execution, recast, or the native
    // game's own render nodes. Press timestamps come from already validated
    // key-pulse state and from actual mouse clicks.
    // v2.0.5.8: More visible native-inspired input feedback. The action icon
    // itself is never replaced or modified: an ImGui overlay recreates the
    // vanilla quick push-in and bright white/gold press flash on that ONE slot.
    private const long PressFeedbackDurationMs = 205;

    private static float NativeLikePressScale(long ageMs)
    {
        if (ageMs < 0 || ageMs >= PressFeedbackDurationMs) return 1f;
        var t = Math.Clamp(ageMs / (float)PressFeedbackDurationMs, 0f, 1f);
        // First frames noticeably compress the icon; return smoothly.
        var eased = 1f - MathF.Pow(1f - t, 2.3f);
        return 0.89f + 0.11f * eased;
    }

    private static void DrawNativeLikePressFeedback(ImDrawListPtr dl,
        Vector2 min, Vector2 max, long ageMs)
    {
        if (ageMs < 0 || ageMs >= PressFeedbackDurationMs) return;
        var t = Math.Clamp(ageMs / (float)PressFeedbackDurationMs, 0f, 1f);
        // Fade rapidly after an obvious initial flash; still fully gone at
        // 205ms so no permanent colored box remains on the hotbar.
        var peak = MathF.Pow(1f - t, 1.5f);
        byte fillA = (byte)Math.Clamp((int)(162f * peak), 0, 255);
        byte ringA = (byte)Math.Clamp((int)(255f * (1f - t * .75f)), 0, 255);
        byte haloA = (byte)Math.Clamp((int)(135f * peak), 0, 255);
        // ImGui ABGR; a nearly-white face flash with soft warm edges.
        dl.AddRectFilled(min + new Vector2(2f), max - new Vector2(2f),
            ((uint)fillA << 24) | 0x00F1F8FFu);
        dl.AddRect(min + new Vector2(.5f), max - new Vector2(.5f),
            ((uint)ringA << 24) | 0x00E2F3FFu, 0f, 0, 3.5f);
        dl.AddRect(min - new Vector2(1.5f), max + new Vector2(1.5f),
            ((uint)haloA << 24) | 0x00B5DFFFu, 0f, 0, 3f);
        if (t < .40f)
        {
            dl.AddLine(new Vector2(min.X + 3f, max.Y - 3f),
                new Vector2(max.X - 3f, max.Y - 3f),
                0xA0000000u, 2.2f);
        }
    }

    // Display the game's MP/CP/GP style resource number at the lower left.
    // If FF14 supplies a native render sample use its computed CostValue;
    // when the original bar is hidden query the game's own action-cost function.
    // Cost queries are cached per slot to avoid running native code every frame.
    private readonly uint[] displayedCostActionIds = new uint[12];
    private readonly int[] displayedCostValues = new int[12];
    private readonly long[] displayedCostReadTicks = new long[12];

    private int GetDisplayedResourceCost(int slotIndex, uint adjustedAction,
        bool hasNativeState, NativeMirrorSample nativeState)
    {
        if (!config.NativeActionCostEnabled || adjustedAction == 0 || slotIndex is < 0 or >= 12)
            return 0;
        // Native CostValue is authoritative if sampled for this exact action.
        if (hasNativeState && nativeState.ActionId == adjustedAction)
            return nativeState.CostValue is > 0 and <= 99999 ? (int)nativeState.CostValue : 0;
        long now = Environment.TickCount64;
        if (displayedCostActionIds[slotIndex] == adjustedAction &&
            now - displayedCostReadTicks[slotIndex] is >= 0 and < 220)
            return displayedCostValues[slotIndex];
        int cost = 0;
        try
        {
            if (ActionManager.Instance() != null)
                cost = ActionManager.GetActionCost(ActionType.Action, adjustedAction, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            if (slotIndex == 0 && now - lastActionCostWarningTick >= 10000)
            {
                lastActionCostWarningTick = now;
                log.Debug($"Action-cost display unavailable: {ex.GetType().Name}");
            }
        }
        cost = cost is > 0 and <= 99999 ? cost : 0;
        displayedCostActionIds[slotIndex] = adjustedAction;
        displayedCostValues[slotIndex] = cost;
        displayedCostReadTicks[slotIndex] = now;
        return cost;
    }
    private long lastActionCostWarningTick;

    private static void DrawNativeActionCost(ImDrawListPtr dl, Vector2 min, Vector2 max,
        int cell, int amount)
    {
        if (amount <= 0) return;
        string label = amount.ToString();
        float fontSize = Math.Clamp(cell * .33f, 13f, 54f);
        var bounds = MeasureOverlayText(label, fontSize);
        float maxWidth = cell * .82f;
        if (bounds.X > maxWidth && bounds.X > 0)
        {
            fontSize *= maxWidth / bounds.X;
            bounds = MeasureOverlayText(label, fontSize);
        }
        var pos = new Vector2(min.X + Math.Max(1f, cell * .027f),
            max.Y - bounds.Y - Math.Max(1f, cell * .025f));
        DrawOutlinedText(dl, label, pos, fontSize, 0xFFF8F8F8u,
            Math.Clamp(cell * .021f, 1f, 3f));
    }

    // A slim, non-obscuring progress trace. The icon stays completely bright
    // (v2.0.5 behavior) while the perimeter advances as the recast recovers.
    private static void DrawNativeLikeCooldownProgress(ImDrawListPtr dl,
        Vector2 min, Vector2 max, float remaining, float total)
    {
        if (!float.IsFinite(total) || !float.IsFinite(remaining) || total <= .01f) return;
        float finished = Math.Clamp(1f - remaining / total, 0f, 1f);
        if (finished <= .001f) return;
        float inset = 2f;
        var tl = min + new Vector2(inset);
        var br = max - new Vector2(inset);
        var tm = new Vector2((tl.X + br.X) / 2f, tl.Y);
        var tr = new Vector2(br.X, tl.Y);
        var rb = br;
        var bl = new Vector2(tl.X, br.Y);
        var lm = tl;
        var points = new[] { tm, tr, rb, bl, lm, tm };
        var lengths = new float[5];
        float perimeter = 0f;
        for (var i = 0; i < 5; i++)
        {
            lengths[i] = Vector2.Distance(points[i], points[i + 1]);
            perimeter += lengths[i];
        }
        float until = perimeter * finished;
        const uint trackColor = 0xD2E1DDC1u;
        for (var i = 0; i < 5 && until > .001f; i++)
        {
            var draw = Math.Min(lengths[i], until);
            var to = Vector2.Lerp(points[i], points[i + 1], draw / Math.Max(.01f, lengths[i]));
            dl.AddLine(points[i], to, trackColor, Math.Clamp((br.X - tl.X) * .045f, 1.5f, 3f));
            until -= draw;
        }
    }

    // Purely optional native-style dark sector. It is OFF by default because
    // earlier users explicitly preferred completely unobscured icons.
    private static void DrawNativeLikeCooldownSector(ImDrawListPtr dl,
        Vector2 min, Vector2 max, float remaining, float total)
    {
        if (!float.IsFinite(total) || !float.IsFinite(remaining) || total <= .01f) return;
        float fraction = Math.Clamp(remaining / total, 0f, 1f);
        if (fraction <= .001f) return;
        var center = (min + max) * .5f;
        float radius = (max.X - min.X) * .76f;
        const int segments = 48;
        float start = -MathF.PI / 2f;
        float sweep = MathF.Tau * fraction;
        dl.PushClipRect(min + new Vector2(1f), max - new Vector2(1f), true);
        // Draw dark sectors only if explicitly enabled in settings; this is
        // separate from the non-obscuring progress outline above.
        for (int i = 0; i < segments; ++i)
        {
            float a = start + sweep * i / segments;
            float b = start + sweep * (i + 1) / segments;
            dl.AddTriangleFilled(center,
                center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius,
                center + new Vector2(MathF.Cos(b), MathF.Sin(b)) * radius,
                0x64000000u);
        }
        dl.PopClipRect();
    }

    // FF14's HotbarUIIntermediate.DrawAnts tells us that an action has the
    // game's own animated emphasis state. Recreate a modest moving yellow
    // perimeter, rather than guessing availability from cooldown numbers.
    private static void DrawNativeLikeProcHighlight(ImDrawListPtr dl,
        Vector2 min, Vector2 max, long timeMs)
    {
        var phase = ((timeMs / 65L) % 16L) / 16f;
        var inset = 1.5f;
        var tl = min + new Vector2(inset);
        var tr = new Vector2(max.X - inset, tl.Y);
        var br = max - new Vector2(inset);
        var bl = new Vector2(tl.X, br.Y);
        var vertices = new[] { tl, tr, br, bl, tl };
        const int dotsPerEdge = 7;
        for (var side = 0; side < 4; side++)
        {
            for (var dot = 0; dot < dotsPerEdge; dot++)
            {
                var pos = (dot + phase) / dotsPerEdge;
                var end = Math.Min(1f, pos + .45f / dotsPerEdge);
                if (pos >= 1f) continue;
                dl.AddLine(Vector2.Lerp(vertices[side], vertices[side + 1], pos),
                    Vector2.Lerp(vertices[side], vertices[side + 1], end),
                    0xF6FFD881u, 2.7f);
            }
        }
    }

    private void DrawInternalHotbarCore()
    {
        var cell = Math.Clamp(config.InternalHotbarCellSize, 24, 200);
        var cols = NativeColumns(config.OverlayLayout);
        var rows = 12 / cols;
        var spacing = Math.Clamp(config.InternalHotbarCellGap, 0, 20);
        // Exactly N cells and (N-1) gaps: never add a trailing phantom gap.
        var contentSize = new Vector2(cols * cell + (cols - 1) * spacing,
            rows * cell + (rows - 1) * spacing);
        // No draggable header above the icons: icon position stays identical
        // between LOCK/EDIT, with the lock placed to the RIGHT of the slots.
        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.NoBackground |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoMove;
        ImGui.SetNextWindowPos(new Vector2(config.InternalHotbarX, config.InternalHotbarY), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f));
        var open = ImGui.Begin("##WrathPrivateHotbarAlpha41", flags);
        if (open)
        {
            var start = ImGui.GetCursorScreenPos();
            // A single hit area per slot; native icons are drawn on the ImGui
            // draw list behind the transparent input hit areas.
            var dl = ImGui.GetWindowDrawList();
            for (var i = 0; i < 12; ++i)
            {
                var x = i % cols;
                var y = i / cols;
                var min = start + new Vector2(x * (cell + spacing), y * (cell + spacing));
                var max = min + new Vector2(cell, cell);
                var baseOff = 516 + i * 12;
                var iconId = overlayView.ReadUInt32(baseOff + 8);
                var storedId = overlayView.ReadUInt32(baseOff);
                var kind = overlayView.ReadInt32(1028 + i * 8);
                bool isItem = kind == 1;
                uint nativeIconId = 0;
                var native = nativeMirrorSlots[config.SelectedHotbar - 1, i];
                var age = Environment.TickCount64 - native.Tick;
                if (!isItem && config.OverlayNativeMirrorEnabled && native.Tick > 0 &&
                    age >= 0 && age <= NativeMirrorStaleMs && native.StoredCommandId == storedId)
                    nativeIconId = native.IconId;
                if (nativeIconId != 0) iconId = nativeIconId;

                // Native source samples may go stale when FF14 hides the
                // original bar. In that case we keep the existing live action
                // fallback, and never claim an unknown proc/availability state.
                NativeMirrorSample nativeState = default;
                bool hasNativeState = config.OverlayNativeMirrorEnabled &&
                    TryGetNativeMirror(config.SelectedHotbar - 1, i, storedId, out nativeState);
                bool nativeProcActive = hasNativeState && (nativeState.Flags & 2u) != 0u;
                var tick = Environment.TickCount64;
                var held = config.PulseEnabled &&
                    overlayView.ReadInt32(PulseDisplayBase) == PulseDisplayMagic &&
                    overlayView.ReadInt32(PulseDisplayBase + 4) == i;
                var pulseTick = held ? overlayView.ReadInt64(PulseDisplayBase + 8) : 0L;
                var lastPressTick = Math.Max(internalClickPressTicks[i], pulseTick);
                var pressAgeMs = lastPressTick > 0 ? tick - lastPressTick : long.MaxValue;
                var pressScale = config.NativePressFeedbackEnabled
                    ? NativeLikePressScale(pressAgeMs) : 1f;
                var iconInset = cell * (1f - pressScale) / 2f;
                var iconMin = min + new Vector2(iconInset);
                var iconMax = max - new Vector2(iconInset);
                // The backplate does not scale, only the pressed icon does.
                dl.AddRectFilled(min, max, 0xA0101218u);

                bool iconRendered = false;
                if (isItem && storedId != 0)
                {
                    iconRendered = DrawItemIconWithFallback(dl, i, storedId,
                        iconId, iconMin, iconMax);
                }
                else if (iconId != 0)
                {
                    try
                    {
                        var shared = textureProvider.GetFromGameIcon(
                            new GameIconLookup { IconId = iconId });
                        if (shared.TryGetWrap(out var wrap, out _) && wrap != null)
                        {
                            dl.AddImage(wrap.Handle, iconMin + new Vector2(1f), iconMax - new Vector2(1f));
                            iconRendered = true;
                        }
                    }
                    catch { /* draw neutral background until texture is available */ }
                }
                if (!iconRendered)
                    dl.AddRectFilled(iconMin, iconMax, iconId == 0 ? 0x88303030u : 0xCC181818u);

                // v2.0.5: Timers take priority without darkening the icon. Native item
                // hotbars hide inventory amount DURING cooldown and display a
                // large timer centered on the icon instead.
                var remaining = overlayView.ReadSingle(792 + i * 8);
                var total = overlayView.ReadSingle(796 + i * 8);
                bool cooldownActive = config.OverlayCooldownEnabled &&
                    total > 0.01f && remaining > 0f &&
                    float.IsFinite(remaining) && float.IsFinite(total);
                bool showCooldown = cooldownActive &&
                    (!config.OverlayHideShortRecast || total > 3f);
                // Default is unobscured, as in the approved v2.0.5. Add
                // per-frame visual progress and an optional native-like sweep.
                if (showCooldown && config.NativeCooldownSectorEnabled)
                    DrawNativeLikeCooldownSector(dl, min, max, remaining, total);
                if (showCooldown && config.NativeCooldownProgressEnabled)
                    DrawNativeLikeCooldownProgress(dl, min, max, remaining, total);
                if (showCooldown && config.OverlayCooldownNumbers)
                    DrawNativeCooldownText(dl, min, max, cell, remaining);
                // Native hotbar resource costs are shown below the icon at
                // bottom left; they are separate from item counts/charges.
                bool actionSlot = overlayView.ReadInt32(
                    NativeMirrorSlotBase + i * NativeMirrorSlotBytes + 44) ==
                    (int)RaptureHotbarModule.HotbarSlotType.Action;
                if (actionSlot && storedId != 0 && kind == 0)
                {
                    uint costAction = lastOverlayPublishedIds[i];
                    int actionCost = GetDisplayedResourceCost(i, costAction, hasNativeState, nativeState);
                    if (actionCost > 0)
                        DrawNativeActionCost(dl, min, max, cell, actionCost);
                }
                var chargeCurrent = overlayView.ReadInt32(904 + i * 8);
                var chargeMax = overlayView.ReadInt32(908 + i * 8);
                var inventory = overlayView.ReadInt32(1024 + i * 8);
                // IMPORTANT: Do not show xN while an item is on recast, even
                // when 'hide short recast' suppresses its timer number.
                if (isItem && inventory >= 0 && !cooldownActive)
                    DrawNativeHotbarCounter(dl, max, cell, "x" + inventory.ToString(), true);
                else if (!isItem && chargeMax > 1)
                    DrawNativeHotbarCounter(dl, max, cell, chargeCurrent.ToString(), false);

                // Show native DrawAnts only when a fresh FF14 render sample
                // confirms it. The selected bar may be hidden; no stale glow.
                if (config.NativeProcHighlightEnabled && nativeProcActive)
                    DrawNativeLikeProcHighlight(dl, min, max, tick);
                if (config.NativePressFeedbackEnabled && pressAgeMs >= 0 && pressAgeMs < PressFeedbackDurationMs)
                    DrawNativeLikePressFeedback(dl, min, max, pressAgeMs);
                else if (held)
                    dl.AddRect(min + new Vector2(1f), max - new Vector2(1f), 0xFF00DFFFu);
                dl.AddRect(min, max, 0xFFA3A3CCu);

                ImGui.SetCursorScreenPos(min);
                ImGui.PushID(i);
                ImGui.InvisibleButton("##nativeSlot", new Vector2(cell, cell));
                if (config.OverlayClickActions && ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    QueueInternalSlotClick(i);
                if (ImGui.IsItemHovered() && !config.InternalHotbarLocked)
                    ImGui.SetTooltip($"HB{config.SelectedHotbar} スロット{i + 1} / ID {storedId}");
                ImGui.PopID();
            }
            // Right-side lock control. The user can always click it (even
            // while locked), just like the native hotbar lock icon.
            const float controlSize = 26f;
            var lockPos = start + new Vector2(contentSize.X + 6f,
                MathF.Max(0f, (cell - controlSize) * 0.5f));
            ImGui.SetCursorScreenPos(lockPos);
            ImGui.InvisibleButton("##internalLockRight", new Vector2(controlSize, controlSize));
            bool lockHovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                config.InternalHotbarLocked = !config.InternalHotbarLocked;
                Save();
            }
            if (lockHovered)
                ImGui.SetTooltip(config.InternalHotbarLocked ? "ロック解除 / 編集する" : "位置とサイズをロック");
            uint controlBorder = lockHovered ? 0xFFBDEBFFu : 0xFF999999u;
            dl.AddRectFilled(lockPos, lockPos + new Vector2(controlSize),
                lockHovered ? 0xB04A4A4Au : 0x8B151515u);
            DrawPrivateLockGlyph(dl, lockPos, config.InternalHotbarLocked,
                lockHovered ? 0xFFFFFFFFu : 0xFFE6E6E6u);
            dl.AddRect(lockPos, lockPos + new Vector2(controlSize), controlBorder);

            if (!config.InternalHotbarLocked)
            {
                // +/- buttons on the right modify the saved CELL size, not the
                // outer window width. This also immediately disables auto-size.
                var minusPos = lockPos + new Vector2(controlSize + 4f, 0f);
                ImGui.SetCursorScreenPos(minusPos);
                if (ImGui.Button("-##internalShrink", new Vector2(controlSize)))
                    ApplyManualInternalCellSize(config.InternalHotbarCellSize - 4);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("アイコンを小さく (-4px)");

                var plusPos = minusPos + new Vector2(controlSize + 4f, 0f);
                ImGui.SetCursorScreenPos(plusPos);
                if (ImGui.Button("+##internalGrow", new Vector2(controlSize)))
                    ApplyManualInternalCellSize(config.InternalHotbarCellSize + 4);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("アイコンを大きく (+4px)");

                // Dedicated drag grip, instead of a header that moves the icons.
                var movePos = plusPos + new Vector2(controlSize + 4f, 0f);
                ImGui.SetCursorScreenPos(movePos);
                ImGui.InvisibleButton("##internalMoveGrip", new Vector2(controlSize));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("ここをドラッグしてホットバーを移動");
                dl.AddRectFilled(movePos, movePos + new Vector2(controlSize), 0x9A252525u);
                dl.AddText(movePos + new Vector2(7f, 5f), 0xFFFFFFFFu, "::");
                if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    var delta = ImGui.GetIO().MouseDelta;
                    config.InternalHotbarX += delta.X;
                    config.InternalHotbarY += delta.Y;
                    internalPanelMoveActive = true;
                }
                else if (internalPanelMoveActive && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    internalPanelMoveActive = false;
                    Save();
                }

                // Ctrl+mouse wheel is an additional shortcut to quickly match
                // the native size. Holding Ctrl avoids accidental resizing.
                if (ImGui.IsWindowHovered() && Math.Abs(ImGui.GetIO().MouseWheel) > 0.001f)
                {
                    if (ImGui.GetIO().KeyShift && !ImGui.GetIO().KeyCtrl)
                    {
                        // Shift+wheel changes the distance between native-style
                        // slots in 1px increments, without changing icon size.
                        var step = Math.Sign(ImGui.GetIO().MouseWheel);
                        ApplyManualInternalCellGap(config.InternalHotbarCellGap + step);
                    }
                    else if (ImGui.GetIO().KeyCtrl)
                    {
                        internalDragRemainder += ImGui.GetIO().MouseWheel * 4f;
                        var change = (int)MathF.Truncate(internalDragRemainder);
                        if (change != 0)
                        {
                            internalDragRemainder -= change;
                            ApplyManualInternalCellSize(config.InternalHotbarCellSize + change);
                        }
                    }
                }
            }
            // Expand our window to cover all click targets without displaying
            // a header above the first row. Lock/unlock never shifts icons.
            ImGui.SetCursorScreenPos(start + new Vector2(0f, contentSize.Y));
            ImGui.Dummy(new Vector2(contentSize.X + (config.InternalHotbarLocked ? 33f : 118f), 1f));
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void OpenSettings() => settingsVisible = true;
    private void OnCommand(string command, string args)
    {
        var action = args.Trim().ToLowerInvariant();
        switch (action)
        {
            case "unlock":
            case "edit":
                config.InternalHotbarLocked = false;
                config.InternalHotbarEnabled = true;
                Save();
                break;
            case "lock":
                config.InternalHotbarLocked = true;
                Save();
                break;
            case "togglelock":
                config.InternalHotbarLocked = !config.InternalHotbarLocked;
                Save();
                break;
            case "":
            case "config":
                settingsVisible = !settingsVisible;
                break;
            default:
                settingsVisible = true;
                break;
        }
    }
    private void Save()
    {
        pluginInterface.SavePluginConfig(config);
        overlayView.Write(892, config.OverlayCooldownEnabled ? 1 : 0);
        overlayView.Write(896, config.OverlayCooldownNumbers ? 1 : 0);
        overlayView.Write(1004, config.OverlayHideShortRecast ? 1 : 0);
        overlayView.Write(1008, Math.Max(0, Array.IndexOf(layouts, config.OverlayLayout)));
    }

    private void DrawSettings()
    {
        if (!settingsVisible) return;
        ImGui.SetNextWindowSize(new Vector2(530, 550), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("WrathPrivateHotbar | 設定", ref settingsVisible))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("WrathComboを使うホットバーと、FF14標準のホットバーを分けて表示します。");
        ImGui.TextDisabled("対象外のホットバーは、元のアイコン・説明・スキル実行を維持します。");
        ImGui.Separator();

        ImGui.Text("WrathComboを使用するホットバー");
        int bar = config.SelectedHotbar;
        if (ImGui.BeginCombo("##sourceHotbar", $"ホットバー {bar}"))
        {
            for (int i = 1; i <= 10; i++)
            {
                if (ImGui.Selectable($"ホットバー {i}", i == bar))
                {
                    config.SelectedHotbar = i;
                    Save();
                }
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled("選択したホットバーだけWrathComboの可変表示を使用します。");

        ImGui.Separator();
        ImGui.Text("疑似ホットバー");
        bool show = config.InternalHotbarEnabled;
        if (ImGui.Checkbox("疑似ホットバーを表示する", ref show))
        {
            config.InternalHotbarEnabled = show;
            config.OverlayEnabled = false;
            Save();
        }
        bool click = config.OverlayClickActions;
        if (ImGui.Checkbox("アイコンのクリックでスキル・アイテムを使用する", ref click))
        {
            config.OverlayClickActions = click;
            Save();
        }
        bool locked = config.InternalHotbarLocked;
        if (ImGui.Checkbox("位置とサイズを固定する", ref locked))
        {
            config.InternalHotbarLocked = locked;
            Save();
        }
        if (ImGui.Button(config.InternalHotbarLocked ? "編集モードにする" : "位置とサイズを固定"))
        {
            config.InternalHotbarLocked = !config.InternalHotbarLocked;
            Save();
        }
        ImGui.TextDisabled("画面右端の鍵でも切替可能。解除中は + / - と移動用つまみが表示されます。");

        int layoutIndex = Array.IndexOf(layouts, config.OverlayLayout);
        if (layoutIndex < 0) layoutIndex = 0;
        if (ImGui.Combo("アイコンの並び方", ref layoutIndex, layouts, layouts.Length))
        {
            config.OverlayLayout = layouts[layoutIndex];
            Save();
        }
        int iconSize = config.InternalHotbarCellSize;
        if (ImGui.SliderInt("アイコンの大きさ (px)", ref iconSize, 24, 200))
            ApplyManualInternalCellSize(iconSize);
        int gap = config.InternalHotbarCellGap;
        if (ImGui.SliderInt("アイコンの間隔 (px)", ref gap, 0, 20))
            ApplyManualInternalCellGap(gap);
        ImGui.TextDisabled("純正ホットバーに合わせて調整した値を自動保存します。");

        bool isHidden = locallyHiddenHotbars.Contains(config.SelectedHotbar);
        if (ImGui.Button(isHidden ? "元のホットバーを表示する" : "元のホットバーを非表示にする"))
            ToggleSelectedHotbarVisibility();
        ImGui.TextDisabled("この非表示設定は保存され、次回ログイン時にも自動適用されます。");
        ImGui.TextDisabled("FF14本体のHUD設定は変更しません。プラグイン停止時は再表示します。");

        bool hideNpc = config.HideDuringNpcDialogue;
        if (ImGui.Checkbox("NPCとの会話中は疑似ホットバーを隠す", ref hideNpc))
        {
            config.HideDuringNpcDialogue = hideNpc;
            Save();
        }
        ImGui.TextDisabled("会話・選択肢・確認画面が開いている間は自動で隠れ、終了後に戻ります。");
        bool hideCutscenes = config.HideDuringCutscenes;
        if (ImGui.Checkbox("カットシーン・ムービー・イベント中は疑似ホットバーを隠す", ref hideCutscenes))
        {
            config.HideDuringCutscenes = hideCutscenes;
            Save();
        }
        ImGui.TextDisabled("イベント演出中も自動で隠れ、通常操作に戻ると再表示します。");

        ImGui.Separator();
        ImGui.Text("リキャスト表示");
        bool cooldownEnabled = config.OverlayCooldownEnabled;
        if (ImGui.Checkbox("リキャスト情報を取得する", ref cooldownEnabled))
        {
            config.OverlayCooldownEnabled = cooldownEnabled;
            Save();
        }
        ImGui.TextDisabled("基本設定ではリキャスト中もアイコンの明るさを維持します。");
        var recastProgress = config.NativeCooldownProgressEnabled;
        if (ImGui.Checkbox("リキャスト進行状況をアイコンの縁に表示する（暗転なし）", ref recastProgress))
        {
            config.NativeCooldownProgressEnabled = recastProgress;
            Save();
        }
        var recastSector = config.NativeCooldownSectorEnabled;
        if (ImGui.Checkbox("純正風の扇形リキャスト暗転も使用する（初期OFF）", ref recastSector))
        {
            config.NativeCooldownSectorEnabled = recastSector;
            Save();
        }
        ImGui.TextDisabled("従来の黒塗りなしが標準。扇形は必要なときだけ有効化できます。");
        bool showSeconds = config.OverlayCooldownNumbers;
        if (ImGui.Checkbox("残り秒数を表示する", ref showSeconds))
        {
            config.OverlayCooldownNumbers = showSeconds;
            Save();
        }
        bool hideShort = config.OverlayHideShortRecast;
        if (ImGui.Checkbox("3秒以下の短いリキャストを省略する", ref hideShort))
        {
            config.OverlayHideShortRecast = hideShort;
            Save();
        }

        ImGui.Separator();
        ImGui.Text("スキルアイコンの純正風アニメーション");
        var pressFeedback = config.NativePressFeedbackEnabled;
        if (ImGui.Checkbox("スキル入力時にアイコンを押し込み・フラッシュさせる", ref pressFeedback))
        {
            config.NativePressFeedbackEnabled = pressFeedback;
            Save();
        }
        var procHighlight = config.NativeProcHighlightEnabled;
        if (ImGui.Checkbox("FF14本体から取得できたProc強調演出を反映する", ref procHighlight))
        {
            config.NativeProcHighlightEnabled = procHighlight;
            Save();
        }
        ImGui.TextDisabled("純正描画情報が取得できない場合は安全な通常表示に戻ります。");
        var actionCost = config.NativeActionCostEnabled;
        if (ImGui.Checkbox("スキルのMP/CP/GP消費数値をアイコン左下に表示する", ref actionCost))
        {
            config.NativeActionCostEnabled = actionCost;
            Save();
        }
        ImGui.TextDisabled("FF14の実行時消費量を参照します。変化スキルや一部の特殊コストは実機で確認してください。");

        ImGui.Separator();
        ImGui.Text("キー操作と発光");
        bool pulse = config.PulseEnabled;
        if (ImGui.Checkbox("押しているキーに対応するスキルを発光させる", ref pulse))
        {
            config.PulseEnabled = pulse;
            Save();
        }
        if (ImGui.CollapsingHeader("キー割り当てを変更する"))
        {
            ImGui.TextWrapped("スロットごとに発光に使用するキーを指定します。Shift/Ctrl/Altとの組み合わせは設定できません。");
            var keys = EffectiveKeyCodes;
            for (int slotIndex = 0; slotIndex < 12; slotIndex++)
            {
                int key = keys[slotIndex];
                var label = Array.Find(KeyChoices, choice => choice.Code == key).Label ?? $"VK 0x{key:X2}";
                if (ImGui.BeginCombo($"スロット {slotIndex + 1}##key{slotIndex}", label))
                {
                    foreach (var choice in KeyChoices)
                    {
                        if (!ImGui.Selectable(choice.Label + $"##{slotIndex}-{choice.Code}", key == choice.Code)) continue;
                        var changed = (int[])keys.Clone();
                        changed[slotIndex] = choice.Code;
                        config.PulseKeyCodes = changed;
                        keys = changed;
                        activePulseSource = -1;
                        Array.Clear(previousKeyDown);
                        Save();
                    }
                    ImGui.EndCombo();
                }
            }
            if (ImGui.Button("キーを初期配置に戻す"))
            {
                config.PulseKeyCodes = (int[])DefaultKeyCodes.Clone();
                activePulseSource = -1;
                Array.Clear(previousKeyDown);
                Save();
            }
        }
        ImGui.Separator();
        ImGui.Text("ReActionを使わない人向け：長押し連打");
        var turbo = config.BuiltInTurboEnabled;
        if (ImGui.Checkbox("内蔵Turbo（選択ホットバーの通常スキルだけ）", ref turbo))
        {
            config.BuiltInTurboEnabled = turbo;
            ResetBuiltInTurbo();
            Save();
        }
        ImGui.TextWrapped("ReAction未導入者向け。初期OFF。キーを押した最初の1回はFF14本体に任せ、押しっぱなしの間だけ追加で実行要求します。") ;
        var turboInterval = config.BuiltInTurboIntervalMs;
        if (ImGui.SliderInt("連打間隔 (ms)##builtInTurbo", ref turboInterval, 50, 500))
        {
            config.BuiltInTurboIntervalMs = turboInterval;
            ResetBuiltInTurbo();
            Save();
        }
        var turboDelay = config.BuiltInTurboStartDelayMs;
        if (ImGui.SliderInt("追加開始遅延 (ms)##builtInTurbo", ref turboDelay, 0, 500))
        {
            config.BuiltInTurboStartDelayMs = turboDelay;
            ResetBuiltInTurbo();
            Save();
        }
        var turboOutOfCombat = config.BuiltInTurboOutOfCombat;
        if (ImGui.Checkbox("戦闘外でも長押し連打を使う", ref turboOutOfCombat))
        {
            config.BuiltInTurboOutOfCombat = turboOutOfCombat;
            ResetBuiltInTurbo();
            Save();
        }
        ImGui.TextDisabled(turboStatus);
        ImGui.TextWrapped("対象：上で設定したスロット1～12の単独キー・通常アクションだけ。Shift/Ctrl/Alt、クロスホットバー、マクロ、アイテムは非対応。FF14のキー設定も一致させてください。");
        ImGui.TextWrapped("ReActionがロードされていると自動停止します。両方の連打機能は併用しません。チャット入力中・NPC会話中・非アクティブ時にも停止します。");

        if (ImGui.CollapsingHeader("発光間隔の詳細設定"))
        {
            bool random = config.RandomPulse;
            if (ImGui.RadioButton("ランダム間隔", random)) { config.RandomPulse = true; Save(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("一定間隔", !random)) { config.RandomPulse = false; Save(); }
            if (config.RandomPulse)
            {
                int min = config.PulseMinMs;
                int max = config.PulseMaxMs;
                if (ImGui.SliderInt("最短間隔 (ms)", ref min, 50, 500))
                {
                    config.PulseMinMs = Math.Min(min, config.PulseMaxMs);
                    Save();
                }
                if (ImGui.SliderInt("最長間隔 (ms)", ref max, 50, 500))
                {
                    config.PulseMaxMs = Math.Max(max, config.PulseMinMs);
                    Save();
                }
            }
            else
            {
                int fixedMs = config.FixedPulseMs;
                if (ImGui.SliderInt("間隔 (ms)", ref fixedMs, 50, 500))
                {
                    config.FixedPulseMs = fixedMs;
                    Save();
                }
            }
        }

        if (ImGui.CollapsingHeader("純正操作音と発光の同期"))
        {
            ImGui.TextWrapped("長押し連打中のFF14純正入力音（ID12）を抑え、疑似ホットバーの発光に合わせて同じゲーム音を再生します。入力や実際のアクションは変更しません。");
            var sync = config.SoundSyncNativeOnPulse;
            if (ImGui.Checkbox("純正操作音を発光と同期する", ref sync))
            {
                config.SoundSyncNativeOnPulse = sync;
                if (!sync) Volatile.Write(ref audioHeldKey, 0);
                Save();
            }
            ImGui.TextDisabled("FF14の更新後などに音が二重に聞こえる場合はOFFにしてください。");
            if (sync && audioSyncStatus != "純正操作音の同期処理が有効です" &&
                audioSyncStatus != "OFF（通常のFF14サウンド）")
                ImGui.TextWrapped(audioSyncStatus);
        }

        if (ImGui.CollapsingHeader("起動に問題がある場合"))
        {
            bool safeStartup = !config.FastNativeHookStartup;
            if (ImGui.Checkbox("安定優先モード（起動時の準備待ちを長くする）", ref safeStartup))
            {
                config.EnableDelayedNativeHooks = true;
                config.FastNativeHookStartup = !safeStartup;
                config.InstantNativeHookStartup = !safeStartup;
                Save();
            }
            ImGui.TextDisabled("FF14更新後などにログインで落ちる場合に試してください。通常はOFFで構いません。");
            if (!string.IsNullOrEmpty(fastHookRecoveryStatus)) ImGui.TextWrapped(fastHookRecoveryStatus);
            ImGui.TextDisabled($"純正表示の切り分け: {(nativeRenderHooksActive ? "有効" : "準備中")}");
            ImGui.TextDisabled($"スキル説明の補正: {tooltipIsolationStatus}");
        }

        ImGui.Separator();
        ImGui.TextDisabled("/wprivate config で設定を開く　/　/wprivate unlock でロック解除");
        ImGui.TextDisabled("配信で非表示になるかは録画方式によります。Dalamud内部描画だけでは自動で隠れません。");
        ImGui.End();
    }

    public void Dispose()
    {
        EndFastHookProbe();
        // When unloading in-game, restore UI nodes, not the persisted opt-in.
        // Next plugin load will re-apply the saved choice after normal HUD readiness.
        if (clientState.IsLoggedIn && playerState.IsLoaded &&
            Volatile.Read(ref earlyHotbarReady) != 0)
        {
            foreach (var bar in locallyHiddenHotbars)
                try { ApplyHotbarNodeVisibility(bar, true); } catch { }
        }
        locallyHiddenHotbars.Clear();
        StopOverlay();
        StopRestoreOverlay();
        SuspendNativeRenderHooks();
        Volatile.Write(ref audioSoundHookEnabled, 0);
        Volatile.Write(ref audioHeldKey, 0);
        try { originalUiSoundHook?.Disable(); } catch { }
        originalUiSoundHookInstalled = false;
        try { originalUiSoundHook?.Dispose(); } catch { }
        try { actionHoverHook?.Disable(); } catch { }
        try { actionHoverHook?.Dispose(); } catch { }
        try { executeByIdHook?.Dispose(); } catch { }
        try { executeSlotHook?.Dispose(); } catch { }
        try { adjustedActionHook?.Dispose(); } catch { }
        try { iconRenderHook?.Dispose(); } catch { }
        clickView.Dispose();
        clickMap.Dispose();
        overlayView.Dispose();
        overlayMap.Dispose();
        framework.Update -= OnUpdate;
        pluginInterface.UiBuilder.Draw -= DrawSettings;
        pluginInterface.UiBuilder.Draw -= DrawInternalHotbar;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        pluginInterface.UiBuilder.OpenMainUi -= OpenSettings;
        commands.RemoveHandler("/wprivate");
    }
}
