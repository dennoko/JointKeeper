using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Dennokoworks.JointKeeper.Editor
{
    /// <summary>
    /// JointKeeper 用ローカライズ管理（JA / EN 切替）。
    /// DennokoMeshEditor に倣い、EditorPrefs で言語設定を永続化し JSON 辞書から文言を解決する。
    /// </summary>
    internal static class JointKeeperLocalization
    {
        private const string PrefsKey = "Dennokoworks.JointKeeper.Language";
        private const string ResourceFileName = "jointkeeper_localization";

        public enum LanguageCode
        {
            JA,
            EN
        }

        [Serializable]
        private class LocalizationEntry
        {
            public string key = null;
            public string ja = null;
            public string en = null;
        }

        [Serializable]
        private class LocalizationData
        {
            public LocalizationEntry[] entries = null;
        }

        private static LanguageCode? _currentLanguage;
        private static Dictionary<string, LocalizationEntry> _dictionary;

        public static event Action OnLanguageChanged;

        public static LanguageCode CurrentLanguage
        {
            get
            {
                if (!_currentLanguage.HasValue)
                {
                    var saved = EditorPrefs.GetString(PrefsKey, "ja");
                    _currentLanguage = string.Equals(saved, "en", StringComparison.OrdinalIgnoreCase)
                        ? LanguageCode.EN
                        : LanguageCode.JA;
                }
                return _currentLanguage.Value;
            }
            set
            {
                if (_currentLanguage == value) return;
                _currentLanguage = value;
                EditorPrefs.SetString(PrefsKey, value == LanguageCode.EN ? "en" : "ja");
                OnLanguageChanged?.Invoke();
                SceneView.RepaintAll();
            }
        }

        public static bool IsJapanese => CurrentLanguage == LanguageCode.JA;

        public static string ButtonText => IsJapanese ? "EN" : "JA";

        public static string ButtonTooltip => Tr("version.switch_language_tooltip");

        public static void ToggleLanguage()
        {
            CurrentLanguage = IsJapanese ? LanguageCode.EN : LanguageCode.JA;
        }

        public static string Tr(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            EnsureLoaded();
            if (_dictionary != null && _dictionary.TryGetValue(key, out var entry))
            {
                var text = CurrentLanguage == LanguageCode.EN ? entry.en : entry.ja;
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            var format = Tr(key);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        public static GUIContent Content(string labelKey, string tooltipKey = null)
        {
            string label = Tr(labelKey);
            string tooltip = string.IsNullOrEmpty(tooltipKey) ? string.Empty : Tr(tooltipKey);
            return new GUIContent(label, tooltip);
        }

        public static GUIContent ContentFormat(string labelKey, string tooltipKey, params object[] args)
        {
            string label = Format(labelKey, args);
            string tooltip = string.IsNullOrEmpty(tooltipKey) ? string.Empty : Tr(tooltipKey);
            return new GUIContent(label, tooltip);
        }

        private static void EnsureLoaded()
        {
            if (_dictionary != null && _dictionary.Count > 0) return;

            var jsonText = LoadJsonText();
            if (string.IsNullOrEmpty(jsonText))
                return;

            var dict = new Dictionary<string, LocalizationEntry>(StringComparer.Ordinal);
            try
            {
                var data = JsonUtility.FromJson<LocalizationData>(jsonText);
                if (data?.entries != null)
                {
                    for (int i = 0; i < data.entries.Length; i++)
                    {
                        var entry = data.entries[i];
                        if (entry != null && !string.IsNullOrEmpty(entry.key))
                            dict[entry.key] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[JointKeeper] Failed to parse localization resource: {ex.Message}");
            }

            if (dict.Count > 0)
                _dictionary = dict;
        }

        private static string LoadJsonText()
        {
            // 1. スクリプトと同一フォルダの JSON を優先読込（インポート直後・テスト環境でも即解決）
            try
            {
                string siblingPath = ResolveSiblingJsonPath();
                if (!string.IsNullOrEmpty(siblingPath) && File.Exists(siblingPath))
                    return File.ReadAllText(siblingPath);
            }
            catch
            {
                // ignore
            }

            // 2. AssetDatabase FindAssets
            var guids = AssetDatabase.FindAssets(ResourceFileName);
            if (guids != null && guids.Length > 0)
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                        if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
                            return textAsset.text;
                    }
                }
            }

            // 3. Direct AssetDatabase path
            const string knownAssetPath = "Assets/dennokoworks/JointKeeper/Editor/Localization/jointkeeper_localization.json";
            var directAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(knownAssetPath);
            if (directAsset != null && !string.IsNullOrEmpty(directAsset.text))
                return directAsset.text;

            // 4. File system search in Assets/
            try
            {
                var files = Directory.GetFiles(Application.dataPath, ResourceFileName + ".json", SearchOption.AllDirectories);
                if (files != null && files.Length > 0)
                    return File.ReadAllText(files[0]);
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string ResolveSiblingJsonPath([CallerFilePath] string scriptPath = null)
        {
            if (string.IsNullOrEmpty(scriptPath)) return null;
            string dir = Path.GetDirectoryName(scriptPath);
            if (string.IsNullOrEmpty(dir)) return null;
            return Path.Combine(dir, ResourceFileName + ".json");
        }

        internal static void ReloadResource()
        {
            _dictionary = null;
            EnsureLoaded();
        }
    }
}
