using NUnit.Framework;
using UnityEngine;
using Dennokoworks.JointKeeper;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class RigSignatureKeyTests
    {
        [Test]
        public void IdenticalWithinQuantization_KeysAreEqual()
        {
            var a = new RigSignatureKey(JointId.LeftElbow, 100, 200, 0.5000f, 0.00200f, new Vector3(0.00100f, 0f, 0f), 10.000f);
            var b = new RigSignatureKey(JointId.LeftElbow, 100, 200, 0.5001f, 0.00201f, new Vector3(0.00101f, 0f, 0f), 10.001f);

            Assert.AreEqual(a, b);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [Test]
        public void DifferentPushOrPivot_KeysDiffer()
        {
            var a = new RigSignatureKey(JointId.LeftElbow, 100, 200, 0.5f, 0.002f, Vector3.zero, 0f);
            var b = new RigSignatureKey(JointId.LeftElbow, 100, 200, 0.5f, 0.004f, Vector3.zero, 0f);
            var c = new RigSignatureKey(JointId.LeftElbow, 100, 200, 0.5f, 0.002f, new Vector3(0.002f, 0f, 0f), 0f);

            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(a, c);
        }
    }
}
