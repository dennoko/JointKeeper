using System.IO;
using NUnit.Framework;
using Dennokoworks.JointKeeper.Editor;

namespace Dennokoworks.JointKeeper.Tests
{
    public class JointKeeperVersionAndLocalizationTests
    {
        [Test]
        public void VersionChecker_ComparesSemanticVersionsCorrectly()
        {
            Assert.IsTrue(DennokoVersionChecker.IsUpdateAvailable("0.2.0", "0.1.0"));
            Assert.IsTrue(DennokoVersionChecker.IsUpdateAvailable("v1.0.0", "0.1.0"));
            Assert.IsTrue(DennokoVersionChecker.IsUpdateAvailable("0.1.1-beta", "0.1.0"));

            Assert.IsFalse(DennokoVersionChecker.IsUpdateAvailable("0.1.0", "0.1.0"));
            Assert.IsFalse(DennokoVersionChecker.IsUpdateAvailable("v0.1.0", "0.1.0"));
            Assert.IsFalse(DennokoVersionChecker.IsUpdateAvailable("0.1", "0.1.0"));
            Assert.IsFalse(DennokoVersionChecker.IsUpdateAvailable("0.0.9", "0.1.0"));
        }

        [Test]
        public void LocalVersion_ReadsVersionJson()
        {
            Assert.AreEqual("0.1.0", JointKeeperVersion.Current);
        }

        [Test]
        public void UserManualPdf_ResolvesJointKeeperDocsByLanguage()
        {
            string jaPath = JointKeeperVersion.ResolveUserManualPdfPath(isJapanese: true);
            string enPath = JointKeeperVersion.ResolveUserManualPdfPath(isJapanese: false);

            Assert.IsFalse(string.IsNullOrEmpty(jaPath));
            Assert.IsFalse(string.IsNullOrEmpty(enPath));
            Assert.IsTrue(File.Exists(jaPath));
            Assert.IsTrue(File.Exists(enPath));
            Assert.AreEqual("user_manual_ja.pdf", Path.GetFileName(jaPath));
            Assert.AreEqual("user_manual_en.pdf", Path.GetFileName(enPath));
            StringAssert.Contains("JointKeeper", jaPath);
            StringAssert.Contains("JointKeeper", enPath);
        }

        [Test]
        public void Localization_ResolvesBothJapaneseAndEnglish()
        {
            var prev = JointKeeperLocalization.CurrentLanguage;
            try
            {
                JointKeeperLocalization.ReloadResource();

                JointKeeperLocalization.CurrentLanguage = JointKeeperLocalization.LanguageCode.JA;
                Assert.AreEqual("EN", JointKeeperLocalization.ButtonText);
                Assert.AreNotEqual("joint.strength_tooltip", JointKeeperLocalization.Tr("joint.strength_tooltip"));
                Assert.AreNotEqual("bake.btn_bake_tooltip", JointKeeperLocalization.Tr("bake.btn_bake_tooltip"));

                JointKeeperLocalization.CurrentLanguage = JointKeeperLocalization.LanguageCode.EN;
                Assert.AreEqual("JA", JointKeeperLocalization.ButtonText);
                Assert.AreEqual("Strength", JointKeeperLocalization.Tr("joint.strength"));
                Assert.AreEqual("Bake & Replace", JointKeeperLocalization.Tr("bake.btn_bake"));
                Assert.AreNotEqual("joint.push90_tooltip", JointKeeperLocalization.Tr("joint.push90_tooltip"));
            }
            finally
            {
                JointKeeperLocalization.CurrentLanguage = prev;
            }
        }
    }
}
