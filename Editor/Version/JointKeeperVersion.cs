using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// ローカルの version.json を読み、GitHub 上の最新版と比較する。
    /// インポート時（コンパイル完了時）や起動時に自動でチェックが走る。
    /// レート制限対策のため前回から CheckIntervalHours 以上経過したときのみ通信し、
    /// 間隔内は EditorPrefs のキャッシュを表示に使う。
    /// </summary>
    [InitializeOnLoad]
    internal static class JointKeeperVersion
    {
        private const string FallbackVersion = "0.0.0";
        private static string _currentCache;

        internal static string Current
        {
            get
            {
                if (string.IsNullOrEmpty(_currentCache))
                {
                    _currentCache = LoadLocalVersion();
                }
                return string.IsNullOrEmpty(_currentCache) ? FallbackVersion : _currentCache;
            }
        }

        private const string RepoOwner = "dennoko";
        private const string RepoName = "JointKeeper";
        private const string RepoBranch = "main";
        private const string VersionFilePath = "version.json";

        private const string VerCheckDoneKey = "JointKeeper_VerCheck_Done";
        private const string VerCheckErrorKey = "JointKeeper_VerCheck_Error";
        private const string VerCheckLatestKey = "JointKeeper_VerCheck_Latest";
        private const string VerCheckUrlKey = "JointKeeper_VerCheck_Url";
        private const string VerCheckMessageKey = "JointKeeper_VerCheck_Message";

        private const string VerCheckLastAttemptKey = "JointKeeper_VerCheck_LastAttemptUtc";
        private const string VerCheckCachedLatestKey = "JointKeeper_VerCheck_CachedLatest";
        private const string VerCheckCachedUrlKey = "JointKeeper_VerCheck_CachedUrl";
        private const string VerCheckCachedMessageKey = "JointKeeper_VerCheck_CachedMessage";

        private const double CheckIntervalHours = 6.0;

        static JointKeeperVersion()
        {
            EditorApplication.delayCall += StartCheckBackgroundTask;
        }

        private static bool _checking;

        internal static void StartCheckBackgroundTask()
        {
            bool done = SessionState.GetBool(VerCheckDoneKey, false);
            bool error = SessionState.GetBool(VerCheckErrorKey, false);
            if (done && !error) return;
            if (_checking) return;

            if (IsInCheckInterval())
            {
                ApplyCachedResult();
                return;
            }

            _checking = true;
            EditorPrefs.SetString(VerCheckLastAttemptKey, DateTime.UtcNow.ToString("o"));

            DennokoVersionChecker.CheckAsync(
                RepoOwner, RepoName, RepoBranch, VersionFilePath, Current, OnVersionChecked);
        }

        private static bool IsInCheckInterval()
        {
            var last = EditorPrefs.GetString(VerCheckLastAttemptKey, string.Empty);
            if (string.IsNullOrEmpty(last)) return false;
            if (!DateTime.TryParse(last, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var lastUtc)) return false;

            var elapsed = DateTime.UtcNow - lastUtc.ToUniversalTime();
            if (elapsed < TimeSpan.Zero) return false;

            return elapsed.TotalHours < CheckIntervalHours;
        }

        private static void ApplyCachedResult()
        {
            var latest = EditorPrefs.GetString(VerCheckCachedLatestKey, string.Empty);
            SessionState.SetBool(VerCheckDoneKey, true);
            SessionState.SetBool(VerCheckErrorKey, string.IsNullOrEmpty(latest));
            SessionState.SetString(VerCheckLatestKey, latest);
            SessionState.SetString(VerCheckUrlKey, EditorPrefs.GetString(VerCheckCachedUrlKey, string.Empty));
            SessionState.SetString(VerCheckMessageKey, EditorPrefs.GetString(VerCheckCachedMessageKey, string.Empty));

            RefreshOpenInspectors();
        }

        /// <summary>手動での再取得。クールダウンを無視して最新版を再チェックする。</summary>
        internal static void ForceRecheck()
        {
            if (_checking) return;
            _currentCache = null;
            SessionState.SetBool(VerCheckDoneKey, false);
            SessionState.SetBool(VerCheckErrorKey, false);
            EditorPrefs.DeleteKey(VerCheckLastAttemptKey);
            StartCheckBackgroundTask();
        }

        private static void OnVersionChecked(DennokoVersionChecker.Result result)
        {
            _checking = false;
            bool failed = result.State == DennokoVersionChecker.State.Error;

            SessionState.SetBool(VerCheckDoneKey, true);
            SessionState.SetBool(VerCheckErrorKey, failed);
            SessionState.SetString(VerCheckLatestKey, result.LatestVersion ?? string.Empty);
            SessionState.SetString(VerCheckUrlKey, result.Url ?? string.Empty);
            SessionState.SetString(VerCheckMessageKey, result.Message ?? string.Empty);

            if (!failed)
            {
                EditorPrefs.SetString(VerCheckCachedLatestKey, result.LatestVersion ?? string.Empty);
                EditorPrefs.SetString(VerCheckCachedUrlKey, result.Url ?? string.Empty);
                EditorPrefs.SetString(VerCheckCachedMessageKey, result.Message ?? string.Empty);
            }

            RefreshOpenInspectors();
        }

        private static void RefreshOpenInspectors()
        {
            var inspectors = Resources.FindObjectsOfTypeAll<JointKeeperEditor>();
            if (inspectors == null) return;
            foreach (var inspector in inspectors)
            {
                if (inspector != null) inspector.ReloadVersionResult();
            }
        }

        internal static DennokoVersionChecker.Result LoadResultFromSessionState()
        {
            string local = Current;
            string latest = SessionState.GetString(VerCheckLatestKey, string.Empty);
            bool done = SessionState.GetBool(VerCheckDoneKey, false);
            bool error = SessionState.GetBool(VerCheckErrorKey, false);

            DennokoVersionChecker.State state;
            if (!done)
                state = DennokoVersionChecker.State.Checking;
            else if (error || string.IsNullOrEmpty(latest))
                state = DennokoVersionChecker.State.Error;
            else if (DennokoVersionChecker.IsUpdateAvailable(latest, local))
                state = DennokoVersionChecker.State.UpdateAvailable;
            else
                state = DennokoVersionChecker.State.UpToDate;

            return new DennokoVersionChecker.Result
            {
                State = state,
                LocalVersion = local,
                LatestVersion = latest,
                Url = SessionState.GetString(VerCheckUrlKey, string.Empty),
                Message = SessionState.GetString(VerCheckMessageKey, string.Empty),
            };
        }

        internal static void OpenUpdatePage(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                url = $"https://github.com/{RepoOwner}/{RepoName}/releases";
            }
            Application.OpenURL(url);
        }

        [Serializable]
        private class VersionInfo
        {
            public string version = null;
        }

        private static string LoadLocalVersion()
        {
            var v = TryReadVersion(ResolveVersionJsonByScriptPath());
            if (v != null) return v;

            foreach (var guid in AssetDatabase.FindAssets("version t:TextAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.EndsWith("/version.json", StringComparison.OrdinalIgnoreCase)) continue;

                var dir = path.Substring(0, path.Length - "/version.json".Length);
                if (!File.Exists(Path.Combine(dir, "Editor", "Version", "JointKeeperVersion.cs")) &&
                    !File.Exists(Path.Combine(dir, "Runtime", "JointKeeperComponent.cs"))) continue;

                v = TryReadVersion(path);
                if (v != null) return v;
            }

            return null;
        }

        private static string TryReadVersion(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var info = JsonUtility.FromJson<VersionInfo>(File.ReadAllText(path));
                if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[JointKeeper] version.json の読み込みに失敗しました ({path}): {e.Message}");
            }
            return null;
        }

        private static string ResolveVersionJsonByScriptPath([CallerFilePath] string scriptPath = null)
        {
            if (string.IsNullOrEmpty(scriptPath)) return null;
            var dir = Path.GetDirectoryName(scriptPath);
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "version.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
