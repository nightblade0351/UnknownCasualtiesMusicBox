using System;
using BepInEx.Logging;

namespace UCMB
{
    /// <summary>统一日志前缀，便于在 BepInEx 日志中定位本模组输出。</summary>
    internal static class Log
    {
        internal const string Prefix = "[UC-MusicBox] ";
        internal static ManualLogSource Source;

        internal static void Info(string msg)
        {
            if (Source != null) Source.LogInfo(Prefix + msg);
        }

        internal static void Warn(string msg)
        {
            if (Source != null) Source.LogWarning(Prefix + msg);
        }

        internal static void Error(string msg)
        {
            if (Source != null) Source.LogError(Prefix + msg);
        }

        internal static void Error(string msg, Exception ex)
        {
            if (Source != null) Source.LogError(Prefix + msg + " :: " + (ex == null ? "(null)" : ex.ToString()));
        }

        // ---- 节流 -------------------------------------------------------
        // 有些位置会被每帧调用（MusicManager.Update 反复请求同一首歌、
        // 世界生成期间反复调用 PlayLayerSong 等），必须节流，否则日志会被刷爆。
        private static readonly System.Collections.Generic.Dictionary<string, float> _lastAt =
            new System.Collections.Generic.Dictionary<string, float>();

        internal static void Throttled(string key, float seconds, string msg)
        {
            float now = UnityEngine.Time.unscaledTime;
            float last;
            if (_lastAt.TryGetValue(key, out last) && now - last < seconds) return;
            _lastAt[key] = now;
            Info(msg);
        }

        internal static void WarnThrottled(string key, float seconds, string msg)
        {
            float now = UnityEngine.Time.unscaledTime;
            float last;
            if (_lastAt.TryGetValue(key, out last) && now - last < seconds) return;
            _lastAt[key] = now;
            Warn(msg);
        }
    }
}