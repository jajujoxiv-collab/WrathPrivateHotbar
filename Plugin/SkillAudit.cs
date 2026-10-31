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
    private uint auditSnapshotJob;
    // Scan often enough to catch short native combo changes, without doing
    // 9x12 hotbar reads on every FF14 frame.
    private const long AuditSnapshotIntervalMs = 200;
    private const long AuditActionTriggeredScanMinMs = 80;
    // Never show an unmatched red warning from a single stale snapshot.
    // Successful matches are shown immediately; only missing transitions
    // need debounce across multiple distinct, successful snapshots.
    private const long AuditMissingConfirmationMs = 1500;
    private const int AuditMissingConfirmationSnapshots = 5;
    private string auditStatus = "自動監視の準備待ち";
    private readonly Dictionary<(uint Job, uint Action), SkillAuditEntry> auditEntries = new();
    private readonly HashSet<uint> auditStoredActionIds = new();
    private readonly HashSet<string> auditStoredActionNames = new(StringComparer.Ordinal);
    private readonly HashSet<uint> auditNativeActionIds = new();
    // Learn game-native transformations of an actually installed hotbar action.
    // Only trust the original native trampoline, never WrathCombo's adjusted
    // output. The mapping is retained across proc / combo transitions, and
    // scoped to job + source-slot action so it cannot match an unrelated job.
    private readonly Dictionary<(uint Job, uint Stored), HashSet<uint>> auditObservedNativeVariants = new();
    private const int AuditMaxNativeVariantsPerSource = 16;
    private int auditRecordedVariantEdges;
    private bool auditNativeObserverAvailable;

    private bool auditSnapshotReady;
    private int auditScannedSlots;
    private int auditCheckedBars;
    private uint auditLastJob;
    private const int AuditMaxDistinctPerJob = 300;

    // Job-specific unassignable actions and their base-slot relationships are
    // defined separately in SkillAuditNonSlotCatalog.cs.

    private sealed class SkillAuditEntry
    {
        public required uint Job;
        public required uint Action;
        public int Count;
        public long FirstAt;
        public long LastAt;
        // Presentation-only hysteresis. Preserve action counts independently
        // from whether a transient 'missing' state has been confirmed.
        public int ConfirmedDisplayKind = -1;
        public long MissingSinceAt;
        public int ConsecutiveMissingSnapshots;
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
        auditSnapshotReady = false;
        auditSnapshotJob = 0;
        foreach (var entry in auditEntries.Values)
        {
            entry.ConfirmedDisplayKind = -1;
            entry.MissingSinceAt = 0;
            entry.ConsecutiveMissingSnapshots = 0;
        }
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
        // Always refresh hotbar assignments while monitoring, not just when
        // the settings window is opened. This is essential for transient
        // native action upgrades/combo states to be seen in real gameplay.
        // Regular 200ms snapshot, even while the config window is closed.
        if (shouldMonitor) RefreshSkillAuditSnapshot();

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
                entry = new SkillAuditEntry
                {
                    Job = job, Action = actionId, FirstAt = Environment.TickCount64
                };
                auditEntries.Add(key, entry);
            }
            entry.Count++;
            entry.LastAt = Environment.TickCount64;
        }
        // Catch the original native transformation close to an action event.
        // The 80ms floor bounds the work even under repeated rapid inputs.
        if (handled > 0 && shouldMonitor)
            RefreshSkillAuditSnapshot(actionTriggered: true);
    }

    // Scan ALL 10 normal (non-cross) hotbars, regardless of HUD visibility.
    // Do NOT include the selected WrathCombo source hotbar. A skill assigned
    // only on that source is always considered a missing candidate.
    private void RefreshSkillAuditSnapshot(bool actionTriggered = false)
    {
        var now = Environment.TickCount64;
        var minInterval = actionTriggered
            ? AuditActionTriggeredScanMinMs : AuditSnapshotIntervalMs;
        if (now - auditLastSnapshotAt < minInterval) return;
        auditLastSnapshotAt = now;
        auditSnapshotReady = false;
        auditStoredActionIds.Clear();
        auditStoredActionNames.Clear();
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
            uint jobId = 0;
            try { jobId = playerState.ClassJob.RowId; } catch { }
            // If WrathCombo's native original is not available, do not
            // mistake a Wrath-adjusted result for a FF14 native transform.
            auditNativeObserverAvailable = config.NativeTrampolineEnabled &&
                wrathNativeOriginal != null;
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
                    var sourceName = AuditActionDetails(stored).Name;
                    if (!string.IsNullOrWhiteSpace(sourceName))
                        auditStoredActionNames.Add(AuditNormalizeName(sourceName));
                    // Game-native transformations can display a different ID
                    // from the stored action. May still miss old proc states.
                    var native = GetNativePulseAction(stored);
                    if (native != 0)
                    {
                        auditNativeActionIds.Add(native);
                        if (jobId != 0 && auditNativeObserverAvailable && native != stored)
                            AuditRememberNativeVariant(jobId, stored, native);
                    }
                }
            }
            auditSnapshotReady = true;
            auditSnapshotJob = jobId;
            AuditUpdateConfirmedDisplayStates(now, jobId);
        }
        catch (Exception ex)
        {
            auditStatus = $"配置の読み取りに失敗: {ex.GetType().Name}";
        }
    }

    // Called only from framework thread, not from the native action hook.
    // Keep bounded memory usage even if a client patch produces unusual IDs.
    private void AuditRememberNativeVariant(uint job, uint stored, uint native)
    {
        if (job == 0 || stored == 0 || native == 0 || stored == native) return;
        var key = (job, stored);
        if (!auditObservedNativeVariants.TryGetValue(key, out var variants))
        {
            variants = new HashSet<uint>();
            auditObservedNativeVariants[key] = variants;
        }
        if (variants.Count >= AuditMaxNativeVariantsPerSource) return;
        if (variants.Add(native)) auditRecordedVariantEdges++;
    }

    private bool AuditMatchesObservedNativeVariant(uint job, uint observed)
    {
        // Only an action STILL registered on a non-Wrath bar can satisfy the
        // audit. This prevents stale learned mappings from concealing missing
        // skills after a player changes the layout.
        foreach (var stored in auditStoredActionIds)
            if (auditObservedNativeVariants.TryGetValue((job, stored), out var variants) &&
                variants.Contains(observed))
                return true;
        return false;
    }

    // Return -1=waiting, 0=missing placeable, 1/2=exact/native match,
    // 3=verified base stored, 4=missing assignable base, 5=unassignable,
    // 6=previously observed native transformation of a currently stored slot.
    // Mapped transformations must be checked before the *current* native slot
    // animation, or status can oscillate when a multi-action combo advances.
    private int AuditMatchKind(uint job, uint action)
    {
        if (!auditSnapshotReady || job == 0 || job != auditSnapshotJob) return -1;
        if (auditStoredActionIds.Contains(action)) return 2;
        // Level-96 Paladin upgrades the assignable Requiescat (7383) into
        // Imperator (36921); the underlying stored hotbar slot may keep 7383.
        // Do not let Imperator flicker as "missing" when that base is assigned.
        if (job == 19u && action == 36921u && auditStoredActionIds.Contains(7383u))
            return 3;
        // Warrior (ClassJob 21): mastered assignable skills may keep their
        // pre-upgrade IDs in stored hotbar slots even after an action fires.
        // Inner Beast (49) -> Fell Cleave (3549) at Lv54.
        // Steel Cyclone (51) -> Decimate (3550) at Lv60.
        // Only accept an upgrade when its ACTUAL pre-upgrade slot is assigned
        // on a non-Wrath hotbar; never blanket-suppress a missing action.
        if (job == 21u &&
            ((action == 3549u && auditStoredActionIds.Contains(49u)) ||
             (action == 3550u && auditStoredActionIds.Contains(51u))))
            return 3;
        // A currently displayed or previously observed *native* adjustment
        // is stronger evidence than an incomplete manually curated source list.
        // Consult it before the 213 candidate catalog to avoid false "source
        // missing" warnings for legitimate chained combo replacements.
        if (auditNativeActionIds.Contains(action)) return 1;
        if (AuditMatchesObservedNativeVariant(job, action)) return 6;
        if (AuditTryNonSlotAction(job, action, out var sources))
        {
            if (sources.Length == 0) return 5;
            return AuditHasRegisteredBase(sources) ? 3 : 4;
        }
        return 0;
    }

    // Reconcile ONLY after a successful fresh snapshot, not once per ImGui
    // render. A transient raw mismatch (e.g. combo phase change before the
    // native-ID observer catches up) must NOT briefly turn a green row red.
    // - Positive/excluded match: show immediately and reset missing debounce.
    // - Missing (0/4): hide as 'pending' until sustained for 1.5 seconds AND
    //   observed in >=5 distinct snapshots. Re-entering a valid match resets
    //   the entire timer, including on the Nth execution of the same action.
    private void AuditUpdateConfirmedDisplayStates(long now, uint job)
    {
        if (!auditSnapshotReady || job == 0) return;
        foreach (var entry in auditEntries.Values)
        {
            if (entry.Job != job) continue;
            var raw = AuditMatchKind(job, entry.Action);
            if (raw is 0 or 4)
            {
                if (entry.MissingSinceAt == 0)
                {
                    entry.MissingSinceAt = now;
                    entry.ConsecutiveMissingSnapshots = 1;
                }
                else
                {
                    entry.ConsecutiveMissingSnapshots++;
                }
                entry.ConfirmedDisplayKind =
                    now - entry.MissingSinceAt >= AuditMissingConfirmationMs &&
                    entry.ConsecutiveMissingSnapshots >= AuditMissingConfirmationSnapshots
                        ? raw : -1;
            }
            else
            {
                entry.MissingSinceAt = 0;
                entry.ConsecutiveMissingSnapshots = 0;
                entry.ConfirmedDisplayKind = raw;
            }
        }
    }

    // UI intentionally does not re-evaluate raw per-frame matches: the latest
    // confirmed snapshot is the only source of display state. This avoids the
    // old v99 bug where FirstAt grace applied just once in the skill lifetime.
    private int AuditDisplayMatchKind(SkillAuditEntry item) =>
        auditSnapshotReady && auditSnapshotJob == item.Job
            ? item.ConfirmedDisplayKind : -1;

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
        ImGui.TextDisabled("判定エンジン v2.0.5.100 ／ 全ジョブのFF14標準変化IDを継続学習（試験機能）");
        ImGui.TextDisabled($"WrathCombo用HB{config.SelectedHotbar}を自動除外 ／ 他の9本を確認（非表示HBも対象）");
        AuditEnsureNonSlotIdIndex();
        ImGui.TextDisabled($"ジョブ別ID索引: {auditNonSlotIdIndexStatus}");
        ImGui.TextDisabled($"ゲーム標準変化の学習: {auditRecordedVariantEdges}組 ／ 元の取得: {(auditNativeObserverAvailable ? "利用可能" : "利用不可（既知ルールで判定）")}");
        ImGui.TextDisabled("登録照合: 200ms周期・使用直後にも更新 ／ 未登録警告: 1.5秒以上の継続確認後のみ表示");
        ImGui.TextColored(auditHookEnabled
            ? new Vector4(0.45f, 0.90f, 0.56f, 1f)
            : new Vector4(0.90f, 0.75f, 0.45f, 1f), auditStatus);
        if (ImGui.Button("結果をクリア##audit"))
        {
            auditEntries.Clear();
            auditObservedNativeVariants.Clear();
            auditRecordedVariantEdges = 0;
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
        var missing = entries.Count(x => AuditDisplayMatchKind(x) is 0 or 4);
        var present = entries.Count(x => AuditDisplayMatchKind(x) is 1 or 2 or 3 or 6);
        var excluded = entries.Count(x => AuditDisplayMatchKind(x) == 5);
        var awaiting = entries.Count(x => AuditDisplayMatchKind(x) == -1);
        ImGui.Text($"現在ジョブID {job}：記録 {entries.Count}種類 ／ 未登録候補 {missing} ／ 一致 {present} ／ 直接登録不可 {excluded} ／ 確認中 {awaiting}");
        ImGui.Checkbox("未登録候補だけ表示##auditMissingOnly", ref auditOnlyMissing);
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("まだ使用履歴がありません。木人などでスキルを使うと自動的に記録します。");
        }
        else
        {
            var sorted = entries.OrderBy(x => AuditDisplayMatchKind(x))
                .ThenByDescending(x => x.Count).ThenBy(x => x.Action).ToArray();
            ImGui.BeginChild("##skillAuditResults", new Vector2(0, 200));
            foreach (var item in sorted)
            {
                var kind = AuditDisplayMatchKind(item);
                if (auditOnlyMissing && kind is not (0 or 4)) continue;
                var knownTransform = AuditTryNonSlotAction(item.Job, item.Action, out var sources);
                var label = kind switch
                {
                    5 => "直接登録不可（対象外）",
                    4 => "置換元未配置（元スキルを確認）",
                    3 => "置換元あり",
                    6 => "標準変化を確認済",
                    2 => "登録あり",
                    1 => "変化一致",
                    0 => "未登録候補",
                    _ => "照合待ち",
                };
                var color = kind is 0 or 4 ? new Vector4(1f, 0.55f, 0.40f, 1f)
                    : kind is 1 or 2 or 3 or 6 ? new Vector4(0.45f, 0.90f, 0.56f, 1f)
                    : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                var hint = knownTransform && sources.Length > 0
                    ? $"  ← 置換元: {string.Join(" / ", sources)}"
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
        ImGui.TextDisabled("登録できない置換スキルは元の登録アクションを確認します。元が未検証のものは警告から除外します。");
        ImGui.TextDisabled("x回数はクライアント実行要求数です。未登録の発生回数ではありません。");
        ImGui.TextWrapped("注意：クライアント側が成功と返した実行要求を記録します。サーバー確定ログではありません。" +
            "置換対応は日本語PvEジョブガイドに基づく候補リストのため、全アクションの完全保証ではありません。" +
            "ホットバーに直接置けないアクションと、元スキルの配置漏れを分けて表示します。" +
            "疑似ホットバーのキー発光だけでは記録されず、ホットバー配置・スキル実行・発光設定は変更しません。");
    }

    private void DisposeSkillAudit()
    {
        StopSkillAudit();
        try { auditUseActionHook?.Dispose(); } catch { }
        while (auditPendingActions.TryDequeue(out _)) { }
        auditEntries.Clear();
        auditObservedNativeVariants.Clear();
        auditActionDetailsCache.Clear();
    }
}
