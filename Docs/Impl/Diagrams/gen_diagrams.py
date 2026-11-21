"""JointKeeper の補助ボーン挙動説明図を生成するスクリプト。

    python gen_diagrams.py

同じフォルダに次の 2 枚を出力する。
    elbow_helper.png   … 肘・ひざの曲げ補助ボーン（尖り維持）
    forearm_twist.png  … 前腕ツイストボーン（ねじれ分散）

どちらも 2D の簡易モデルだが、ウェイト配分とボーン変形は実装と同じ式で計算している
(ARCHITECTURE §5.4/§5.5, FEATURE_ForearmTwist §4〜§5)。
必要: Python 3.10+, numpy, matplotlib
"""

from __future__ import annotations

import os

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from matplotlib import font_manager
from matplotlib.patches import FancyArrowPatch, Polygon

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

# ---------------------------------------------------------------- style
C_PROX = "#3B6FB6"   # 近位（上腕・太もも）
C_DIST = "#E07B39"   # 遠位（前腕・すね）
C_HELP = "#2E9E5B"   # 肘補助ボーン
C_TWIST = "#8A4FBF"  # ツイストボーン
C_BAD = "#C8414B"
C_SKIN = "#E9EEF5"
C_EDGE = "#46505E"
C_TEXT = "#222831"
C_SUB = "#6B7280"


def setup_font() -> None:
    names = {f.name for f in font_manager.fontManager.ttflist}
    for cand in ("Yu Gothic", "Meiryo", "BIZ UDGothic", "Noto Sans JP", "MS Gothic"):
        if cand in names:
            plt.rcParams["font.family"] = cand
            break
    plt.rcParams["axes.unicode_minus"] = False
    plt.rcParams["text.color"] = C_TEXT


def smoothstep(e0, e1, x):
    t = np.clip((np.asarray(x, dtype=float) - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def rot2(theta):
    c, s = np.cos(theta), np.sin(theta)
    return np.array([[c, -s], [s, c]])


def clean_axes(ax):
    ax.set_aspect("equal")
    ax.axis("off")


# =====================================================================
# 図 1: 肘・ひざの曲げ補助ボーン
# =====================================================================
# 2D 断面モデル: 肢軸 +x、関節中心 J = 原点、半径 R の筒。
# 遠位は J 回りに +θ 回転（内側 = +y が折り目、外側 = -y が肘頭）。
R = 1.0
L_SEG = 3.6
BLEND = 0.7            # 元衣装の上腕/前腕ウェイトの遷移幅（片側）
ALPHA = 0.5            # bendFollowRatio
STRENGTH = 0.85        # strength S
GAMMA = 1.2            # blendCurvePower γ
D90 = 0.55             # 90° 曲げ時の外側押し出し量 d90（R 比、説明用に大きめ）
R_AXIAL, R_RADIAL = 2.2, 2.6
INNER_FALLOFF = 0.25
C_IN, C_OUT = -0.15, 0.35
M_IN = 0.0             # innerRetention
THETA = np.deg2rad(95.0)


def elbow_weights(x, y):
    """元ウェイト (W_P, W_D) と JointKeeper 配分後 (w_P', w_D', w_H) を返す。§5.5 step 1〜3。"""
    w_d = smoothstep(-BLEND, BLEND, x)
    w_p = 1.0 - w_d
    d_norm = np.sqrt((x / R_AXIAL) ** 2 + (y / R_RADIAL) ** 2)
    m_dist = 1.0 - smoothstep(INNER_FALLOFF, 1.0, d_norm)
    c = np.where(np.abs(y) < 1e-5, 0.0, -np.sign(y))   # n_out = -y
    m_dir = M_IN + (1.0 - M_IN) * smoothstep(C_IN, C_OUT, c)
    m = m_dist * m_dir
    w_hmax = np.minimum(w_p / (1.0 - ALPHA), w_d / ALPHA)
    u = np.clip(w_hmax, 0.0, 1.0)  # W_joint = 1
    w_h = STRENGTH * m * u ** GAMMA
    w_h = np.where(w_h < 1e-5, 0.0, w_h)
    wp2 = np.maximum(0.0, w_p - (1.0 - ALPHA) * w_h)
    wd2 = np.maximum(0.0, w_d - ALPHA * w_h)
    return w_p, w_d, wp2, wd2, w_h


def helper_transform(theta):
    """補助ボーンのワールド変形 (回転, 平行移動)。§5.4 の押し出し式。"""
    k = np.sqrt(2.0) * D90
    t = np.array([1.0, 0.0])
    pos = 0.5 * k * (t - rot2(theta) @ t)    # = k sin(θ/2) · b_out
    return rot2(ALPHA * theta), pos


def skin_elbow(x, y, theta, use_helper):
    pts = np.stack([x, y], axis=-1)
    w_p, w_d, wp2, wd2, w_h = elbow_weights(x, y)
    rd = pts @ rot2(theta).T
    if not use_helper:
        out = w_p[..., None] * pts + w_d[..., None] * rd
    else:
        rh, th = helper_transform(theta)
        ph = pts @ rh.T + th
        out = wp2[..., None] * pts + wd2[..., None] * rd + w_h[..., None] * ph
    return out[..., 0], out[..., 1], w_h


def tube_grid(nx=161, ny=41):
    xs = np.linspace(-L_SEG, L_SEG, nx)
    ys = np.linspace(-R, R, ny)
    return np.meshgrid(xs, ys)


def outline(X, Y):
    """グリッドの外周を 1 本の閉ポリゴンにする。"""
    top = np.stack([X[-1], Y[-1]], 1)
    bot = np.stack([X[0], Y[0]], 1)
    right = np.stack([X[:, -1], Y[:, -1]], 1)
    left = np.stack([X[:, 0], Y[:, 0]], 1)
    return np.concatenate([bot, right, top[::-1], left[::-1]])


def draw_bone(ax, a, b, color, lw=5, label=None, label_off=(0, 0), ha="center"):
    ax.plot([a[0], b[0]], [a[1], b[1]], color=color, lw=lw, solid_capstyle="round", zorder=6)
    if label:
        m = (np.asarray(a) + np.asarray(b)) / 2 + np.asarray(label_off)
        ax.text(m[0], m[1], label, color=color, fontsize=10.5, ha=ha, va="center",
                fontweight="bold", zorder=9)


def draw_elbow_panel(ax, theta, mode):
    X, Y = tube_grid()
    if theta == 0:
        SX, SY = X, Y
        _, _, _, _, WH = elbow_weights(X, Y)
    else:
        SX, SY, WH = skin_elbow(X, Y, theta, use_helper=(mode == "helper"))

    poly = outline(SX, SY)
    ax.add_patch(Polygon(poly, closed=True, fc=C_SKIN, ec="none", zorder=1))
    if mode != "none":
        cmap = matplotlib.colors.LinearSegmentedColormap.from_list("h", ["#FFFFFF00", C_HELP])
        ax.pcolormesh(SX, SY, WH, cmap=cmap, vmin=0, vmax=0.9, shading="gouraud",
                      zorder=2, alpha=0.75, rasterized=True)
    ax.add_patch(Polygon(poly, closed=True, fc="none", ec=C_EDGE, lw=1.8, zorder=4))

    j = np.array([0.0, 0.0])
    p_end = np.array([-L_SEG + 0.4, 0.0])
    d_end = rot2(theta) @ np.array([L_SEG - 0.4, 0.0])
    draw_bone(ax, p_end, j, C_PROX)
    draw_bone(ax, j, d_end, C_DIST)
    ax.plot(*j, "o", ms=9, mfc="white", mec=C_TEXT, mew=1.6, zorder=8)

    corner = np.array([np.tan(theta / 2) * R, -R]) if theta else None  # 理想の外側の角

    if theta and mode == "none":
        sx, sy, _ = skin_elbow(np.array([0.0]), np.array([-R]), theta, False)
        ax.plot([corner[0]], [corner[1]], marker="x", color=C_SUB, ms=10, mew=2, zorder=7)
        ax.add_patch(FancyArrowPatch(corner, (sx[0], sy[0]), arrowstyle="-|>", mutation_scale=16,
                                     color=C_BAD, lw=2.4, zorder=7))
        ax.text(corner[0] + 0.25, corner[1] - 0.35, "潰れる", color=C_BAD,
                fontsize=13, ha="left", va="top", fontweight="bold")

    if theta and mode == "helper":
        rh, th = helper_transform(theta)
        hdir = rh @ np.array([1.0, 0.0])
        ax.add_patch(FancyArrowPatch(j, th, arrowstyle="-|>", mutation_scale=14,
                                     color=C_HELP, lw=2.2, zorder=8))
        draw_bone(ax, th - 0.55 * hdir, th + 0.55 * hdir, C_HELP, lw=4.5)
        ax.plot(*th, "D", ms=9, mfc=C_HELP, mec="white", mew=1.2, zorder=9)
        ax.plot([corner[0]], [corner[1]], marker="x", color=C_SUB, ms=10, mew=2, zorder=7)
        ax.text(corner[0] + 0.25, corner[1] - 0.35, "尖りを保つ", color=C_HELP,
                fontsize=13, ha="left", va="top", fontweight="bold")
        NX, NY, _ = skin_elbow(X, Y, theta, use_helper=False)  # 比較用: 補助ボーンなしの外形
        ax.add_patch(Polygon(outline(NX, NY), closed=True, fc="none", ec=C_BAD, lw=1.2,
                             ls=(0, (3, 3)), zorder=3))

    if theta == 0:
        ax.plot(*j, "D", ms=10, mfc=C_HELP, mec="white", mew=1.2, zorder=9)
        ax.annotate("補助ボーン", xy=(0.05, -0.1), xytext=(0.6, -1.75),
                    color=C_HELP, fontsize=13, fontweight="bold", ha="left", va="center",
                    arrowprops=dict(arrowstyle="-", color=C_HELP, lw=1.4))
        ax.text(-2.0, 0.42, "上腕 / 太もも", color=C_PROX, fontsize=13, ha="center", fontweight="bold")
        ax.text(2.0, 0.42, "前腕 / すね", color=C_DIST, fontsize=13, ha="center", fontweight="bold")

    clean_axes(ax)
    if theta == 0:
        ax.set_xlim(-5.2, 5.2)
        ax.set_ylim(-2.1, 1.25)
    else:
        ax.set_xlim(-4.0, 3.4)
        ax.set_ylim(-1.9, 3.9)


def figure_elbow():
    fig = plt.figure(figsize=(9, 9))
    gs = fig.add_gridspec(2, 2, height_ratios=[0.62, 1.0], hspace=0.18, wspace=0.04,
                          left=0.03, right=0.97, top=0.86, bottom=0.03)
    ax_r = fig.add_subplot(gs[0, :])
    ax_n = fig.add_subplot(gs[1, 0])
    ax_h = fig.add_subplot(gs[1, 1])
    draw_elbow_panel(ax_r, 0.0, "rest")
    draw_elbow_panel(ax_n, THETA, "none")
    draw_elbow_panel(ax_h, THETA, "helper")
    for ax, t in ((ax_r, "① 静止姿勢"), (ax_n, "② 補助ボーンなし"), (ax_h, "③ JointKeeper")):
        ax.set_title(t, fontsize=16, fontweight="bold", loc="left", pad=8)
    fig.suptitle("肘・ひざの補助ボーン", fontsize=22, fontweight="bold", x=0.03, ha="left", y=0.965)
    path = os.path.join(OUT_DIR, "elbow_helper.png")
    fig.savefig(path, dpi=150, facecolor="white")
    plt.close(fig)
    return path


# =====================================================================
# 図 2: 前腕ツイストボーン
# =====================================================================
# 側面から見た筒。肢軸 = 画面 x。肘 x=0、手首 x=FL。前腕が軸回りに PHI ロールした状態。
# 頂点は「ロール角 roll_b のボーン群のブレンド」なので、断面円上の点 e^{iψ} は
#     z = r · e^{iψ} · Σ w_b e^{i roll_b}
# に移る（線形ブレンドスキニング）。|Σ w_b e^{i roll_b}| < 1 が「キャンディ包み」の痩せ。
FL = 6.0
UL = 2.2
TR = 0.85
PHI = np.deg2rad(110.0)
N_SEG = 3
SNAP = 0.0             # 説明図では丸めなし（実装の既定 twistSnapThreshold は 0.15）
ELBOW_BLEND = 0.45
N_STRIPES = 8


def twist_knots(f, n):
    return np.array([f * j / n for j in range(n + 1)])


def rig_tau(x, f, n=N_SEG, snap=SNAP):
    """頂点ごとの実効ロール率（φ 比）と、補間に使う 2 ノットの重みを返す (§5)。"""
    s = np.clip(x / FL, 0.0, 1.0)
    tau = f * s                                   # curve = Linear
    knots = twist_knots(f, n)
    step = f / n
    pos = np.clip(tau / step, 0, n - 1e-9)
    j = np.floor(pos).astype(int)
    u = pos - j
    if snap > 0:
        u = np.where(u <= snap, 0.0, np.where(u >= 1 - snap, 1.0, u))
    return knots[j], knots[np.minimum(j + 1, n)], u


def blend_factor(x, mode, f=1.0):
    """筒上の各 x について Σ w_b e^{i roll_b}（複素数）を返す。"""
    w_d = smoothstep(-ELBOW_BLEND, ELBOW_BLEND, x)
    w_p = 1.0 - w_d
    if mode == "none":
        return w_p + w_d * np.exp(1j * PHI)
    ka, kb, u = rig_tau(np.maximum(x, 0.0), f)
    # 遠位ウェイトはツイストチェーンへ。肘付近は N（ロール 0）へ行くので上腕との間で痩せない。
    return w_p + w_d * ((1 - u) * np.exp(1j * ka * PHI) + u * np.exp(1j * kb * PHI))


def draw_twist_tube(ax, mode, f=1.0):
    xs = np.linspace(-UL, FL, 600)
    bf = blend_factor(xs, mode, f)
    half = TR * np.abs(bf)
    ax.fill_between(xs, -half, half, color=C_SKIN, zorder=1, lw=0)
    ax.plot(xs, half, color=C_EDGE, lw=1.8, zorder=4)
    ax.plot(xs, -half, color=C_EDGE, lw=1.8, zorder=4)

    for i in range(N_STRIPES):
        psi = 2 * np.pi * i / N_STRIPES + 0.2
        z = TR * np.exp(1j * psi) * bf
        y, depth = z.real, z.imag
        hl = i == 0
        yv = np.where(depth > 0, y, np.nan)
        ax.plot(xs, yv, color=C_TWIST if hl else "#7A8597", lw=3.0 if hl else 1.1,
                zorder=5 if hl else 3, solid_capstyle="butt")

    # 手（手首から先）
    ax.add_patch(Polygon([[FL, -0.75], [FL + 0.9, -0.6], [FL + 0.9, 0.6], [FL, 0.75]],
                         closed=True, fc="#F3E3D3", ec=C_EDGE, lw=1.3, zorder=2))
    for xv, lab in ((0.0, "肘"), (FL, "手首")):
        ax.plot([xv, xv], [-1.2, 1.2], color=C_SUB, lw=0.9, ls=(0, (2, 2)), zorder=0)
        ax.text(xv, -1.35, lab, fontsize=12.5, ha="center", va="top", color=C_SUB)
    ax.text(-UL / 2, 1.15, "上腕", color=C_PROX, fontsize=13, ha="center", fontweight="bold")
    if mode == "none":
        ax.text(FL / 2, 1.15, "前腕", color=C_DIST, fontsize=13, ha="center", fontweight="bold")
    clean_axes(ax)
    ax.set_xlim(-UL - 0.2, FL + 1.1)
    ax.set_ylim(-1.9, 1.6 if mode == "none" else 2.5)


def draw_roll_icon(ax, cx, cy, frac, label, color, r=0.36):
    t = np.linspace(0, 2 * np.pi, 60)
    ax.plot(cx + r * np.cos(t), cy + r * np.sin(t), color=color, lw=1.4, zorder=6)
    a = np.pi / 2 - frac * PHI
    ax.plot([cx, cx + r * np.cos(a)], [cy, cy + r * np.sin(a)], color=color, lw=2.6, zorder=7)
    ax.plot([cx, cx], [cy, cy + r], color=color, lw=0.9, ls=(0, (1.5, 1.5)), zorder=6)
    ax.text(cx, cy + r + 0.12, label, fontsize=12, ha="center", va="bottom", color=color,
            fontweight="bold")


def figure_twist():
    fig = plt.figure(figsize=(9, 9))
    gs = fig.add_gridspec(2, 1, height_ratios=[3.5, 4.4], hspace=0.14, left=0.03, right=0.97, top=0.86, bottom=0.02)
    ax_a = fig.add_subplot(gs[0])
    ax_b = fig.add_subplot(gs[1])
    draw_twist_tube(ax_a, "none")
    draw_twist_tube(ax_b, "rig", 1.0)

    bf = blend_factor(np.array([0.0]), "none")
    ax_a.annotate("肘で急にねじれて潰れる", xy=(0.08, -TR * abs(bf[0])), xytext=(0.9, -1.75),
                  color=C_BAD, fontsize=13, fontweight="bold", va="center",
                  arrowprops=dict(arrowstyle="-|>", color=C_BAD, lw=1.8))

    labels = ["N"] + [f"T{j}" for j in range(1, N_SEG)] + ["LowerArm"]
    for j, k in enumerate(twist_knots(1.0, N_SEG)):
        draw_roll_icon(ax_b, FL * j / N_SEG, 1.4, k, labels[j], C_TWIST)

    for ax, t in ((ax_a, "① ツイストボーンなし"), (ax_b, "② JointKeeper")):
        ax.set_title(t, fontsize=16, fontweight="bold", loc="left", pad=6)
    fig.suptitle("前腕ツイストボーン", fontsize=22, fontweight="bold", x=0.03, ha="left", y=0.965)
    path = os.path.join(OUT_DIR, "forearm_twist.png")
    fig.savefig(path, dpi=150, facecolor="white")
    plt.close(fig)
    return path


if __name__ == "__main__":
    setup_font()
    for p in (figure_elbow(), figure_twist()):
        print("wrote", p)
