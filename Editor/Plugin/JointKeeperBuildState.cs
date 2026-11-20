using System.Collections.Generic;
using UnityEngine;
using Dennokoworks.JointKeeper;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// ビルド 1 回分の補助ボーン共有レジストリ (ADR-02, ADR-03)。
    /// RigSignatureKey が一致すれば同一補助ボーンを再利用し、異なる場合のみ追加生成する。
    /// </summary>
    internal sealed class JointKeeperBuildState
    {
        private readonly Dictionary<RigSignatureKey, Transform> _helpersByKey =
            new Dictionary<RigSignatureKey, Transform>();
        private readonly Dictionary<JointId, int> _variantCounts =
            new Dictionary<JointId, int>();

        public int CreatedBoneCount => _helpersByKey.Count;

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
    }
}
