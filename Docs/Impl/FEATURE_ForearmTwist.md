# FEATURE — 前腕ツイスト分散ボーン（Forearm Twist Distribution）検討書

- **ステータス:** 初期実装済み（NDMF ビルド＋プレビュー。Bake は未対応）— 実装内容は [§13](#13-実装メモ初期実装) を参照
- **作成日:** 2026-11-21
- **関連:** [overview.md](./overview.md) / [ARCHITECTURE.md](./ARCHITECTURE.md)（ADR-01〜08、§5.1〜§5.5、§7.3）

この文書は、`LowerArm` にツイスト補助ボーンを追加する機能について、実現可能性・実装コスト・動作安定性・懸念事項・実装ロジックを検討したものです。腕をひねったときに衣装が肘で急にねじれる問題を、肘から手首までのなめらかなねじれに変えることが目的です。

---

## 目次

0. [結論サマリー](#0-結論サマリー)
1. [要求の整理と解釈](#1-要求の整理と解釈)
2. [現象のメカニズム](#2-現象のメカニズム)
3. [方式検討](#3-方式検討)
4. [リグ構成（コンストレイント設計）](#4-リグ構成コンストレイント設計)
5. [ウェイト再分配ロジック](#5-ウェイト再分配ロジック)
6. [データモデル・UI](#6-データモデルui)
7. [既存モジュールへの変更点](#7-既存モジュールへの変更点)
8. [動作安定性・懸念事項](#8-動作安定性懸念事項)
9. [実装コスト見積もり](#9-実装コスト見積もり)
10. [テスト計画](#10-テスト計画)
11. [実装前スパイク（要検証事項）](#11-実装前スパイク要検証事項)
12. [残課題・確認事項](#12-残課題確認事項)
13. [実装メモ（初期実装）](#13-実装メモ初期実装)

---

## 0. 結論サマリー

| 観点 | 評価 | 要点 |
| :--- | :--- | :--- |
| **実現可能性** | **高** | VRChat 標準コンストレイントだけで組めます。`VRCAimConstraint` で「曲げだけを拾う基準ボーン」を作り、`VRCRotationConstraint`（または `VRCParentConstraint`）でそのボーンと `DistalSource` の間の回転を分配します（§3, §4）。Animator・FX・PhysBone は使いません。既存の分類器・Bindpose 逆算・4 ボーン統合・プレビュー基盤のほとんどを再利用できます。 |
| **実装コスト** | **中（約 12〜17 人日）** | コア計算とテストで約 5 日、パイプライン・Bake・UI で約 4 日、プレビューで約 2 日、実機検証で約 2〜3 日です（§9）。 |
| **動作安定性** | **中〜高** | 数式としては「レスト姿勢でのズレ 0」「曲げ方向の誤差 0」「4 ボーン超過は外部ボーンに触れずに必ず解決できる」が成り立ちます（§4.4, §5.4）。不確実なのは **VRChat 実機で前腕のロールが実際にどう配分されるか**（IK・デスクトップ・FBT の違い）と、**袖の PhysBone が元のロールで動き続けること**の 2 点です（§8）。 |
| **推奨** | **実装を推奨（既定は OFF の追加機能として）** | まずスパイク S1〜S3（§11）で VRChat 実機の挙動と SDK・MA の仕様を確認してから、本実装に入ります。 |

---

## 1. 要求の整理と解釈

### 1.1 要求（原文の要旨）

1. `LowerArm` にツイスト補助ボーンを追加します。肘での急なねじれを、肘から手首にかけてのなめらかなねじれにします。
2. 衣装のウェイトを、補助ボーンと元のボーンに配分します。
3. ツイストボーンは VRC コンストレイントで制御します。
4. **オプション:** 腕のひねりに袖をどれだけ追従させるかを変えられるようにします。
5. **前提:** 衣装の袖は肘の曲げだけに追従させます。

### 1.2 本書での解釈

| 項目 | 解釈 |
| :--- | :--- |
| 「袖は肘の曲げのみに追従」 | **肘付近の袖はロール（ひねり）成分を 0 にして、曲げ（スイング）成分だけに追従させる**、と解釈します。JointKeeper の既存の肘補助ボーン（`JK_Helper_*Elbow`）も、曲げだけを拾う基準ボーンに追従させます（§4.5）。 |
| ひねり追従量（オプション） | **手首側でのロール追従率 $f \in [0, 1]$** とします。$f=1$ なら手首で元の `LowerArm` と同じだけロールします（ねじれが前腕全体に分散するだけで、手首部分の見た目は今と同じ）。$f=0$ なら袖全体がロールせず、曲げだけに追従します。 |
| 対象部位 | **肘（前腕）だけ**を対象にします。下腿のツイストも同じ仕組みで扱えますが、今回の範囲には入れません（§12）。 |
| 対象メッシュ | 既存の `JointKeeperComponent` の対象 SMR（所有権解決 §4.3 に従う）とします。**衣装と素体の両方を対象にします**（§1.4）。 |
| 想定環境 | **VR での見た目の改善が目的**です。デスクトップや FBT ごとの IK の違いは、検討の対象にしません。 |

### 1.3 推奨用途と注意書き（UI・README に表示する）

> [!IMPORTANT]
> **推奨：手首から先（`Hand` 以降）にウェイトが乗っていない衣装**（長袖・七分袖・アームカバーなど）での使用を推奨します。
> 手袋や、袖口が手の甲まで覆う衣装のように `Hand` にもウェイトがある衣装では、ひねり追従率を 1.0 未満にすると、手首でねじれの段差が出ます（§8-B1）。
> このような衣装や素体メッシュでは、**ひねり追従率を 1.0 のまま**使ってください。1.0 なら手首部分は今と同じ動きになり、肘のねじれだけが前腕全体に分散されます。

- 実装では、対象 SMR の頂点のうち `Hand` 系（`ChildHumanoid` の配下）にウェイトを持つ頂点を数えます。1 つ以上あって $f < 1$ の場合は、Inspector に上の注意書きを警告として表示します。

### 1.4 決定事項（2026-11-21 レビュー）

| 項目 | 決定 |
| :--- | :--- |
| IK | VR での改善が目的なので、IK の種類ごとの違いは深く追わない。VR で一度だけ実機確認する（S1 を縮小）。 |
| 推奨用途 | 手首から先にウェイトが乗っていない衣装を推奨し、注意書きとして表示する（§1.3）。 |
| 袖の PhysBone | **警告だけ**にする（ルートの付け替えはしない）。 |
| Bake モード | **後で確認する**。初期実装は NDMF ビルド（非破壊）を対象にする。 |
| 素体への適用 | **対応する**。ツイストは素体への効果が大きい（§1.5）。 |
| 肘補助ボーンとの関係 | **共存させる。排他にはしない**（§1.6）。 |
| ひねり追従量 $f$ の定義 | **手首端でのロール追従率 $f \in [0, 1]$** とし、肘端は常に 0 に固定する（肘のキャンディラッパー解消と $H$ との連続性を保証）。 |
| 既定の区間数 $n$ | **$n = 3$**（中間ボーン 2 本、両腕で Constraint +6）を既定とする。 |
| 既存ツイストリグ検出時の既定 | **SMR の実ウェイト単位**で検出し、非ゼロウェイトが含まれる SMR のみ「スキップ＋警告」を既定とする（§8-B2）。 |
| 下腿ツイストへの拡張（C2） | UI・設定は肘専用でシンプルに保ち、コア計算（`ForearmTwistRigBuilder` / `ForearmTwistRedistributor`）の引数のみ `JointId` 汎用とする。 |

### 1.5 素体メッシュへの適用

- **現在の実装:** 素体にもそのまま適用できます。`JointKeeperTargetCollector` は素体を区別しません。アバタールートに `AutoDetect` で置くと素体も対象になります（Inspector にもその旨の案内があります）。素体の SMR を `ManualList` で指定することもできます。素体のボーンはアバターの Humanoid ボーンそのものなので、MA の統合を経由する衣装よりも処理の前提が単純です。
- **ツイスト機能でも同じ扱いにします。** 素体と衣装を同じ設定にすると、$N$・$T_j$ は `RigSignatureKey` で**共有されます**。$\tau$ は頂点の位置だけで決まる同じ関数なので、素体と袖が同じ割合でねじれます。これで §8-B5（ロールしない袖を素体が突き抜ける問題）も、根本から起きにくくなります。**衣装だけに適用するより、素体と衣装の両方に同じ設定で適用することを推奨**します。
- **素体での注意点:**
  1. **手首から先のウェイト:** 素体は必ず `Hand` にもウェイトを持っているので、$f = 1$ で使います（§1.3）。$f < 1$ のときは警告を出します。
  2. **既存のツイストボーン:** 素体がアバター自身のツイストボーン（Constraint 駆動の `LowerArm_Twist` など）に乗っている場合は、§8-B2 の検出によって既定でスキップされます。ツイストボーンが無いアバターでの効果が最も大きくなります。
  3. **肘の押し出し量:** 素体の肘補助ボーンの押し出し（`pushAt90Deg`）を衣装より大きくすると、素体が衣装を突き抜ける原因になります。素体の押し出し量は、衣装と同じかそれ以下にしてください。衣装と別の値にしたい場合は、所有権解決（ARCHITECTURE §4.3）を使って、素体専用のコンポーネント（`ManualList`）に分けます。
  4. **メッシュの複製:** 素体は頂点数と BlendShape が多いですが、処理は CPU の頂点ループだけなので、数万頂点でも問題になる時間はかかりません。メッシュの複製は今の肘処理と 1 回にまとめて行います（SMR ごとに 1 回だけ）。
- **二重適用の防止（追加対策）:** 生成したボーンをすでに `bones` に含んでいる場合は、SMR 全体ではなく **「関節 × 機能」単位** で処理済みとしてスキップして警告します（例：`JK_Helper_LeftElbow` があれば左肘のボリューム補正のみスキップし、左膝や前腕ツイストの追加適用は妨げない。`JK_TwistBase_LeftElbow` があれば左前腕ツイストのみスキップ）。素体やボディスーツの一部だけ Bake 確定した後に別部位を追加する運用でも安全です。なお、マーカー用コンポーネントを付与する場合は **必ず `VRC.SDKBase.IEditorOnly` を実装**し、`JointKeeperPass` の `finally` で確実に除去します。

### 1.6 肘の補助ボーンとツイストボーンの共存

**共存できます。排他制御は必要ありません。** 本設計は、両方が有効な状態を前提にしています（ADR-10、§4.5、§5.1）。

| 観点 | 共存時の扱い |
| :--- | :--- |
| 役割の分担 | 肘補助ボーン $H$ は**曲げたときの体積**を保ちます。ツイストボーン $N$・$T_j$ は**ひねったときのねじれ**を分散します。変形の成分が違うので干渉しません。 |
| コンストレイント | $H$ の遠位ソースを `LowerArm` から $N$ に切り替えます。$N$ は `LowerArm` と曲げ成分が完全に同じなので、$H$ の曲げと押し出しの挙動は変わりません。ロールの拾い方だけが $\alpha\varphi$ から 0 になります。 |
| ウェイトの処理順 | ①肘の処理（$G_P$ と $G_D$ の一部を $H$ へ） → ②ツイストの処理（残った剛体遠位ウェイトを $N$・$T_j$ へ）の順に固定します。②は $G_P$ と $H$ のウェイトに触れないので、①の結果（角モーメントの保存）はそのまま残ります。 |
| 4 ボーン制限 | 肘の付近は $\tau \approx 0$ なので、遠位ウェイトはほぼ $N$ の 1 本だけです。典型的な頂点は `UpperArm`・$H$・$N$ の 3 本で、4 本に収まります。超えた場合も §5.4 の丸めで必ず解決できます。 |
| 片方だけ有効 | 肘だけ有効なら今と同じ動作です。ツイストだけ有効なら $H$ は生成されず、$N$・$T_j$ だけが生成されます。どちらの組み合わせも、同じコードの経路で扱えます。 |
| 生成の順序 | $H$ が $N$ を参照するので、$N$ を先に生成します（§8-B6）。 |

> [!NOTE]
> 「ひねり」の元になるのは **`LowerArm` の、`UpperArm`（厳密には `LowerArm.parent`）に対するロール** です。手首（`Hand`）のロールのうち `LowerArm` に配分されない分は、この機能では扱いません（手首側のキャンディラッパー対策は §8 の拡張案）。

---

## 2. 現象のメカニズム

### 2.1 なぜ肘でねじれるのか

- Humanoid の前腕ひねり（`Forearm Twist In-Out` マッスル、または VRChat IK が解いた手首のロール）が入ると、`LowerArm` は肢軸のまわりに $\varphi$ だけロールします。
- 衣装頂点のうち、`LowerArm` 側のグループ（$G_D$）に乗った分は $\varphi$ だけ回り、`UpperArm` 側のグループ（$G_P$）に乗った分は回りません。
- 一般的な衣装では、$G_P$ と $G_D$ が混ざる範囲は**肘のまわりの狭い帯**だけです。そのため、ロール差 $\varphi$ の全量がこの帯に集中して、「肘での急なねじれ」と「帯の細り（キャンディラッパー）」が同時に起きます。

### 2.2 LBS（線形ブレンドスキニング）によるロール帯の細り

軸が同じでロール角だけが $\Delta$ 違う 2 本のボーンを、ウェイト $w$ と $1-w$ で混ぜたとします。半径 $r$ の頂点がブレンド後にとる半径は次のとおりです。

$$r' = r\sqrt{1 - 2w(1-w)(1-\cos\Delta)}, \qquad r'_{\min} = r\cos\frac{\Delta}{2}\quad (w = 0.5)$$

前腕のロール $\varphi$ を $n$ 区間に等分すると、隣り合うボーン同士のロール差は $\Delta = f\varphi / n$ になります。最悪点（$w = 0.5$）の細り率 $1-\cos(\Delta/2)$ は次のとおりです（$f = 1$）。

| `LowerArm` ロール $\varphi$ | $n=1$（現状と同じ：肘帯に集中） | $n=2$ | **$n=3$（推奨既定）** | $n=4$ |
| :--- | :--- | :--- | :--- | :--- |
| 60° | 13.4 % | 3.4 % | **1.5 %** | 0.9 % |
| 90° | 29.3 % | 7.6 % | **3.4 %** | 1.9 % |
| 120° | 50.0 % | 13.4 % | **6.0 %** | 3.4 % |

- **結論 1:** 補助ボーン 1 本（＝ `LowerArm` との 2 本で線形補間するだけ）では、ねじれ位置が前腕の中ほどへ移るだけです。$\varphi = 90°$ では、前腕の中ほどが約 30 % 細ります。**中間ボーンが 2 本以上（$n \ge 3$）必要**です。
- **結論 2:** **各頂点は、隣り合う 2 本のツイストボーンだけ**で補間します（3 本以上を混ぜても細りは改善せず、スロットを無駄に使うだけです）。

---

## 3. 方式検討

### 3.1 技術要件

| ID | 要件 | 理由 |
| :--- | :--- | :--- |
| R1 | 基準ボーン $N$ は、`LowerArm` の**曲げ（スイング）にだけ完全に追従**し、ロールには追従しない | 「袖は肘の曲げのみに追従」（§1.2）。曲げ方向の誤差があると、肘で衣装がずれます。 |
| R2 | 中間ボーン $T_j$ のロールは $k_j\varphi$（$0 < k_j \le f$）で、**曲げ成分は $N$ と完全に一致** | 曲げ成分がずれると、ツイストしていなくても前腕の途中で形が崩れます。 |
| R3 | レスト姿勢でスキニング結果が変わらない（ゼロポップ） | 既存の不変条件 5 を引き継ぎます。 |
| R4 | VRChat ネイティブコンストレイントだけを使い、Animator・FX・PhysBone は使わない | 既存の ADR-01 の方針（Quest 対応・低負荷）。 |
| R5 | 4 ボーン制限を守り、外部ボーン（PhysBone 等）のウェイトを改変しない | 既存の不変条件 2・4。 |

### 3.2 候補比較

| 案 | 構成 | R1（曲げのみ） | R2 | 評価 |
| :--- | :--- | :--- | :--- | :--- |
| **A（採用）** | $N$ = `DistalSource` 配下の `VRCAimConstraint`（肢軸方向に向ける＋上方向は上腕の生のヒンジ軸 $\hat{h}$）。$T_j$ = $N$ と `DistalSource` の 2 ソース `VRCRotationConstraint` / `VRCParentConstraint`（ウェイト $1-k_j,\ k_j$） | **厳密**（スイングとツイストの分解そのもの） | **厳密**（§4.4） | 必要な Constraint は腕 1 本あたり 1 + $(n-1)$ 個です。Aim は ADR-01（`VRCParentConstraint` に統一）の例外になります。 |
| B | $N$ = `LowerArm` の子に `VRCRotationConstraint`（ソース `UpperArm`、ロール軸だけ有効） | ×（オイラー軸を部分的に固定すると、曲げ角が大きいときに軸どうしが干渉します） | △ | 曲げが 120° を超えるとロールの取り出しが崩れるので、採用しません。 |
| C | `LowerArm` と既存の肘補助ボーン $H$ の 2 本だけで、前腕全体を線形補間する | × | × | §2.2 のとおり、前腕の中ほどが最大 30 % 細るので、採用しません。 |
| D | $N$ = `UpperArm` と `LowerArm` の 2 ソース `ParentConstraint`（$\alpha$ ブレンド） | ×（曲げ角も $\alpha$ 倍になります） | × | 曲げに追従しないので、採用しません。 |
| E | 一般的なアバターのツイスト方式（`LowerArm_Twist` が `Hand` のロールを分配する） | − | − | 手首側の対策です。肘のねじれは解消しません（`LowerArm` 自身のロールは残ります）。拡張案としてだけ扱います（§8-C3）。 |

### 3.3 追加 ADR 案

| ID | 決定事項 | 採択内容（案） |
| :--- | :--- | :--- |
| **ADR-09** | 前腕ツイスト基準ボーン・中間ボーンのコンストレイント種別 | **曲げ専用の基準ボーン $N$ には `VRCAimConstraint` を使います（ADR-01 の例外）。** $N$ は `DistalSource` の子（`localPosition = 0`）として配置し `armStretch` にも自動追従させます。中間ボーン $T_j$ は $N$ の子で位置が固定されるため 3 DoF の `VRCRotationConstraint`（または `VRCParentConstraint`）とし、スパイク S2 で最終確定します。 |
| **ADR-10** | 既存の肘補助ボーンの遠位ソース | **ツイスト機能が有効な肘では、補助ボーン $H$ の `DistalSource` を $N$ に切り替えます**（既存の `DistalSourceMode.CustomOverride` と同じ仕組みです）。$H$ は今、$\alpha\varphi$ だけロールしています。これを 0 にして、肘まわりを「曲げのみに追従」で揃えます。 |

---

## 4. リグ構成（コンストレイント設計）

### 4.1 生成ボーン階層

※ 本節の `ProximalSource`・`DistalSource` は、`TwistBoneClassifier` が解決したツイスト切替前の実ソースを指します。通常は `UpperArm`（または直列 `UpperArm_Twist`）と `LowerArm` ですが、**AvatarPoseSystem（APS）等の休止中ポーズラッパーが存在する場合は `UpperArm_*_Const` / `LowerArm_*_Const`** になります。これにより APS のポーズ固定モードでも破綻せず動作します。

```text
ProximalSource（UpperArm / 直列 UpperArm_Twist / APS 時は UpperArm_*_Const）
├── DistalSource（LowerArm / APS 時は LowerArm_*_Const）
│   ├── Hand ...
│   ├── JK_TwistAim_LeftElbow          [新規・Constraint なし]  Aim ターゲット
│   └── JK_TwistBase_LeftElbow (= N)   [新規・VRCAimConstraint]  k = 0（localPosition = 0）
│       ├── JK_Twist_LeftElbow_1 (= T_1) [新規・VRC Constraint] k = f/n
│       ├── JK_Twist_LeftElbow_2 (= T_2) [新規・VRC Constraint] k = 2f/n
│       └── ...（f < 1 のときは T_n も生成。f = 1 なら T_n = DistalSource そのもの）
└── JK_Helper_LeftElbow   (= H)        [既存・VRCParentConstraint] DistalSource を N に変更
```

- **$N$ を `DistalSource` 配下に置く理由:** Unity Humanoid は既定で `armStretch = 0.05` を持ち、VR IK で手を伸ばした際に `LowerArm.localPosition` が 1〜2 cm 移動します。$N$ を `ProximalSource` の子にすると位置が追従せず Aim 方向の傾きと袖の引き残りが生じますが、**`DistalSource` の子（`localPosition = 0`）に置けば追加 Constraint ゼロで位置が完全追従**します。`JK_TwistAim` は兄弟、`WorldUpTransform`（`ProximalSource`）は祖先となるため循環依存も発生しません。
- **ボーン名:** 既存の `JK_Helper_*` と同じく、`VrcConstraintRigBuilder.MakeUniqueChildName` で重複を避けます。
- **生成数（腕 1 本、$n = 3$、$f = 1$）:** Transform 4 本（$N$、$T_1$、$T_2$、Aim ターゲット）、Constraint 3 個です。両腕では Transform 8 本、Constraint 6 個になります。

### 4.2 基準ボーン $N$（`VRCAimConstraint`）

前提として、関節中心を $J$（= `DistalSource.position`）、**`LowerArm` のロール軸**を $\hat{a}$（§4.6）、ヒンジ軸を $\hat{h}$（既存の `JointFrame.HingeAxis`。ADR-06 の実測値）、前腕の長さを $L_L$ とします。$\hat{h}_\perp = \text{normalize}(\hat{h} - (\hat{h}\cdot\hat{a})\hat{a})$ です。

| 項目 | 設定値 | 意図 |
| :--- | :--- | :--- |
| 親 | **`DistalSource`**（`localPosition = Vector3.zero`） | VR IK の `armStretch` による肘位置の伸長にも位置ズレゼロで自動追従します。 |
| `position` | $J$ | 肘を中心に回ります。 |
| `rotation`（レスト） | **`DistalSource.rotation` と同じ** | $N$・$T_j$・`DistalSource` のレスト回転を揃えます。こうすると回転オフセットがすべて恒等になり、§4.4 の分配が厳密になります。 |
| Aim ターゲット | `JK_TwistAim`（`DistalSource` の子、位置 $J + \hat{a}L_L$） | **ロール軸上にある**ので、`DistalSource` がロールしても動きません。`Hand` を直接使わないのは、Bake モードで衣装側の Armature に `Hand` が無い場合に備えるためです。 |
| `AimAxis` | `DistalSource.InverseTransformDirection(â)` | レスト姿勢でターゲットを向く局所軸です。 |
| `UpAxis` | `DistalSource.InverseTransformDirection(ĥ⊥)` | ロール軸に直交化したヒンジ方向を上方向として使います。 |
| `WorldUp` | `ObjectRotationUp`、`WorldUpTransform = ProximalSource`、**`WorldUpVector = ProximalSource.InverseTransformDirection(ĥ)`**（直交化前の生のヒンジ軸） | 上腕座標系に固定した生のヒンジ軸 $\hat{h}$ を上方向にします。$R(\hat{h}, \theta)\hat{h} = \hat{h}$ より、Aim 方向 $R\hat{a}$ への直交射影が常に $R(\hat{h}, \theta)\hat{h}_\perp$ と厳密一致するため、**$\hat{h} \not\perp \hat{a}$ のアバター（A ポーズ・キャリングアングル等）でも純粋曲げ時の偽ロールが厳密 0** になります。 |
| `RotationOffset` / `RotationAtRest` | 0 / レスト局所回転 | 上の設定から、レスト姿勢での Aim の結果は `DistalSource` のレスト回転と厳密に一致します。 |
| `Locked` / `IsActive` | `true` / `true` | 既存の不変条件 9 と同じです。 |

> **結果:** $N$ の回転 = `ProximalSource` の回転 × 「ヒンジ軸まわりの曲げ角だけの回転」 × レストでの相対回転。これは `DistalSource` からロール成分だけを取り除いた姿勢（スイングとツイストの分解のスイング側）そのものです。

### 4.3 中間ボーン $T_j$（`VRCRotationConstraint` / `VRCParentConstraint`）

$N$ が `DistalSource` の子になったことで $T_j$ の位置も常に `DistalSource` のロール軸上へ自動追従するため、$T_j$ は **3 DoF の `VRCRotationConstraint`（位置計算なし・軽量・位置ドリフト 0）** で実現できます（`VRCParentConstraint` と比べ、スパイク S2 で挙動・負荷を確認して確定します）。

| 項目 | 設定値 |
| :--- | :--- |
| 親 | $N$ |
| `position` | $P_j = J + \hat{a}\,s_j L_L$（$s_j$ は表示用の配置で、変形には影響しません。§4.4 の注） |
| `rotation`（レスト） | `DistalSource.rotation` と同じ |
| `Sources[0]` | $N$、`Weight` $= 1-k_j$（回転オフセット 0） |
| `Sources[1]` | `DistalSource`、`Weight` $= k_j$（回転オフセット 0） |
| 軸 | 回転全軸有効（`VRCParentConstraint` 採用時は位置も全軸有効＋`ParentPositionOffset` 設定） |

### 4.4 厳密性の確認

ワールドでの変化量を $\Delta_N = N N_0^{-1}$、$\Delta_L = L L_0^{-1}$ とします。$N$ は $L$ からロールだけを除いたものなので、$\Delta_L = \Delta_N \cdot R(\hat{a}_0, \varphi)$ が成り立ちます（$\hat{a}_0$ はレスト時のロール軸）。レスト回転は $N_0 = L_0 = T_{j,0}$ で揃えてあるので、クォータニオン Slerp の両側不変性から次の式が成り立ちます。

$$T_j = \mathrm{Slerp}(\Delta_N T_0,\ \Delta_L T_0,\ k_j) = \Delta_N\,R(\hat{a}_0,\ k_j\varphi)\,T_0$$

- **曲げ成分は $N$ と完全に一致**し、ロールだけが $k_j\varphi$ になります（R2 を満たします）。
- 回転はすべてロール軸 $\hat{a}$ 上の点を中心にしているので、**$T_j$ をロール軸上のどこに置いても、スキニング結果は同じ**です（$s_j$ の配置は見た目と、ギズモの分かりやすさのためだけのものです）。
- VRC の 2 ソース回転ブレンドが Slerp ではなく正規化線形補間（NLerp）の場合、半角補間によりロール配分は次式になります：
  $$\varphi_{\text{nlerp}} = 2\,\arctan\!\left(\frac{k\sin(\varphi/2)}{(1-k)+k\cos(\varphi/2)}\right)$$
  $\varphi = 90°,\ k = 1/3$ でも $29.27°$（誤差わずか **$0.73°$**）、$\varphi = 120°,\ k = 1/3$ でも $38.2°$（誤差 $1.8°$）にとどまり、単調かつ連続なため **静的な $k_j$ 逆算補正は不要** です（実装を簡素化できます）。

### 4.5 既存の肘補助ボーン $H$ との統合（ADR-10）

- 今の $H$ は `Sources = (ProximalSource: 1-α, DistalSource: α)` なので、**回転が $\alpha\varphi$ だけロールしています**（位置のアンカーは肢軸上にあるので、位置はロールの影響を受けません。ARCHITECTURE §5.4）。
- ツイスト機能が有効なら `DistalSource = N` にします。$N_0 = L_0$ なので、`ParentPositionOffset` と `ParentRotationOffset` は元の `DistalSource` のときと同じ値になり、曲げの挙動は完全に同じです。違いはロールが 0 になることだけです。
- `RigSignatureKey` には `DistalSourceId` が含まれているので、ツイストを有効にした衣装と無効の衣装では、自動的に別の $H$ になります（ADR-02 と同じ）。
- Constraint Depth: $N$ = 1、$T_j$ = 2、$H$ = 2 です（APS 時はポーズラッパーが Depth 1 のため +1。ADR-01 の「ソースの Depth の最大値 + 1」のとおり）。

### 4.6 ロール軸 $\hat{a}$ の決定

1. **第 1 優先（実測）:** `HingeAxisSampler` と同じ方法で、`Left/Right Forearm Twist In-Out` マッスルを微小に変化させて `LowerArm` の回転軸を測ります。曲げ軸を測るのと同時に行えるので、追加コストはほとんどありません。
2. **第 2 優先:** $\text{normalize}(P_{\text{Hand}} - P_{\text{LowerArm}})$。
3. 1 と 2 のなす角が 5° を超える場合は、情報ログに出します。VRChat の IK がどちらの軸でロールするかは、スパイク S1 で確認します。

> **副産物（診断）:** 同じサンプリングおよび `animator.avatar.humanDescription.foreArmTwist` の参照から「ひねりマッスル 1.0 あたり `LowerArm` が何度ロールするか」も分かります。Inspector に「このアバターでは前腕のひねりの約 X % が `LowerArm` のロールになります」と表示できます。X ≈ 0 のアバターでは、この機能の効果がありません。

---

## 5. ウェイト再分配ロジック

新しく `ForearmTwistRedistributor` を作ります。既存の `WeightRedistributor` と同じく、Unity のシーンに依存しない純粋計算として実装し、ヒープ確保もしません。

### 5.1 処理順序と対象ボーン

1. 今の肘の処理（`WeightRedistributor`。$H$ への分配）を先に行います。
2. その結果の `workingWeights` に対して、**剛体遠位ボーン群 $G_D^{\text{rigid}} = \{ b \in G_D \mid \neg\text{IsConstraintDriven}(b) \}$** に乗っているウェイト $W_R$ だけを、ツイストボーンのチェーンへ振り替えます。
   - `LowerArm`（および MA `ScaleProxy`）と、その剛体の子孫（袖口のボタンボーンなど）が対象です。どれも `LowerArm` と同じ運動をします（ADR-05 の根拠と同じ）。
   - **アニメーション対象の小物の注意:** 腕時計やカフス等の剛体子ボーンが FX アニメーション（スケール 0 での表示切替等）で動かされている場合、$G_D^{\text{rigid}}$ へ吸収するとアニメーションが効かなくなります。該当ボーンは `excludedBones` での除外を案内し、レストスキニング行列が `DistalReferenceBoneIndex` と大きく異なる剛体子ボーン（`ScaleProxy` を除く）は警告対象とします。
   - **Constraint で駆動される $G_D$ ボーン（既存の `LowerArm_Twist` など）は対象にしません。** 既存のツイストリグの配分を壊さないためです（§8-B2）。
   - $G_P$、$H$、$G_O$（PhysBone 等）のウェイトは、ビット単位で変えません。

### 5.2 ツイスト追従率 $\tau$ の算出（頂点ごと）

$$z = (P_v - J)\cdot\hat{a}, \qquad s = \text{clamp01}\!\left(\frac{z / L_L - s_{\text{start}}}{\max(s_{\text{end}} - s_{\text{start}},\ 10^{-4})}\right), \qquad \tau = f \cdot c(s)$$

- $c(s)$ は分配カーブです。既定は線形 $c(s) = s$（実際の前腕で、橈骨が尺骨のまわりを回るときのねじれの分布に近い形です）。オプションで $s^p$ または SmoothStep を選べます。
- $s_{\text{start}}$ の既定は 0.0（肘）、$s_{\text{end}}$ の既定は 1.0（手首）です。`startRatio` と `endRatio` のスライダー範囲が重複するため、ゼロ除算ガード（および Inspector 側での `endRatio >= startRatio + 0.05f` クランプ）を入れます。
- $z < 0$ の頂点（肘より上腕側にあるが、$G_D$ のウェイトを持つ頂点）は $\tau = 0$ になり、$N$ に乗ります。こうして肘の帯は曲げにだけ追従します。
- 頂点のワールド位置と元のウェイトだけで決まる関数なので、**シームの一貫性（不変条件 6）を引き継ぎます**。

### 5.3 ノットへの割り当て

ノットを $k_j = f\cdot j / n$（$j = 0..n$、$k_0$ = $N$、$k_n$ = $f = 1$ なら `DistalSource`、それ以外は $T_n$）とします。$\tau / f \equiv c(s)$ なので、$c(s) \in [0, 1]$ から直接ノット区間を計算します（$f \to 0$ でのゼロ除算を構造上回避）。

$$j = \min(\lfloor c(s)\cdot n \rfloor,\ n-1), \quad u = c(s)\cdot n - j$$
$$w_{(j)} = (1-u)\,W_R, \qquad w_{(j+1)} = u\,W_R$$

- 元の $G_D^{\text{rigid}}$ のスロット（1 本以上）を、最大 2 本のチェーンボーンのスロットに置き換えます。合計 $W_R$ は保存されます。
- $u < 10^{-5}$ または $u > 1 - 10^{-5}$ の場合は、1 本にまとめます。
- $f \le 10^{-5}$ の場合は、全頂点が $N$ だけになります（チェーンは $N$ だけで、中間ボーン $T_j$ は生成しません）。

### 5.4 4 ボーン制限

- 1 本以上のスロットを最大 2 本に置き換えるので、スロットの増加は最大 +1 です。
- **超過したときは、$\tau$ を最も近いノットに丸めて 1 本にします。** スロット数は元と同じかそれ以下になるので、**外部ボーンに触れずに必ず解決できます**。
  - 丸めによるロールの誤差は、最大で $f\varphi / (2n)$ です（$n = 3,\ \varphi = 90°$ で 15°）。局所的な段差になりますが、元の状態（肘で 90° の段差）よりは十分に小さくなります。
  - 閾値 `twistSnapThreshold`（既定 0.15）より $u$ が端に近い場合は、超過していなくても丸めて、スロットを節約します（任意）。
- 丸めた頂点は統計 `TwistQuantizedCount` に数えて、ヒートマップの診断モードに表示します。

### 5.5 Bindpose とレスト姿勢の不変性

- チェーンボーン $b$ の Bindpose は $M_{\text{bind},b} = B_b^{-1}\,S_{D}^{\text{ref}}$ です（$S_D^{\text{ref}}$ は剛体遠位の基準ボーンのスキニング行列。既存の `DistalReferenceBoneIndex` を使います）。
- レスト姿勢では $S_b = S_D^{\text{ref}}$ になるので、基準ボーンに乗っていた頂点のズレは厳密に 0 です。
- $G_D^{\text{rigid}}$ の中で bind pose がそろっていない場合（剛体の子ボーンが bind pose から動かされている場合）は、ズレが残ります。既存の 5f（レスト姿勢ズレの実測、0.5 mm で警告）でそのまま検出できます。

### 5.6 追加する不変条件

| # | 内容 |
| :--- | :--- |
| 11 | **ツイスト総量の保存:** 各頂点で、チェーンボーンのウェイトの合計が、処理前の $W_R$ と一致すること（$\le 10^{-5}$）。 |
| 12 | **チェーンの隣接性:** 1 頂点が参照するチェーンボーンは、隣り合う 2 本以下であること。 |
| 13 | **曲げ専用の基準:** レスト姿勢と純粋な曲げ（$\varphi = 0$）の姿勢で、$N$、$T_j$、`LowerArm` のスキニング行列が一致すること（RigMath のテスト）。 |

---

## 6. データモデル・UI

### 6.1 データモデル（案）

`JointConfig` は膝と共用しています。前腕ツイストは肘だけの機能なので、**別のクラスにして、肘の左右ペアと同じリンク規約（ARCHITECTURE §3.3）に従わせます**。

```csharp
[Serializable]
public class ForearmTwistConfig
{
    public bool enabled = false;                 // 既定 OFF（既存ビルド結果を変えない）
    [Range(0f, 1f)]   public float twistFollow = 1.0f;     // f: 手首端でのロール追従率
    [Range(1, 4)]     public int   segments = 3;           // n: 区間数（中間ボーン n-1 本）
    [Range(-0.3f, 0.5f)] public float startRatio = 0.0f;   // s_start（前腕長比）
    [Range(0.3f, 1.2f)]  public float endRatio = 1.0f;     // s_end
    public TwistCurve curve = TwistCurve.Linear;           // Linear / Power / SmoothStep
    [Range(1f, 3f)]   public float curvePower = 1.0f;
    public bool bendOnlyElbowHelper = true;      // ADR-10: H の DistalSource を N に切替
    public bool applyEvenIfExistingTwistRig = false;       // §8-B2
    [Range(0f, 0.5f)] public float twistSnapThreshold = 0.15f;
}

// JointKeeperComponent に追加
public ForearmTwistConfig leftForearmTwist  = new ForearmTwistConfig();
public ForearmTwistConfig rightForearmTwist = new ForearmTwistConfig();
// 左右リンクは既存 unlinkElbowLeftRight に従う（全項目がスカラーなので符号反転は不要）
```

- 既存のシーンやプレハブでは、新しいフィールドが既定値（`enabled = false`）で読み込まれます。**後方互換が保たれます**。

### 6.2 Inspector（段階的開示に追加）

- **通常表示（肘セクション）:** 「前腕ツイスト分散」トグルと、「ひねり追従率」スライダー（0 = 曲げのみ、1 = 手首で元どおり）。
- **詳細設定:** 区間数、開始・終了位置、カーブ、「肘補助ボーンを曲げのみ追従にする」、既存ツイストリグがあっても適用、丸め閾値。
- **診断:** ロール軸の算出元（実測／Hand 方向）、`LowerArm` のロール配分率（§4.6）、既存ツイストリグの検出結果、丸められた頂点の数。
- **プレビュー:** 既存の「前腕ツイスト角スライダー（-90°〜+90°）」をそのまま使います。ヒートマップに「ツイスト追従率 $\tau$」モードを追加します。

---

## 7. 既存モジュールへの変更点

| モジュール | 変更内容 | 規模 |
| :--- | :--- | :--- |
| `Runtime/JointSettings.cs`・`JointKeeperComponent.cs` | `ForearmTwistConfig` の追加、`ResolveTwistConfig(JointId)`（左右リンクの解決） | 小 |
| `Editor/Core/HingeAxisSampler.cs` | ひねりマッスルによるロール軸の実測と、ロール配分率の取得を追加 | 小 |
| `Editor/Core/RigMath.cs` | `EvaluateAimConstraint`（VRC Aim と同じ式）、ノットの算出の純粋関数 | 小〜中 |
| **新規** `Editor/Core/ForearmTwistRigBuilder.cs` | Aim ターゲット・$N$・$T_j$ の生成（`VRCAimConstraint` + `VRCRotationConstraint` / `VRCParentConstraint`） | 中 |
| **新規** `Editor/Core/ForearmTwistRedistributor.cs` | §5 のウェイト振り替え・4 ボーン丸め・統計 | 中 |
| `Editor/Core/TwistBoneClassifier.cs` | 既存の前腕ツイストリグの検出（Constraint 駆動の $G_D$ ボーンで、`Hand` に依存するもの）を、診断結果として返す | 小 |
| `Editor/Core/VrcConstraintRigBuilder.cs` | $H$ の遠位ソースに $N$ を受け取れるようにする（今の引数のままで済む見込み） | 極小 |
| `Editor/Plugin/JointKeeperPass.cs` | 肘の処理の後にツイストの処理を追加。$N$ を先に生成して $H$ の遠位ソースにする（遅延生成の順序。§8-B6） | 中 |
| `Editor/Plugin/JointKeeperBuildState.cs`・`RigSignatureKey.cs` | ツイストリグの共有キー（`JointId`、`Proximal`、`LowerArm`、量子化した $k_j$）。$N$ は設定に関係なく、同じ腕なら共有 | 小 |
| `Editor/Core/JointKeeperBaker.cs` | Bake・Revert・Re-Bake の対象に、ツイストボーンを含める（`createdHelperBones` に登録）。衣装側の Armature に配置 | 中 |
| `Editor/UI/JointKeeperPreviewPose.cs`・`JointKeeperRenderFilter.cs` | 影の $N$・$T_j$ を RigMath で評価。ツイストで振り替えたウェイトをプレビュー用メッシュに反映 | 中 |
| `Editor/UI/JointKeeperEditor.cs`・`JointKeeperHeatmapDrawer.cs`・ローカライズ JSON | UI・診断・$\tau$ ヒートマップ・日英の文言 | 中 |

---

## 8. 動作安定性・懸念事項

重要度は **A**（設計の前提に関わる）、**B**（品質に影響するので対策が必要）、**C**（既知の制限・拡張案）の 3 段階です。

### A. 前提に関わる不確実性

| # | 懸念 | 内容 | 対策 |
| :--- | :--- | :--- | :--- |
| A1 | **VR での前腕ロールの量**（重要度は低め） | 対象は VR だけと決めました（§1.4）。IK の種類ごとの違いは追いません。仕組みはロール量に依存しないので、量が変わっても破綻はしません。 | VR で 1 回だけ確認します（スパイク S1）。Inspector にはロール配分率を参考値として表示します（§4.6）。 |
| A2 | **VRC コンストレイントの API と評価** | `VRCAimConstraint` の `WorldUp = ObjectRotationUp` のフィールド名と挙動、Depth 2 のチェーンが同じフレーム内で解決されるか（1 フレーム遅れが出ないか）、$T_j$ に `VRCRotationConstraint` と `VRCParentConstraint` のどちらが適しているか。なお NLerp 時の誤差は 90° で 0.73° 未満のため補正は不要（§4.4）。 | **スパイク S2** で実機・SDK API を確認します。 |

### B. 品質に影響する懸念

| # | 懸念 | 内容 | 対策 |
| :--- | :--- | :--- | :--- |
| B1 | **$f < 1$ のとき手首に段差ができる** | ねじれは消えず、場所が移るだけです。$f < 1$ にすると、袖口（ロール $f\varphi$）と手・手袋（ロール $\varphi$ 以上）の間に、$(1-f)\varphi$ の段差ができます。 | 「手首から先にウェイトが乗っていない衣装」を推奨用途とし、注意書きを表示します（§1.3）。`Hand` にウェイトを持つ頂点があって $f < 1$ の場合は、Inspector で警告します。素体は $f = 1$ で使います（§1.5）。 |
| B2 | **既存の前腕ツイストリグがあるアバター・衣装** | アバター素体が `LowerArm_Twist`（Constraint 駆動）を持っていても、他アバター向けの長袖衣装 SMR には `LowerArm` しかウェイトが乗っていないことが多いため、アバター単位でスキップすると衣装まで適用されなくなります。 | **対象 SMR の非ゼロ頂点ウェイトに Constraint 駆動の $G_D$ ボーンが含まれる場合のみ** 警告を出してスキップします（`applyEvenIfExistingTwistRig` で強制適用）。素体側の既存リグを壊さず、衣装側には自動適用できます。 |
| B3 | **袖の PhysBone が元のロールのまま動く** | PhysBone のルートが `LowerArm` の子にあると、揺れる部分（フリルなど）は $\varphi$ のロールで回り続けます。その結果、まわりの袖（$\tau < 1$）との間でずれます。$G_O$ のウェイトは変えないので、ここは直りません。 | **決定：警告だけにします**（§1.4）。対象 SMR のウェイトに、ルートが $G_D$ 配下にある PhysBone のボーンが含まれていれば、Inspector とビルドログに警告を出します。ルートの付け替えはしません。 |
| B4 | **Aim の上方向の特異点** | 前腕の向きがヒンジ軸と平行になると、上方向が決まりません。Humanoid の肘は 2 自由度なので通常は起きませんが、IK が肘に解剖学的でない回転を入れた場合は近づくことがあります。 | ヒンジ軸を使うので、平行になるのは「前腕が真横（ヒンジ方向）に 90° 曲がる」場合だけで、実用上は起きません。スパイク S1 で、IK が極端な姿勢になったときのフリップの有無を確認します。 |
| B5 | **素体メッシュとの貫通** | 前腕の断面は楕円形です（手首側は平たい）。素体がロールしているのに、ぴったりした袖がロールしないと、楕円の向きがずれて、手首付近で素体が袖を突き抜けることがあります。 | **素体にも同じ設定で適用するのが最も有効**です。ツイストボーンが共有されて、素体と袖が同じ割合でねじれます（§1.5）。そのうえで $f = 1$ を既定にし、プレビューで素体も表示して確認します。 |
| B6 | **生成の順序（遅延生成との整合）** | $H$ の遠位ソースを $N$ にするには、$H$ より先に $N$ が必要です。一方、ADR-03（遅延生成）では、影響頂点が 0 ならボーンを作りません。 | 肘とツイストのウェイト計算を、どちらも仮のインデックスで先に済ませます。どちらかの影響頂点が 1 以上なら $N$ を生成してから $H$ を生成します。ツイストの影響頂点が 0 なら、$H$ は従来どおり元の `DistalSource` をソースにします。 |
| B7 | **Bake モードと MA Merge Armature**（**後で確認**。初期実装の範囲外） | Bake モードでは、ボーンを衣装側の Armature に置きます。MA の `MergeArmatureHook.RetainBoneReferences` は全 `VRCConstraintBase` の全 `ObjectReference` フィールド（`Sources`・`WorldUpTransform` 含む）を走査してボーンを保持することをソース確認済みです。`MA Scale Adjuster` 併用時は、既存の `extraBoneScales` 処理で $N$・$T_j$ の Bindpose へ実効スケールを織り込みます。 | **スパイク S3**（Bake 対応時に実ビルドで確認）。 |
| B8 | **AAO との共存（ソース確認済み）** | AAO の `ComponentInfos.VRCSDK.cs`（`VRCConstraintInformation` / `VRCWorldUpConstraintInformation`）が `Sources` と `WorldUpTransform` の両方を `AddDependency` へ登録していることを確認済みのため、**`JK_TwistAim` も `WorldUpTransform` も Trace and Optimize で削除されません**。 | 統合テストで念のため最終確認します。 |
| B9 | **パフォーマンスランク** | Constraint が両腕で +6 個（$n = 3$）、ボーンが +8 本、Depth の最大値が 2 になります。 | 既存の肘・膝補助ボーン（最大 4 個）と合わせても、一般的なアバターの Constraint 数に比べて小さい追加です。区間数 $n$ を 2 にすると、両腕で +4 個に減らせます。 |
| B10 | **4 ボーン丸めによる局所的な段差** | 丸めた頂点では、ロールが最大 $f\varphi/(2n)$ ずれます。 | 丸めた頂点をヒートマップで確認できるようにします。元のボーン数が多い衣装で頻発する場合は、区間数を増やすと段差の幅が小さくなります。 |

### C. 既知の制限・拡張案

| # | 内容 |
| :--- | :--- |
| C1 | VRCFury Armature Link の衣装は、今と同じく対象外です（ARCHITECTURE §4.1）。 |
| C2 | 下腿（膝）のツイストは範囲外です。同じコードで `LowerLeg`・`Foot` を扱えるように、`JointId` で汎用化しておきます。 |
| C3 | `Hand` のロールのうち `LowerArm` に配分されない分（手首でのキャンディラッパー）を袖へ分配する「$f > 1$」の拡張（チェーンの最上位を `Hand` のロールでも駆動する）。既存の一般的なツイストリグ（案 E）に近い構成になります。 |
| C4 | 上腕のロール（`Upper Arm Twist`）の肩での集中も同じ構造で扱えますが、範囲外です。 |
| C5 | 2 ソースのクォータニオン補間は最短経路を取るため、VR トラッキング異常等で前腕ロールが $|\varphi| > 180°$ を超えた場合は $T_j$ が反転します（解剖学的には起きにくい既知制限）。 |

---

## 9. 実装コスト見積もり

既存コードベース（分類器、Bindpose ブレンド、4 ボーン統合、NDMF Preview、Bake・Revert）の上に作る前提で見積もりました。

| 作業 | 内容 | 目安（人日） |
| :--- | :--- | :--- |
| スパイク S1〜S3 | 実機でのロール測定、VRC Aim の API とブレンド方式、MA の参照付け替え | 1.5〜2 |
| データモデル・ローカライズ | `ForearmTwistConfig`、左右リンク、日英の文言 | 0.5〜1 |
| 軸の実測・RigMath | ロール軸と配分率のサンプリング、Aim の等価評価、ノットの算出、テスト | 1〜1.5 |
| `ForearmTwistRigBuilder` | Aim ターゲット・$N$・$T_j$ の生成、$H$ の遠位ソース切り替え | 1 |
| `ForearmTwistRedistributor` | §5 のロジック、4 ボーン丸め、統計、単体テスト | 2〜3 |
| Pass・BuildState・Baker の統合 | 生成順序（B6）、共有キー、Bake・Revert・Re-Bake | 1.5〜2 |
| プレビュー・ヒートマップ | 影の $N$・$T_j$、$\tau$ 表示、ビルド結果との一致確認 | 1.5〜2 |
| Inspector・診断 UI | トグル・スライダー・詳細設定・警告 | 1 |
| 実環境の統合検証 | GestureManager、VRChat 実機（デスクトップ・VR・FBT）、MA・AAO・Quest | 2〜3 |
| **合計** | | **約 12〜17 人日** |

- **§1.4 の決定を反映した初期実装の範囲:** Bake 対応を後回しにし（−1〜1.5 日）、実機検証は VR だけにし（−1 日）、PhysBone は警告だけにします。その代わり、素体への適用時の警告と二重適用の防止（§1.3、§1.5）を追加します（+0.5 日）。これで**約 10〜14 人日**です。
- **最小構成（MVP）:** さらにプレビューを今のツイストスライダーでの目視確認だけにすると、約 7〜9 人日です。
- **リスクを考慮:** スパイク S1 で、VR でもねじれが改善しないと分かった場合は、手首側の分配（C3）へ方針を変える必要があり、+3〜5 人日かかります。

---

## 10. テスト計画

### 10.1 単体テスト（NUnit EditMode）

| スイート | 検証ケース |
| :--- | :--- |
| `RigMathTests`（追加） | ・Aim の等価評価で、純粋な曲げ（0〜150°）のとき $N$ = `LowerArm` の回転、純粋なロールのとき $N$ = レストの回転<br>・**$\hat{h} \not\perp \hat{a}$（例：5° 傾き）でも生の $\hat{h}$ を `WorldUpVector` に使えば純粋曲げ時の偽ロールが 0** になること<br>・曲げ＋ロールの組み合わせで、$T_j$ の回転 = $N \cdot R(\hat{a}_0, k_j\varphi)$（誤差 $\le 10^{-4}$ rad）<br>・ボーンのローカル軸を任意に回転させても結果が同じ |
| `ForearmTwistRedistributorTests`（新規） | ・ツイスト総量の保存（不変条件 11）、隣接性（12）、総和 1.0・降順ソート<br>・$G_P$・$H$・$G_O$・Constraint 駆動の $G_D$ のウェイトがビット単位で変わらないこと<br>・4 スロットが埋まった頂点で、最寄りのノットに丸めて解決し、外部ボーンは変わらないこと<br>・$f = 0$ で全頂点が $N$ だけ、$f = 1$・$s = 1$ の頂点が `DistalSource` だけ、`endRatio <= startRatio` で安全にガードされること<br>・UV シームで分割された頂点で、出力が完全に一致すること |
| `SkinningEvaluatorTests`（追加） | ・チェーンボーンの Bindpose で、レスト姿勢のズレが $\le 10^{-4}$ m |
| `TwistBoneClassifierTests`（追加） | ・`Hand` をソースにする既存の `LowerArm_Twist` について、**その SMR の実ウェイトに載っている場合のみ**既存前腕ツイストリグとして検出し、衣装 SMR（ウェイト 0）は誤スキップしないこと<br>・剛体の子（`Cuff`・`ScaleProxy`）が $G_D^{\text{rigid}}$ に入り、APS 休止中ラッパー配下でも `DistalSource` が `LowerArm_*_Const` へ昇格すること<br>・関節×機能単位の二重適用スキップ（肘のみ適用済みでも前腕ツイストや膝は適用できること） |

### 10.2 実環境の統合テスト

- [ ] 長袖の衣装で、手首を ±90° ひねったとき、肘の帯にねじれと細りが無く、前腕全体でなめらかにねじれること（$n = 1, 2, 3$ を比較）
- [ ] 肘 120° の曲げと前腕 90° のひねりを同時に入れても、肘で袖がずれないこと（$H$ が曲げにだけ追従すること）
- [ ] $f = 0 / 0.5 / 1.0$ で、手首の段差と素体の貫通の見え方
- [ ] VRChat 実機（デスクトップ・VR 3 点・FBT）で、ロールの量・フリップの有無・1 フレーム遅れの有無
- [ ] MA Merge Armature・Scale Adjuster・AAO Trace and Optimize を併用してエラーが出ず、生成ボーンが残ること
- [ ] Bake → Revert → Re-Bake で、ツイストボーンが残らないこと（Undo を 1 回で戻せること）
- [ ] Quest ビルドで Constraint の変換警告が出ないこと
- [ ] プレビューとビルド結果の見た目が一致すること

---

## 11. 実装前スパイク（要検証事項）

| ID | 検証内容 | 判定基準 | 結果による分岐 |
| :--- | :--- | :--- | :--- |
| **S1** | **VR で 1 回だけ**、手首をひねったときに肘で衣装がねじれるのを目視で確認し、本機能を入れて改善するかを見る（IK の種類ごとの比較はしない）。 | 肘でのねじれが前腕全体に分散して見える | 改善しない場合だけ、手首側の分配（C3）を検討します。 |
| **S2** | SDK 3.10.5 で `VRCAimConstraint`（$N$ を `DistalSource` 配下に配置・`WorldUp = ObjectRotationUp`・`WorldUpVector = 生の ĥ`）の API と挙動、$T_j$ の `VRCRotationConstraint` vs `VRCParentConstraint` 比較、Depth 2 のチェーンの 1 フレーム遅れの有無 | Aim の結果が RigMath の評価と一致し（$\le 0.1°$）、1 フレーム遅れが無い | 遅れがある場合は、$T_j$ のソースを $N$ ではなく「`ProximalSource` と同じ Aim を持つ独立ボーン」にして Depth を 1 にそろえます。 |
| **S3**（Bake 対応時に実施） | MA `MergeArmatureHook.RetainBoneReferences` による参照保持はソース確認済み。Bake モード＋`MA Scale Adjuster` 併用時の実ビルド検証 | 統合後も参照・Bindpose が有効 | 想定外のケースがあれば Bake モードだけ別の上方向設定を検討します（§8-B7）。 |

※ AAO 共存（§8-B8）は `ComponentInfos.VRCSDK.cs` のソース上で依存保持を確認済み。

---

## 12. 残課題・確認事項

主要方針は §1.4 に確定済みです。着手のトリガーとなる残確認事項は次の 2 点のみです：

1. **スパイク S2 の結果に応じた $T_j$ コンストレイント種別の最終確定:** 3 DoF で軽量な `VRCRotationConstraint` を優先しつつ、実機・SDK 評価で問題が無いか確認する（§4.3）。
2. **スパイク S1 の VR 実機確認:** 本アプローチ（`LowerArm` ロールの分散）で VR 上の見た目が狙いどおり改善するかを確認してから本実装へ進む。

---

## 13. 実装メモ（初期実装）

### 13.1 実装範囲

| 項目 | 状態 |
| :--- | :--- |
| NDMF ビルド（非破壊） | 実装済み。肘処理 → ツイスト処理の順で、N を先に生成して $H$ の遠位ソースにする（§8-B6） |
| Edit モードプレビュー | 実装済み。影の $N$・$T_j$ を RigMath で評価。ヒートマップに `TwistFollow`（$\tau$、4 ボーン丸め頂点はマゼンタ）を追加 |
| Bake & Replace | **未対応**（§1.4）。ツイストが有効なコンポーネントを Bake すると、Inspector と Bake 結果メッセージで「適用されない」旨を表示する |
| 素体への適用 | 対象 SMR の扱いは肘・膝と同じ。素体と衣装で同じ設定なら $N$・$T_j$ を共有する |
| 既存ツイストリグ | SMR の実ウェイト単位で検出し、スキップ＋警告（`applyEvenIfExistingTwistRig` で強制適用） |
| 袖の PhysBone | 警告のみ |
| 二重適用防止 | 生成ボーン名（`JK_Helper_{JointId}` / `JK_TwistBase_{JointId}` / `JK_Twist_{JointId}_*`）で「関節 × 機能」単位に判定。ビルド・プレビュー・Bake（肘補助のみ）で共通。マーカーコンポーネントは追加していない |

### 13.2 確定した実装上の選択

- **$T_j$ のコンストレイント:** `VRCRotationConstraint`（2 ソース、オフセット 0、`SolveInLocalSpace = false`）を採用（§4.3。S2 で挙動確認）。
- **VRC SDK API（3.10.5 の DLL メタデータで確認済み）:** `VRCAimConstraint.AimAxis` / `UpAxis` / `WorldUp = VRCConstraintBase.WorldUpType.ObjectRotationUp` / `WorldUpVector` / `WorldUpTransform` / `RotationOffset` / `RotationAtRest`。
- **ロール軸:** `HingeAxisSampler` が `Left/Right Forearm Twist In-Out` を微小変化させて実測し、前腕方向から 60° 以内なら採用、それ以外は Hand 方向（§4.6）。`humanDescription.lowerArmTwist`（文書中の `foreArmTwist` は Unity API では `lowerArmTwist`）と「マッスル 1.0 あたりのロール角」を Inspector の診断に表示する。
- **ヒンジ軸:** 実測ヒンジ軸（生の $\hat{h}$）を優先し、未測定時は `JointFrame.HingeAxis`。
- **剛体遠位 $G_D^{\text{rigid}}$:** `DistalSource` 自身、または `DistalSource` から Constraint を介さずに追従する子孫（APS のポーズラッパーが `DistalSource` に昇格した場合も、その配下だけを対象にする）。
- **最上位ノット:** $f = 1$ のとき、SMR が `DistalSource` を既に参照していればそのインデックスを使い、無ければ `DistalSource` をボーン配列に追加する。
- **遅延生成:** 変更頂点から参照されたノットのボーンだけを生成・追加する（未使用ノットのプレースホルダーは詰める）。
- **`twistSnapThreshold` の既定値:** 文書どおり 0.15。値が大きいほどスロットを節約できる一方、段差が出やすくなる。実機で段差が目立つ場合は 0〜0.05 に下げる。

### 13.3 追加ファイル

| ファイル | 役割 |
| :--- | :--- |
| `Editor/Core/ForearmTwistRedistributor.cs` | §5 の純粋計算（ノット補間・4 ボーン丸め・プレースホルダー付け替え） |
| `Editor/Core/ForearmTwistRigBuilder.cs` | `ForearmTwistGeometry`（ロール軸・ヒンジ軸・前腕長）と N / T_j / Aim ターゲットの生成 |
| `Editor/Core/ForearmTwistProcessor.cs` | ビルド・プレビュー共通の診断（既存リグ・Hand ウェイト・前腕 PhysBone）・計画・確定 |
| `Editor/Core/JointKeeperGeneratedNames.cs` | 生成ボーン名の規約と二重適用判定 |
| `Tests/Editor/ForearmTwistRedistributorTests.cs` | 不変条件 11・12、外部ボーンのビット単位保持、4 ボーン丸め、シーム一貫性 ほか |
| `Tests/Editor/ForearmTwistRigMathTests.cs` | 不変条件 13（純粋曲げで偽ロール 0、ロール除去、$T_j = \Delta_N R(\hat{a}, k\varphi)$）、分類・診断・名前判定・左右リンク |

### 13.4 検証状況

- Unity 外（`dotnet build`）で Runtime / Editor / Tests の 3 アセンブリがエラー 0 でコンパイルできることを確認済み。
- `ForearmTwistRedistributor` の純粋計算は、NUnit を使わないハーネスで単体テストと同じケースに加え、ランダム 20,000 頂点の不変条件（総和保存・4 本以下・外部ボーン不変・隣接ノット）を確認済み。
- Quaternion・GameObject を使うテスト（RigMath の Aim 等価評価・分類器）は Unity ネイティブが必要なので、**Unity Test Runner（EditMode）で実行が必要**。
- 実機確認（スパイク S1・S2）は未実施。

