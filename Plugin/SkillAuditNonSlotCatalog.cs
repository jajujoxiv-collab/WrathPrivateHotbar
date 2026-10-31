using System;
using System.Collections.Generic;
using System.Text;

namespace WrathPrivateHotbar;

public sealed unsafe partial class Plugin
{
    // PvE Japanese client action names from the user-provided 7.5 list.
    // List is a candidate catalog, not a claim that every entry has been
    // individually verified. Confirm known roots against official job guide.
    // Empty array: do not claim a source HB registration is missing.
    // Nonempty array: at least one assignable base action must be on a non-Wrath HB.
    private static readonly Dictionary<uint, Dictionary<string, string[]>> AuditNonSlotRules = new()
    {
        // ナイト (ClassJob 19)
        [19u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ゲベート"] = Array.Empty<string>(),
            ["グラブカッマー"] = Array.Empty<string>(),
            ["ブレード・オブ・フェイス"] = new[] { "コンフィテオル" },
            ["ブレード・オブ・トゥルース"] = new[] { "コンフィテオル" },
            ["ブレード・オブ・ヴァラー"] = new[] { "コンフィテオル" },
            ["ブレード・オブ・オナー"] = new[] { "インペラトル" },
        },
        // 戦士 (ClassJob 21)
        [21u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["カオティックサイクロン"] = Array.Empty<string>(),
            ["インナーカオス"] = Array.Empty<string>(),
            ["原初の激震"] = Array.Empty<string>(),
            ["ルイネーター"] = Array.Empty<string>(),
        },
        // 暗黒騎士 (ClassJob 32)
        [32u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["レッドデリリアム"] = Array.Empty<string>(),
            ["カマパンス"] = Array.Empty<string>(),
            ["トアクリーバー"] = Array.Empty<string>(),
            ["インペイルメント"] = Array.Empty<string>(),
        },
        // ガンブレイカー (ClassJob 37)
        [37u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["サベッジクロウ"] = Array.Empty<string>(),
            ["ウィケッドタロン"] = Array.Empty<string>(),
            ["ジャギュラーリップ"] = Array.Empty<string>(),
            ["アブドメンテアー"] = Array.Empty<string>(),
            ["アイガウジ"] = Array.Empty<string>(),
            ["ハイパーヴェロシティ"] = Array.Empty<string>(),
            ["フェイテッドブランド"] = Array.Empty<string>(),
            ["ルーラー・オブ・ハート"] = Array.Empty<string>(),
            ["エンド・オブ・ハート"] = Array.Empty<string>(),
        },
        // 学者 (ClassJob 28)
        [28u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["マニフェステーション"] = Array.Empty<string>(),
            ["アクセッション"] = Array.Empty<string>(),
        },
        // 占星術師 (ClassJob 33)
        [33u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["アーゼマの均衡"] = Array.Empty<string>(),
            ["オシュオンの矢"] = Array.Empty<string>(),
            ["ビエルゴの塔"] = Array.Empty<string>(),
            ["ハルオーネの槍"] = Array.Empty<string>(),
            ["世界樹の幹"] = Array.Empty<string>(),
            ["サリャクの水瓶"] = Array.Empty<string>(),
            ["クラウンロード"] = Array.Empty<string>(),
            ["クラウンレディ"] = Array.Empty<string>(),
        },
        // 賢者 (ClassJob 40)
        [40u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["エウクラシア・ドシス"] = new[] { "ドシス" },
            ["エウクラシア・ドシスII"] = new[] { "ドシスII" },
            ["エウクラシア・ドシスIII"] = new[] { "ドシスIII" },
            ["エウクラシア・ディアグノシス"] = new[] { "ディアグノシス" },
            ["エウクラシア・プログノシス"] = new[] { "プログノシス" },
            ["エウクラシア・プログノシスII"] = new[] { "プログノシスII" },
            ["エウクラシア・ディスクラシア"] = new[] { "ディスクラシア" },
        },
        // モンク (ClassJob 20)
        [20u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["闘魂旋風脚"] = Array.Empty<string>(),
            ["蒼気砲"] = Array.Empty<string>(),
            ["天宙脚"] = Array.Empty<string>(),
            ["爆裂脚"] = Array.Empty<string>(),
            ["鳳凰の舞"] = Array.Empty<string>(),
            ["夢幻闘舞"] = Array.Empty<string>(),
            ["真空波"] = Array.Empty<string>(),
        },
        // 竜騎士 (ClassJob 22)
        [22u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["雲蒸竜変"] = Array.Empty<string>(),
            ["竜眼雷電"] = Array.Empty<string>(),
            ["竜眼蒼穹"] = Array.Empty<string>(),
        },
        // 忍者 (ClassJob 30)
        [30u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["幻影野槌"] = Array.Empty<string>(),
            ["口寄せの術・蝦蟇仙"] = Array.Empty<string>(),
            ["是生滅法"] = Array.Empty<string>(),
            ["天理人道"] = Array.Empty<string>(),
            ["風魔手裏剣"] = new[] { "忍術" },
            ["火遁の術"] = new[] { "忍術" },
            ["雷遁の術"] = new[] { "忍術" },
            ["氷遁の術"] = new[] { "忍術" },
            ["風遁の術"] = new[] { "忍術" },
            ["土遁の術"] = new[] { "忍術" },
            ["水遁の術"] = new[] { "忍術" },
            ["劫火滅却の術"] = new[] { "忍術" },
            ["氷晶乱流の術"] = new[] { "忍術" },
        },
        // 侍 (ClassJob 34)
        [34u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["返し波切"] = Array.Empty<string>(),
            ["彼岸花"] = Array.Empty<string>(),
            ["天下五剣"] = Array.Empty<string>(),
            ["乱れ雪月花"] = Array.Empty<string>(),
            ["返し五剣"] = Array.Empty<string>(),
            ["返し雪月花"] = Array.Empty<string>(),
            ["天道五剣"] = Array.Empty<string>(),
            ["天道雪月花"] = Array.Empty<string>(),
            ["天道返し五剣"] = Array.Empty<string>(),
            ["天道返し雪月花"] = Array.Empty<string>(),
        },
        // リーパー (ClassJob 39)
        [39u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ジビトゥクロウ"] = Array.Empty<string>(),
            ["ギャロウズクロウ"] = Array.Empty<string>(),
            ["リターン"] = Array.Empty<string>(),
            ["ヴォイドリーパー"] = Array.Empty<string>(),
            ["クロスリーパー"] = Array.Empty<string>(),
            ["グリムリーパー"] = Array.Empty<string>(),
            ["ハーベストムーン"] = new[] { "ソウルソウ" },
            ["レムールスライス"] = new[] { "ストークスウェーズ" },
            ["レムールサイズ"] = new[] { "シーフスウェーズ" },
            ["サクリフィキウム"] = Array.Empty<string>(),
            ["エクスジビトゥ"] = Array.Empty<string>(),
            ["エクスギャロウズ"] = Array.Empty<string>(),
            ["エクスギロティン"] = Array.Empty<string>(),
            ["ペルフェクティオ"] = Array.Empty<string>(),
        },
        // ヴァイパー (ClassJob 41)
        [41u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["弐の牙〖猛襲〗"] = Array.Empty<string>(),
            ["弐の牙〖疾速〗"] = Array.Empty<string>(),
            ["参の牙〖側撃〗"] = Array.Empty<string>(),
            ["参の牙〖側裂〗"] = Array.Empty<string>(),
            ["参の牙〖背撃〗"] = Array.Empty<string>(),
            ["参の牙〖背裂〗"] = Array.Empty<string>(),
            ["弐の大牙〖猛襲〗"] = Array.Empty<string>(),
            ["弐の大牙〖疾速〗"] = Array.Empty<string>(),
            ["参の大牙〖乱撃〗"] = Array.Empty<string>(),
            ["参の大牙〖乱裂〗"] = Array.Empty<string>(),
            ["蛇尾撃"] = Array.Empty<string>(),
            ["蛇尾閃"] = Array.Empty<string>(),
            ["双牙連撃"] = Array.Empty<string>(),
            ["双牙乱撃"] = Array.Empty<string>(),
            ["双牙連閃"] = Array.Empty<string>(),
            ["双牙乱閃"] = Array.Empty<string>(),
            ["飛蛇連尾撃"] = Array.Empty<string>(),
            ["飛蛇乱尾撃"] = Array.Empty<string>(),
            ["祖霊の牙〖壱〗"] = Array.Empty<string>(),
            ["祖霊の牙〖弐〗"] = Array.Empty<string>(),
            ["祖霊の牙〖参〗"] = Array.Empty<string>(),
            ["祖霊の牙〖肆〗"] = Array.Empty<string>(),
            ["祖霊の大蛇牙"] = Array.Empty<string>(),
            ["祖霊の蛇〖壱〗"] = Array.Empty<string>(),
            ["祖霊の蛇〖弐〗"] = Array.Empty<string>(),
            ["祖霊の蛇〖参〗"] = Array.Empty<string>(),
            ["祖霊の蛇〖肆〗"] = Array.Empty<string>(),
        },
        // 吟遊詩人 (ClassJob 23)
        [23u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ブラストアロー"] = Array.Empty<string>(),
        },
        // 機工士 (ClassJob 31)
        [31u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["オーバーロード・ルーク"] = Array.Empty<string>(),
            ["デトネーター"] = Array.Empty<string>(),
            ["クイーン・アームパンチ"] = Array.Empty<string>(),
            ["クイーン・ローラーダッシュ"] = Array.Empty<string>(),
            ["クイーン・パイルバンカー"] = Array.Empty<string>(),
            ["クイーン・コレダー"] = Array.Empty<string>(),
            ["エクスカベーター"] = new[] { "回転のこぎり" },
        },
        // 踊り子 (ClassJob 38)
        [38u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["スタンダードフィニッシュ"] = new[] { "スタンダードステップ" },
            ["テクニカルフィニッシュ"] = new[] { "テクニカルステップ" },
            ["インプロビゼーション・フィニッシュ"] = new[] { "インプロビゼーション" },
            ["ティラナ"] = new[] { "テクニカルステップ" },
            ["フィニシングムーブ"] = Array.Empty<string>(),
            ["暁の舞い"] = Array.Empty<string>(),
            ["薔薇のアンボワテ"] = Array.Empty<string>(),
            ["小鳥のアントルシャ"] = Array.Empty<string>(),
            ["緑葉のジュテ"] = Array.Empty<string>(),
            ["金冠のピルエッテ"] = Array.Empty<string>(),
        },
        // 黒魔道士 (ClassJob 25)
        [25u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["パラドックス"] = new[] { "ファイア", "ブリザド" },
        },
        // 赤魔道士 (ClassJob 35)
        [35u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["エンリポスト"] = new[] { "リポスト" },
            ["エンツヴェルクハウ"] = new[] { "ツヴェルクハウ" },
            ["エンルドゥブルマン"] = new[] { "ルドゥブルマン" },
            ["エンムーリネ・アン"] = new[] { "ムーリネ" },
            ["エンムーリネ・ドゥ"] = new[] { "ムーリネ" },
            ["エンムーリネ・トロワ"] = new[] { "ムーリネ" },
            ["エンルプリーズ"] = new[] { "ルプリーズ" },
            ["ヴァルフレア"] = Array.Empty<string>(),
            ["ヴァルホーリー"] = Array.Empty<string>(),
            ["スコーチ"] = Array.Empty<string>(),
            ["レゾリューション"] = Array.Empty<string>(),
            ["ノーブルインパクト"] = new[] { "ジョルガ", "インパクト" },
        },
        // 召喚士 (ClassJob 27)
        [27u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ルビー・ルイン"] = Array.Empty<string>(),
            ["トパーズ・ルイン"] = Array.Empty<string>(),
            ["エメラルド・ルイン"] = Array.Empty<string>(),
            ["ルビー・ルインラ"] = Array.Empty<string>(),
            ["トパーズ・ルインラ"] = Array.Empty<string>(),
            ["エメラルド・ルインラ"] = Array.Empty<string>(),
            ["ルビー・ルインガ"] = Array.Empty<string>(),
            ["トパーズ・ルインガ"] = Array.Empty<string>(),
            ["エメラルド・ルインガ"] = Array.Empty<string>(),
            ["ルビー・リチュアル"] = Array.Empty<string>(),
            ["トパーズ・リチュアル"] = Array.Empty<string>(),
            ["エメラルド・リチュアル"] = Array.Empty<string>(),
            ["ルビー・アウトバースト"] = Array.Empty<string>(),
            ["トパーズ・アウトバースト"] = Array.Empty<string>(),
            ["エメラルド・アウトバースト"] = Array.Empty<string>(),
            ["ルビー・ディザスター"] = Array.Empty<string>(),
            ["トパーズ・ディザスター"] = Array.Empty<string>(),
            ["エメラルド・ディザスター"] = Array.Empty<string>(),
            ["ルビー・カタストロフィ"] = Array.Empty<string>(),
            ["トパーズ・カタストロフィ"] = Array.Empty<string>(),
            ["エメラルド・カタストロフィ"] = Array.Empty<string>(),
            ["アストラルインパルス"] = Array.Empty<string>(),
            ["アストラルフレア"] = Array.Empty<string>(),
            ["デスフレア"] = Array.Empty<string>(),
            ["ウィルムウェーブ"] = Array.Empty<string>(),
            ["アク・モーン"] = Array.Empty<string>(),
            ["サモン・フェニックス"] = Array.Empty<string>(),
            ["霊泉の炎"] = Array.Empty<string>(),
            ["煉獄の炎"] = Array.Empty<string>(),
            ["再生の炎"] = Array.Empty<string>(),
            ["エンキンドル・フェニックス"] = Array.Empty<string>(),
            ["不死鳥の翼"] = Array.Empty<string>(),
            ["火焔"] = Array.Empty<string>(),
            ["リヴァレーション"] = Array.Empty<string>(),
            ["クリムゾンサイクロン"] = Array.Empty<string>(),
            ["クリムゾンストライク"] = Array.Empty<string>(),
            ["マウンテンバスター"] = Array.Empty<string>(),
            ["スリップストリーム"] = Array.Empty<string>(),
            ["サモン・ソルバハムート"] = Array.Empty<string>(),
            ["アンブラルインパルス"] = Array.Empty<string>(),
            ["アンブラルフレア"] = Array.Empty<string>(),
            ["ソルフレア"] = Array.Empty<string>(),
            ["エンキンドル・ソルバハムート"] = Array.Empty<string>(),
            ["光芒"] = Array.Empty<string>(),
            ["エクソダス"] = Array.Empty<string>(),
        },
        // ピクトマンサー (ClassJob 42)
        [42u] = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["グリーンエアロ"] = new[] { "レッドファイア" },
            ["ブルーウォータ"] = new[] { "レッドファイア" },
            ["グリーンエアロラ"] = new[] { "レッドファイラ" },
            ["ブルーウォタラ"] = new[] { "レッドファイラ" },
            ["イエローストーン"] = new[] { "シアンブリザド" },
            ["マゼンタサンダー"] = new[] { "シアンブリザド" },
            ["イエローストンラ"] = new[] { "シアンブリザラ" },
            ["マゼンタサンダラ"] = new[] { "シアンブリザラ" },
            ["ピクトポンポン"] = new[] { "ピクトアニマル" },
            ["ピクトウィング"] = new[] { "ピクトアニマル" },
            ["ピクトクロー"] = new[] { "ピクトアニマル" },
            ["ピクトファング"] = new[] { "ピクトアニマル" },
            ["イマジンポンポン"] = new[] { "イマジンアニマル" },
            ["イマジンウィング"] = new[] { "イマジンアニマル" },
            ["イマジンクロー"] = new[] { "イマジンアニマル" },
            ["イマジンファング"] = new[] { "イマジンアニマル" },
            ["ピクトハンマー"] = new[] { "ピクトウェポン" },
            ["イマジンハンマー"] = new[] { "イマジンウェポン" },
            ["ハンマーブラッシュ"] = new[] { "ハンマースタンプ" },
            ["ハンマーポリッシュ"] = new[] { "ハンマースタンプ" },
            ["ピクトスカイ"] = new[] { "ピクトスケープ" },
            ["イマジンスカイ"] = new[] { "イマジンスケープ" },
            ["マディーンレトリビューション"] = new[] { "モーグリストリーム" },
        },
    };

    // Explicit backstops for Pictomancer IDs observed in an in-game report.
    // Other actions are resolved by enumerating the installed client Action
    // Excel sheet once, and indexing each known unassignable name by row ID.
    // Avoid depending on localized names at the time a skill is used.
    private static readonly Dictionary<(uint Job, uint Action), string[]> AuditNonSlotIdRules = new()
    {
        // Paladin PvE: Confiteor (16459) -> Blade of Faith/Truth/Valor,
        // while Imperator (36921) -> Blade of Honor. These are NOT the same slot.
        // Explicit IDs prevent one-frame fallthrough while a job-specific
        // localized Action-name index is being constructed.
        [(19u, 25748u)] = new[] { "コンフィテオル" },
        [(19u, 25749u)] = new[] { "コンフィテオル" },
        [(19u, 25750u)] = new[] { "コンフィテオル" },
        [(19u, 36922u)] = new[] { "インペラトル" },
        [(42u, 34664u)] = new[] { "ピクトアニマル" },  // ピクトポンポン
        [(42u, 34668u)] = new[] { "ピクトウェポン" }, // ピクトハンマー
        [(42u, 34669u)] = new[] { "ピクトスケープ" }, // ピクトスカイ
    };

    // Generated at runtime from the same Action sheet used for icon/name
    // display. A keyed pair prevents an ID from a different job silently
    // suppressing a valid missing-skill warning.
    private readonly Dictionary<(uint Job, uint Action), string[]> auditNonSlotRuntimeIds = new();
    private bool auditNonSlotIdIndexBuilt;
    private long auditNonSlotIdIndexRetryAt;
    private string auditNonSlotIdIndexStatus = "未作成";

    private static string AuditNormalizeName(string name) =>
        name.Trim().Normalize(NormalizationForm.FormKC).Replace(" ", "").Replace("　", "");

    private void AuditEnsureNonSlotIdIndex()
    {
        if (auditNonSlotIdIndexBuilt || Environment.TickCount64 < auditNonSlotIdIndexRetryAt)
            return;
        try
        {
            // Build a reverse lookup from all job-specific candidate names.
            // Multiple jobs can use the same name; retain every (job, sources).
            var rulesByName = new Dictionary<string, List<(uint Job, string[] Sources)>>(StringComparer.Ordinal);
            foreach (var jobRules in AuditNonSlotRules)
            {
                foreach (var rule in jobRules.Value)
                {
                    var key = AuditNormalizeName(rule.Key);
                    if (!rulesByName.TryGetValue(key, out var bucket))
                    {
                        bucket = new List<(uint, string[])>();
                        rulesByName.Add(key, bucket);
                    }
                    bucket.Add((jobRules.Key, rule.Value));
                }
            }
            var sheet = data.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (sheet == null)
            {
                auditNonSlotIdIndexStatus = "Actionデータ読込待ち";
                auditNonSlotIdIndexRetryAt = Environment.TickCount64 + 10000;
                return;
            }
            var resolved = new Dictionary<(uint Job, uint Action), string[]>();
            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!rulesByName.TryGetValue(AuditNormalizeName(name), out var candidates))
                    continue;
                foreach (var pair in candidates)
                    resolved[(pair.Job, row.RowId)] = pair.Sources;
            }
            auditNonSlotRuntimeIds.Clear();
            foreach (var rule in resolved)
                auditNonSlotRuntimeIds[rule.Key] = rule.Value;
            auditNonSlotIdIndexStatus = $"Actionデータから{auditNonSlotRuntimeIds.Count}件のジョブ別IDを照合";
            auditNonSlotIdIndexBuilt = true;
        }
        catch (Exception ex)
        {
            auditNonSlotIdIndexStatus = $"ID索引の構築に失敗: {ex.GetType().Name}";
            auditNonSlotIdIndexRetryAt = Environment.TickCount64 + 10000;
        }
    }

    private bool AuditTryNonSlotAction(uint job, uint action, out string[] sources)
    {
        sources = Array.Empty<string>();
        // Explicitly reported IDs remain an independent fallback even if the
        // index fails or the client is not using Japanese Action.Name.
        if (AuditNonSlotIdRules.TryGetValue((job, action), out var explicitSources))
        {
            sources = explicitSources;
            return true;
        }
        AuditEnsureNonSlotIdIndex();
        if (auditNonSlotRuntimeIds.TryGetValue((job, action), out var resolvedSources))
        {
            sources = resolvedSources;
            return true;
        }
        // Fallback for an old/partial game sheet: localized names of the
        // *actual observed action*. IDs are prioritized when available.
        if (!AuditNonSlotRules.TryGetValue(job, out var names)) return false;
        var text = AuditNormalizeName(AuditActionDetails(action).Name);
        if (names.TryGetValue(text, out var found))
        {
            sources = found;
            return true;
        }
        foreach (var rule in names)
        {
            if (AuditNormalizeName(rule.Key) != text) continue;
            sources = rule.Value;
            return true;
        }
        return false;
    }

    private bool AuditHasRegisteredBase(string[] sources)
    {
        foreach (var source in sources)
        {
            // Stored IDs stay stable even while native action adjustments change.
            // This fallback is job-specific via AuditTryNonSlotAction's catalog.
            if (source == "コンフィテオル" && auditStoredActionIds.Contains(16459u)) return true;
            // Requiescat (7383) is upgraded to Imperator (36921) by
            // Paladin Lv96 trait; either may be stored on the hotbar.
            if (source == "インペラトル" &&
                (auditStoredActionIds.Contains(36921u) || auditStoredActionIds.Contains(7383u)))
                return true;
            if (auditStoredActionNames.Contains(AuditNormalizeName(source))) return true;
        }
        return false;
    }
}
