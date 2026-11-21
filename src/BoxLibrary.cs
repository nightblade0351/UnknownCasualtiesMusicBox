using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using CUCoreLib.Helpers;

namespace UCMB
{
    /// <summary>
    /// 音乐盒扫描与音频加载。
    /// 选歌键 = data 下的文件夹名称（严格），不使用文件元数据/标签。
    /// </summary>
    public sealed class BoxLibrary
    {
        public const string DefaultChoiceKey = "__default";
        public const string DefaultChoiceLabel = "游戏默认";

        private static readonly string[] ExtensionPriority = new string[] { ".mp3", ".wav", ".ogg" };
        private static readonly string[] KnownFlac = new string[] { ".flac" };

        private readonly string _root;
        private readonly List<string> _boxes = new List<string>();
        private readonly Dictionary<string, AudioClip> _clipCache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loggedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 文件解析结果缓存：值可能为 null（表示已确认缺失）。
        // 没有它时，像 MusicManager.Update 每帧反复请求同一文件会产生大量磁盘 IO。
        private readonly Dictionary<string, string> _resolveCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public BoxLibrary(string dataRoot)
        {
            _root = dataRoot;
        }

        public string Root { get { return _root; } }

        public IList<string> Boxes { get { return _boxes; } }

        /// <summary>重新扫描 data 目录下的音乐盒文件夹。</summary>
        public void Refresh()
        {
            _boxes.Clear();
            _resolveCache.Clear();
            try
            {
                if (!Directory.Exists(_root))
                {
                    Directory.CreateDirectory(_root);
                    Log.Info("已创建 data 目录：" + _root);
                    return;
                }

                string[] dirs = Directory.GetDirectories(_root);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string name = Path.GetFileName(dirs[i]);
                    if (string.IsNullOrEmpty(name) || name.Trim().Length == 0) continue;
                    if (name == "." || name == "..") continue;
                    if (!seen.Add(name))
                    {
                        Log.Warn("音乐盒文件夹名称大小写冲突，已忽略：" + name);
                        continue;
                    }
                    if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        Log.Warn("音乐盒文件夹名称包含非法字符，已忽略：" + name);
                        continue;
                    }
                    _boxes.Add(name);
                }
                Log.Info("扫描到 " + _boxes.Count + " 个音乐盒：" + (_boxes.Count == 0 ? "（无）" : string.Join(" / ", _boxes.ToArray())));
            }
            catch (Exception ex)
            {
                Log.Error("扫描音乐盒目录失败：" + ex);
            }
        }

        public bool HasBox(string boxName)
        {
            if (string.IsNullOrEmpty(boxName)) return false;
            string wanted = boxName.Trim();
            for (int i = 0; i < _boxes.Count; i++)
            {
                // 忽略大小写与首尾空白：配置里的值与磁盘目录名可能因为手工编辑/粘贴而不完全一致，
                // 这种情况不应该被判成“音乐盒不存在”而回退游戏默认。
                if (string.Equals(_boxes[i].Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把配置里的名字解析成磁盘上真实存在的文件夹名（大小写以磁盘为准）。</summary>
        public string ResolveBoxName(string boxName)
        {
            if (string.IsNullOrEmpty(boxName)) return null;
            string wanted = boxName.Trim();
            for (int i = 0; i < _boxes.Count; i++)
            {
                if (string.Equals(_boxes[i].Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return _boxes[i];
            }
            return null;
        }

        public int IndexOf(string boxName)
        {
            if (string.IsNullOrEmpty(boxName)) return 0;
            string wanted = boxName.Trim();
            for (int i = 0; i < _boxes.Count; i++)
            {
                if (string.Equals(_boxes[i].Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return i + 1; // 0 = 游戏默认
            }
            return 0;
        }

        public string BoxAtChoiceIndex(int index)
        {
            if (index <= 0 || index > _boxes.Count) return null;
            return _boxes[index - 1];
        }

        /// <summary>
        /// 解析事件音频文件。返回 null 表示缺失（调用方回退游戏默认）。
        /// </summary>
        public string ResolveFile(string boxName, string fileBaseName, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(boxName)) { reason = "未选择音乐盒"; return null; }
            if (!HasBox(boxName)) { reason = "音乐盒不存在：" + boxName; return null; }

            string cacheKey = boxName + "|" + fileBaseName;
            string cached;
            if (_resolveCache.TryGetValue(cacheKey, out cached))
            {
                if (cached == null) reason = "缺少文件 " + fileBaseName + ".mp3/.wav/.ogg";
                return cached;
            }

            string dir = Path.Combine(_root, boxName);
            if (!Directory.Exists(dir)) { reason = "音乐盒目录不存在：" + dir; return null; }

            // 1) 主文件名
            for (int i = 0; i < ExtensionPriority.Length; i++)
            {
                string p = Path.Combine(dir, fileBaseName + ExtensionPriority[i]);
                if (File.Exists(p)) { _resolveCache[cacheKey] = p; return p; }
            }

            // 1.5) 宽松匹配：文件名可能带多余空格（例如 "waiting_map_1 .mp3"）或大小写不同。
            //      很多音乐盒是手工整理的，这类差异很常见，不应该被判成“缺失”。
            try
            {
                string[] all = Directory.GetFiles(dir);
                for (int i = 0; i < all.Length; i++)
                {
                    string stem = Path.GetFileNameWithoutExtension(all[i]).Trim();
                    if (!string.Equals(stem, fileBaseName, StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(all[i]);
                    ext = ext == null ? string.Empty : ext.ToLowerInvariant();
                    for (int k = 0; k < ExtensionPriority.Length; k++)
                    {
                        if (ExtensionPriority[k] != ext) continue;
                        Log.Info("文件名宽松匹配命中：" + Path.GetFileName(all[i]) + " -> " + fileBaseName);
                        _resolveCache[cacheKey] = all[i];
                        return all[i];
                    }
                }
            }
            catch { }

            // 2) 分段：<name>_1 / <name>_2 ...
            List<string> segs = new List<string>();
            for (int i = 0; i < ExtensionPriority.Length; i++)
            {
                string[] found;
                try { found = Directory.GetFiles(dir, fileBaseName + "_*" + ExtensionPriority[i]); }
                catch { found = new string[0]; }
                for (int f = 0; f < found.Length; f++)
                {
                    string stem = Path.GetFileNameWithoutExtension(found[f]);
                    string suffix = stem.Substring(fileBaseName.Length);
                    if (suffix.Length < 2 || suffix[0] != '_') continue;
                    bool digits = true;
                    for (int c = 1; c < suffix.Length; c++)
                    {
                        if (!char.IsDigit(suffix[c])) { digits = false; break; }
                    }
                    if (digits) segs.Add(found[f]);
                }
                if (segs.Count > 0) break; // 按扩展名优先级取一组
            }
            if (segs.Count > 0)
            {
                segs.Sort(StringComparer.OrdinalIgnoreCase);
                string pick = segs[UnityEngine.Random.Range(0, segs.Count)];
                Log.Info("分段命中：" + Path.GetFileName(pick) + "（共 " + segs.Count + " 段，随机选择）");
                _resolveCache[cacheKey] = pick;
                return pick;
            }

            // 3) flac：Unity/NAudio 无解码器，明确记为不支持
            for (int i = 0; i < KnownFlac.Length; i++)
            {
                string p = Path.Combine(dir, fileBaseName + KnownFlac[i]);
                if (File.Exists(p))
                {
                    reason = "存在 " + fileBaseName + ".flac，但本模组运行时不支持 FLAC 解码（Unity 2022 + NAudio 均无 FLAC 解码器），已回退游戏默认";
                    _resolveCache[cacheKey] = null;
                    return null;
                }
            }

            reason = "缺少文件 " + fileBaseName + ".mp3/.wav/.ogg";
            _resolveCache[cacheKey] = null;
            return null;
        }

        /// <summary>加载音频（带缓存）。失败返回 null。</summary>
        public AudioClip LoadClip(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            AudioClip cached;
            if (_clipCache.TryGetValue(path, out cached) && cached != null) return cached;

            try
            {
                string ext = Path.GetExtension(path);
                if (ext == null) ext = string.Empty;
                ext = ext.ToLowerInvariant();
                bool supported = false;
                for (int i = 0; i < ExtensionPriority.Length; i++)
                {
                    if (ExtensionPriority[i] == ext) { supported = true; break; }
                }
                if (!supported)
                {
                    Log.Warn("不支持的音频格式（仅支持 mp3/wav/ogg）：" + path);
                    return null;
                }

                AudioClip clip = AssetLoader.LoadAudioFromFile(path);
                if (clip == null)
                {
                    Log.Warn("音频加载失败：" + path);
                    return null;
                }
                _clipCache[path] = clip;
                return clip;
            }
            catch (Exception ex)
            {
                Log.Error("音频加载异常：" + path + " -> " + ex.Message);
                return null;
            }
        }

        public void LogMissingOnce(string boxName, string fileBaseName, string reason)
        {
            string key = boxName + "|" + fileBaseName;
            if (!_loggedMissing.Add(key)) return;
            Log.Warn("事件音频不可用（" + boxName + " / " + fileBaseName + "）：" + reason + "，本次回退游戏默认行为。");
        }

        public void ClearClipCache()
        {
            _clipCache.Clear();
            _loggedMissing.Clear();
            _resolveCache.Clear();
        }
    }
}