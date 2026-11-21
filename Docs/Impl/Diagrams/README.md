# Diagrams — 補助ボーン挙動の説明図

| ファイル | 内容 |
| :--- | :--- |
| `elbow_helper.png` | 肘・ひざの曲げ補助ボーン（正方形 1350px）。静止姿勢／補助なしの曲げ／JointKeeper の曲げを比較し、外側の角が潰れずに尖りを保つ様子を示す |
| `forearm_twist.png` | 前腕ツイストボーン（正方形 1350px）。補助なし／ツイストボーン 3 段を比較し、肘での急なねじれが手首まで分散される様子を示す |
| `gen_diagrams.py` | 上の 2 枚を生成するスクリプト |

## 再生成

```sh
python gen_diagrams.py   # Python 3.10+, numpy, matplotlib
```

日本語フォントは Yu Gothic → Meiryo → BIZ UDGothic → Noto Sans JP → MS Gothic の順で、見つかったものを使います。

## 図のモデル

- どちらも 2D の簡易モデルですが、ウェイト配分とボーン変形は実装と同じ式で計算しています。
  - 肘: ARCHITECTURE §5.4 の押し出し式と §5.5 の角モーメント保存配分（外側マスク込み）
  - ツイスト: FEATURE_ForearmTwist の knot `k_j = f·j/n` と、隣り合う 2 ノットの補間
- 見やすさのため、実装の既定値から次の 2 点を変えています。
  - 肘の押し出し量 `D90` を大きめにしている
  - ツイストの丸め閾値 `twistSnapThreshold` を 0 にしている。実装の既定 0.15 では、ノット付近にわずかな平坦部ができる
- パラメータはスクリプト冒頭の定数で変更できます。
