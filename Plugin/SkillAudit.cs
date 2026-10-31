using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace WrathPrivateHotbar;

// This feature OBSERVES successful local execution requests and reads current
// standard-hotbar assignments. It never sends actions or edits any hotbar slot.
public sealed unsafe partial class Plugin
{
    // UseActionLocation is the final client-side attempt to execute the adjusted
    // action. A non-zero return is an indication of client success, NOT proof of
    // server acceptance or damage. UI wording deliberately says "候補".
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte AuditUseActionLocationDelegate(ActionManager* self,
        ActionType actionType, uint actionId, ulong targetId, Vector3* location,
        uint extraParam, byte a7);

    private Hook<AuditUseActionLocationDelegate>? auditUseActionHook;
    private readonly ConcurrentQueue<uint> auditPendingActions = new();
    private int auditPendingCount;
    private int auditMonitoring;
    private long auditNextHookRetryAt;
    private bool auditHookEnabled;
    private bool auditOnlyMissing = true;
    private readonly Dictionary<uint, (string Name, uint Icon)> auditActionDetailsCache = new();
    private long auditLastSnapshotAt;
    private string auditStatus = "自動監視の準備待ち";
    private readonly Dictionary<(uint Job, uint Action), SkillAuditEntry> auditEntries = new();
    private readonly HashSet<uint> auditStoredActionIds = new();
    private readonly HashSet<uint> auditNativeActionIds = new();
    private bool auditSnapshotReady;
    private int auditScannedSlots;
    private int auditCheckedBars;
    private uint auditLastJob;
    private const int AuditMaxDistinctPerJob = 300;

    // FFXIV's official PvE BLM job guide: Paradox cannot be placed on a
    // hotbar. Fire and Blizzard are the two hotbar actions that change into
    // Paradox. Check the STORED source IDs, not only the current adjusted IDs:
    // the Paradox state may have ended when the user opens this window.
    // Keep the mapping job-scoped to avoid treating an unrelated action as a
    // replacement. Extend only with verified native replacement relations.
    private const uint AuditBlmJob = 25;
    private const uint AuditParadox = 25797;
    private const uint AuditFire = 141;
    private const uint AuditBlizzard = 142;

    private static bool AuditIsKnownParadox(uint job, uint action) =>
        job == AuditBlmJob && action == AuditParadox;

    private bool AuditParadoxSourceRegistered() =>
        auditStoredActionIds.Contains(AuditFire) ||
        auditStoredActionIds.Contains(AuditBlizzard);


    private sealed class SkillAuditEntry
    {
        public required uint Job;
        public required uint Action;
        public int Count;
        public long LastAt;
    }

    private byte OnAuditUseActionLocation(ActionManager* self, ActionType actionType,
        uint actionId, ulong targetId, Vector3* location, uint extraParam, byte a7)
    {
        // Invoke the original exactly once and return its result unchanged.
        var result = auditUseActionHook!.Original(self, actionType, actionId,
            targetId, location, extraParam, a7);
        if (result != 0 && actionType == ActionType.Action && actionId != 0 &&
            Volatile.Read(ref auditMonitoring) != 0)
        {
            // Keep native hook light: no Lumina or UI lookups on the hook.
            if (Interlocked.Increment(ref auditPendingCount) <= 512)
                auditPendingActions.Enqueue(actionId);
            else
                Interlocked.Decrement(ref auditPendingCount);
        }
        return result;
    }

    // Always-on while logged in and the normal hotbar UI is ready.
    // A failed hook install is retried at a capped interval rather than once
    // per frame. No actions are sent/modified by this observer.
    private void StopSkillAudit(string status = "ログイン待ち（自動監視）")
    {
        Volatile.Write(ref auditMonitoring, 0);
        auditStatus = status;
        if (!auditHookEnabled) return;
        auditHookEnabled = false;
        try { auditUseActionHook?.Disable(); }
        catch (Exception ex) { log.Debug($"Skill audit disable: {ex.GetType().Name}"); }
    }

    private void UpdateSkillAuditMonitor(bool uiReady)
    {
        bool shouldMonitor = uiReady && clientState.IsLoggedIn && playerState.IsLoaded;
        if (!shouldMonitor)
        {
            // Disable safely on logout / UI transition. Keep hook allocated so
            // it can be re-enabled after the next login (not disposed each frame).
            if (auditHookEnabled || Volatile.Read(ref auditMonitoring) != 0)
                StopSkillAudit("ログイン・HUD準備待ち（自動監視）");
            else if (auditStatus == "自動監視の準備待ち")
                auditStatus = "ログイン・HUD準備待ち（自動監視）";
        }
        else
        {
            Volatile.Write(ref auditMonitoring, 1);
            if (!auditHookEnabled && Environment.TickCount64 >= auditNextHookRetryAt)
            {
                try
                {
                    auditUseActionHook ??= gameInterop.HookFromAddress<AuditUseActionLocationDelegate>(
                        (nint)ActionManager.MemberFunctionPointers.UseActionLocation,
                        OnAuditUseActionLocation);
                    auditUseActionHook.Enable();
                    auditHookEnabled = true;
                    auditStatus = "常時監視中（クライアント実行要求を観測）";
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref auditMonitoring, 0);
                    auditNextHookRetryAt = Environment.TickCount64 + 10000;
                    auditStatus = $"監視準備に失敗: {ex.GetType().Name}（10秒後再試行）";
                }
            }
        }
        // Drain in managed framework.Update; jobs and Excel sheets are NOT read
        // from the native hook. Each job keeps independent, in-memory results.
        int handled = 0;
        while (handled < 128 && auditPendingActions.TryDequeue(out var actionId))
        {
            Interlocked.Decrement(ref auditPendingCount);
            handled++;
            if (!clientState.IsLoggedIn || !playerState.IsLoaded || actionId == 0) continue;
            uint job;
            try { job = playerState.ClassJob.RowId; }
            catch { continue; }
            if (job == 0) continue;
            auditLastJob = job;
            var key = (job, actionId);
            if (!auditEntries.TryGetValue(key, out var entry))
            {
                if (auditEntries.Keys.Count(x => x.Job == job) >= AuditMaxDistinctPerJob)
                {
                    auditStatus = "上限300種類に達しました。結果をクリアしてください。";
                    continue;
                }
                entry = new SkillAuditEntry { Job = job, Action = actionId };
                auditEntries.Add(key, entry);
            }
            entry.Count++;
            entry.LastAt = Environment.TickCount64;
        }
    }

    // Scan ALL 10 normal (non-cross) hotbars, regardless of HUD visibility.
    // Do NOT include the selected WrathCombo source hotbar. A skill assigned
    // only on that source is always considered a missing candidate.
    private void RefreshSkillAuditSnapshot()
    {
        var now = Environment.TickCount64;
        if (now - auditLastSnapshotAt < 400) return;
        auditLastSnapshotAt = now;
        auditSnapshotReady = false;
        auditStoredActionIds.Clear();
        auditNativeActionIds.Clear();
        auditScannedSlots = 0;
        auditCheckedBars = 0;
        if (!clientState.IsLoggedIn || !playerState.IsLoaded ||
            Volatile.Read(ref earlyHotbarReady) == 0) return;
        try
        {
            var module = RaptureHotbarModule.Instance();
            if (module == null || !module->ModuleReady) return;
            var sourceBar = Math.Clamp(config.SelectedHotbar, 1, 10);
            for (var hb = 1; hb <= 10; hb++)
            {
                if (hb == sourceBar) continue;
                auditCheckedBars++;
                for (var slot = 0; slot < 12; slot++)
                {
                    var nativeSlot = module->GetSlotById((uint)(hb - 1), (uint)slot);
                    if (nativeSlot == null || nativeSlot->CommandType !=
                        RaptureHotbarModule.HotbarSlotType.Action || nativeSlot->CommandId == 0)
                        continue;
                    auditScannedSlots++;
                    var stored = nativeSlot->CommandId;
                    auditStoredActionIds.Add(stored);
                    // Game-native transformations can display a different ID
                    // from the stored action. May still miss old proc states.
                    var native = GetNativePulseAction(stored);
                    if (native != 0) auditNativeActionIds.Add(native);
                }
            }
            auditSnapshotReady = true;
        }
        catch (Exception ex)
        {
            auditStatus = $"配置の読み取りに失敗: {ex.GetType().Name}";
        }
    }

    private int AuditMatchKind(uint job, uint action)
    {
        if (!auditSnapshotReady) return -1; // waiting for player/HUD
        if (auditStoredActionIds.Contains(action)) return 2; // exact stored ID
        if (auditNativeActionIds.Contains(action)) return 1; // current native transformation
        // Paradox is unassignable: even after the proc ends, Fire or Blizzard
        // on ANY non-Wrath source HB is a valid display/assignment source.
        // This does not suppress truly missing Fire IV or other normal actions.
        if (AuditIsKnownParadox(job, action) && AuditParadoxSourceRegistered())
            return 3; // verified stored source of a native transformed action
        return 0; // missing candidate (not absolute proof)
    }

    private (string Name, uint Icon) AuditActionDetails(uint id)
    {
        if (auditActionDetailsCache.TryGetValue(id, out var cached)) return cached;
        string name = $"Action ID {id}";
        uint icon = 0;
        try
        {
            var row = data.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(id);
            if (row is { } action)
            {
                var text = action.Name.ToString();
                if (!string.IsNullOrWhiteSpace(text)) name = text;
                icon = action.Icon;
            }
        }
        catch { /* display action ID even if game data is unavailable */ }
        var info = (name, icon);
        auditActionDetailsCache[id] = info;
        return info;
    }

    // Same Dalamud texture path used by the pseudo-hotbar. Missing icons get
    // a neutral placeholder; never block the results while a texture loads.
    private void DrawAuditActionIcon(uint iconId)
    {
        const float side = 34f;
        ImGui.Dummy(new Vector2(side, side));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        bool drawn = false;
        if (iconId != 0)
        {
            try
            {
                var shared = textureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup { IconId = iconId });
                if (shared.TryGetWrap(out var wrap, out _) && wrap != null)
                {
                    DrawIconWithRoundedCorners(dl, wrap, min, max, 4f);
                    drawn = true;
                }
            }
            catch { /* keep a neutral fallback for missing icons */ }
        }
        if (!drawn) dl.AddRectFilled(min, max, 0xAA333333u, 4f);
    }

    private void DrawSkillAuditSettings()
    {
        if (!ImGui.CollapsingHeader("4. 未登録スキルチェッカー（テスト機能）")) return;
        ImGui.TextWrapped("使用されたスキルを記録し、WrathCombo用HB以外の標準ホットバー1～10に登録されているかチェックします。");
        ImGui.TextDisabled($"WrathCombo用HB{config.SelectedHotbar}を自動除外 ／ 他の9本を確認（非表示HBも対象）");
        ImGui.TextColored(auditHookEnabled
            ? new Vector4(0.45f, 0.90f, 0.56f, 1f)
            : new Vector4(0.90f, 0.75f, 0.45f, 1f), auditStatus);
        if (ImGui.Button("結果をクリア##audit"))
        {
            auditEntries.Clear();
            while (auditPendingActions.TryDequeue(out _)) Interlocked.Decrement(ref auditPendingCount);
        }
        ImGui.TextDisabled("ログイン中は自動監視します。結果はメモリにのみ保存され、プラグイン再起動で消えます。");
        RefreshSkillAuditSnapshot();
        ImGui.TextDisabled(auditSnapshotReady
            ? $"登録照合：HB{config.SelectedHotbar}以外の{auditCheckedBars}本／登録済Action枠{auditScannedSlots}個"
            : "登録照合：ログインとHUD準備待ち");
        uint job = 0;
        try { if (playerState.IsLoaded) job = playerState.ClassJob.RowId; }
        catch { }
        if (job == 0) job = auditLastJob;
        var entries = auditEntries.Values.Where(x => x.Job == job).ToList();
        var missing = entries.Count(x => AuditMatchKind(x.Job, x.Action) == 0);
        var present = entries.Count(x => AuditMatchKind(x.Job, x.Action) > 0);
        ImGui.Text($"現在ジョブID {job}：記録 {entries.Count}種類 ／ 未登録候補 {missing} ／ 一致 {present}");
        ImGui.Checkbox("未登録候補だけ表示##auditMissingOnly", ref auditOnlyMissing);
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("まだ使用履歴がありません。木人などでスキルを使うと自動的に記録します。");
        }
        else
        {
            var sorted = entries.OrderBy(x => AuditMatchKind(x.Job, x.Action))
                .ThenByDescending(x => x.Count).ThenBy(x => x.Action).ToArray();
            ImGui.BeginChild("##skillAuditResults", new Vector2(0, 200));
            foreach (var item in sorted)
            {
                var kind = AuditMatchKind(item.Job, item.Action);
                if (auditOnlyMissing && kind != 0) continue;
                var missingSource = kind == 0 && AuditIsKnownParadox(item.Job, item.Action);
                var label = kind switch
                {
                    3 => "置換元あり",
                    2 => "登録あり",
                    1 => "変化一致",
                    0 when missingSource => "置換元を確認",
                    0 => "未登録候補",
                    _ => "照合待ち",
                };
                var color = kind == 0 ? new Vector4(1f, 0.55f, 0.40f, 1f)
                    : kind > 0 ? new Vector4(0.45f, 0.90f, 0.56f, 1f)
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                // For Paradox, report the actual assignable source actions,
                // rather than telling users to add the unassignable skill.
                var hint = AuditIsKnownParadox(item.Job, item.Action)
                    ? (kind == 0 ? "  ← ファイア／ブリザドを確認" : "  ← ファイア／ブリザド")
                    : string.Empty;
                var details = AuditActionDetails(item.Action);
                ImGui.PushID((int)item.Action);
                DrawAuditActionIcon(details.Icon);
                ImGui.SameLine(0f, 9f);
                ImGui.BeginGroup();
                ImGui.TextColored(color, $"[{label}] {details.Name}  x{item.Count}{hint}");
                ImGui.TextDisabled($"スキルID: {item.Action}");
                ImGui.EndGroup();
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
        ImGui.TextDisabled("パラドックスは直接登録不可。対象HBのファイア／ブリザドを照合します（Wrath用HBは除外）。");
        ImGui.TextWrapped("注意：クライアント側が成功と返した実行要求を記録します。サーバー確定ログではありません。" +
            "同一ボタンから別スキルへ変化するアクション、レベルによる置換、マクロなどでは未登録候補が誤って出る場合があります。" +
            "疑似ホットバーのキー発光だけでは記録されず、ホットバー配置・スキル実行・発光設定は変更しません。");
    }

    private void DisposeSkillAudit()
    {
        StopSkillAudit();
        try { auditUseActionHook?.Dispose(); } catch { }
        while (auditPendingActions.TryDequeue(out _)) { }
        auditEntries.Clear();
        auditActionDetailsCache.Clear();
    }
}
