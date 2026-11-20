using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.JointKeeper
{
    /// <summary>
    /// 破壊的ベイク時に差し替えた SkinnedMeshRenderer 1 件分の復元情報。
    /// </summary>
    [Serializable]
    public sealed class BakedRendererRecord
    {
        public SkinnedMeshRenderer renderer;
        public Mesh originalMesh;
        public Transform[] originalBones;
        public Mesh bakedMesh;
        public string bakedAssetPath;
        public int affectedVertexCount;
    }

    /// <summary>
    /// JointKeeperComponent の破壊的ベイク状態および Revert 用スナップショット。
    /// </summary>
    [Serializable]
    public sealed class JointBakeRecord
    {
        public bool isBaked;
        public List<BakedRendererRecord> renderers = new List<BakedRendererRecord>();
        public List<GameObject> createdHelperBones = new List<GameObject>();
        public bool bakedWithScaleAdjuster;
    }
}
