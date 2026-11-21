using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// ビルド 1 回分の補助ボーン共有レジストリ (ADR-02, ADR-03)。
    /// RigSignatureKey が一致すれば同一補助ボーンを再利用し、異なる場合のみ追加生成する。
    /// 前腕ツイスト分散ボーンも同様に共有し、素体と衣装で同じ N・T_j を使う (FEATURE_ForearmTwist §1.5)。
    /// </summary>
    internal sealed class JointKeeperBuildState
    {
        private readonly Dictionary<RigSignatureKey, Transform> _helpersByKey =
            new Dictionary<RigSignatureKey, Transform>();
        private readonly Dictionary<JointId, int> _variantCounts =
            new Dictionary<JointId, int>();

        private readonly Dictionary<(JointId jointId, int proximalId, int distalId), Transform> _twistBases =
            new Dictionary<(JointId, int, int), Transform>();
        private readonly Dictionary<(int baseId, int quantizedK), Transform> _twistKnots =
            new Dictionary<(int, int), Transform>();
        private readonly Dictionary<int, int> _twistKnotCounts = new Dictionary<int, int>();

        public int CreatedBoneCount => _helpersByKey.Count;
        public int CreatedTwistBoneCount => _twistBases.Count + _twistKnots.Count;

        public Transform GetOrCreateHelperBone(
            RigSignatureKey key,
            JointFrame frame,
            Transform proximalSource,
            Transform distalSource,
            float bendFollowRatio,
            float pushAt90Deg)
        {
            if (_helpersByKey.TryGetValue(key, out Transform existing) && existing != null)
                return existing;

            int variant = _variantCounts.TryGetValue(key.JointId, out int count) ? (count + 1) : 1;
            _variantCounts[key.JointId] = variant;

            Transform created = VrcConstraintRigBuilder.CreateHelperBone(
                key.JointId,
                frame,
                proximalSource,
                distalSource,
                bendFollowRatio,
                pushAt90Deg,
                variant);

            _helpersByKey[key] = created;
            return created;
        }

        /// <summary>
        /// 曲げ専用基準ボーン N を取得または生成する。N はボーンだけで決まるため、設定に関係なく同じ腕なら共有する。
        /// </summary>
        public Transform GetOrCreateTwistBase(
            JointId jointId,
            in ForearmTwistGeometry geometry,
            Transform proximalSource,
            Transform distalSource)
        {
            return GetOrCreateTwistBase(jointId, geometry, proximalSource, distalSource, out _, out _);
        }

        /// <summary>
        /// 曲げ専用基準ボーン N を取得または生成し、新規生成有無と Aim ターゲットを返す（Bake 時の Undo/Revert 管理用）。
        /// </summary>
        public Transform GetOrCreateTwistBase(
            JointId jointId,
            in ForearmTwistGeometry geometry,
            Transform proximalSource,
            Transform distalSource,
            out Transform createdAimTarget,
            out bool wasCreated)
        {
            createdAimTarget = null;
            wasCreated = false;
            var key = (jointId, proximalSource.GetInstanceID(), distalSource.GetInstanceID());
            if (_twistBases.TryGetValue(key, out Transform existing) && existing != null)
                return existing;

            Transform created = ForearmTwistRigBuilder.CreateTwistBase(
                jointId, geometry, proximalSource, distalSource, out createdAimTarget);
            _twistBases[key] = created;
            wasCreated = created != null;
            return created;
        }

        /// <summary>
        /// 中間ボーン T_j（ロール追従率 k）を取得または生成する。k が一致すれば衣装・素体で共有する。
        /// </summary>
        public Transform GetOrCreateTwistKnot(
            JointId jointId,
            in ForearmTwistGeometry geometry,
            Transform twistBase,
            Transform distalSource,
            float k)
        {
            return GetOrCreateTwistKnot(jointId, geometry, twistBase, distalSource, k, out _);
        }

        /// <summary>
        /// 中間ボーン T_j（ロール追従率 k）を取得または生成し、新規生成有無を返す（Bake 時の Undo/Revert 管理用）。
        /// </summary>
        public Transform GetOrCreateTwistKnot(
            JointId jointId,
            in ForearmTwistGeometry geometry,
            Transform twistBase,
            Transform distalSource,
            float k,
            out bool wasCreated)
        {
            wasCreated = false;
            int baseId = twistBase.GetInstanceID();
            var key = (baseId, Mathf.RoundToInt(Mathf.Clamp01(k) * 10000f));
            if (_twistKnots.TryGetValue(key, out Transform existing) && existing != null)
                return existing;

            int index = _twistKnotCounts.TryGetValue(baseId, out int c) ? c + 1 : 1;
            _twistKnotCounts[baseId] = index;

            Transform created = ForearmTwistRigBuilder.CreateTwistKnot(
                jointId, index, geometry, twistBase, distalSource, k, displayAxialRatio: k);
            _twistKnots[key] = created;
            wasCreated = created != null;
            return created;
        }
    }
}
