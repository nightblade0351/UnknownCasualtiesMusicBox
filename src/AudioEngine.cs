using System;
using System.Collections.Generic;
using UnityEngine;

namespace UCMB
{
    internal sealed class Voice
    {
        internal AudioSource Source;
        internal AudioLowPassFilter Filter;
        internal float Target;
        internal float Speed;      // 每秒音量变化量
        internal bool FadingOut;
        internal float StartVolume;
        internal float Elapsed;
        internal string Tag;
        internal float Logical;   // 未乘 duck 的逻辑音量
    }

    /// <summary>
    /// 统一音频引擎：新音乐立即播放，旧音乐 3 秒淡出后停止。
    /// 所有 AudioSource 均为 2D、独立于游戏 Time.timeScale（暂停时仍可淡出）。
    /// </summary>
    internal sealed class AudioEngine
    {
        internal const float FadeSeconds = 1.5f;
        /// <summary>新音乐淡入时长（秒）。太长会显得迟滞，太短会“砰”地一下进来。</summary>
        internal const float FadeInSeconds = 1.5f;
        private const int MaxVoices = 8;

        private readonly Transform _parent;
        private readonly List<Voice> _voices = new List<Voice>();
        private AudioMixerGroupProxy _mixerProxy;

        internal AudioEngine(Transform parent)
        {
            _parent = parent;
        }

        internal float MusicVolumeScale = 1f;

        private const float NormalCutoff = 22000f;
        private const float MuffledCutoff = 480f;

        /// <summary>
        /// 外部“闷音”系数：1 = 正常；&lt;1 表示被压低（模拟原版对话/交易的 ducking）。
        /// 只降低音量与高频，不做静音——保留“闷响感”但不消失。
        /// </summary>
        internal float Duck = 1f;
        private float _duckCur = 1f;

        /// <summary>
        /// 原版音乐总线的低通截止频率（MusicManager.lowCutoff，250~30000）。
        /// &gt;0 时与自身 duck 曲线取较小值，用来复现原版“进图后变闷”的听感。
        /// </summary>
        internal float CutoffCap;

        /// <summary>把 AudioSource 挂到与游戏音乐相同的混音组（继承静音时段等效果）。</summary>
        internal void SetMixerGroup(UnityEngine.Audio.AudioMixerGroup group)
        {
            _mixerProxy = new AudioMixerGroupProxy(group);
            for (int i = 0; i < _voices.Count; i++)
            {
                if (_voices[i].Source != null) _voices[i].Source.outputAudioMixerGroup = group;
            }
        }

        internal UnityEngine.Audio.AudioMixerGroup MixerGroup
        {
            get { return _mixerProxy == null ? null : _mixerProxy.Group; }
        }

        private sealed class AudioMixerGroupProxy
        {
            internal readonly UnityEngine.Audio.AudioMixerGroup Group;
            internal AudioMixerGroupProxy(UnityEngine.Audio.AudioMixerGroup g) { Group = g; }
        }

        private AudioSource CreateSource()
        {
            GameObject go = new GameObject("UC-MusicBox-Voice");
            if (_parent != null) go.transform.SetParent(_parent, false);
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            src.dopplerLevel = 0f;
            src.bypassReverbZones = true;
            src.bypassEffects = false;
            src.volume = 0f;
            AudioLowPassFilter lpf = go.AddComponent<AudioLowPassFilter>();
            lpf.cutoffFrequency = NormalCutoff;
            lpf.lowpassResonanceQ = 1f;
            if (MixerGroup != null) src.outputAudioMixerGroup = MixerGroup;
            return src;
        }

        /// <summary>淡出所有正在播放的声音。</summary>
        internal void FadeOutAll(string exceptTag)
        {
            for (int i = 0; i < _voices.Count; i++)
            {
                Voice v = _voices[i];
                if (v.FadingOut) continue;
                if (exceptTag != null && v.Tag == exceptTag) continue;
                BeginFadeOut(v);
            }
        }

        private static void BeginFadeOut(Voice v)
        {
            v.FadingOut = true;
            v.Target = 0f;
            float vol = v.Logical;   // 用逻辑音量，避免 duck 时淡出速度被放大
            v.Speed = Mathf.Max(vol, 0.001f) / FadeSeconds;
            v.Elapsed = 0f;
        }

        /// <summary>
        /// 播放新音乐：立即开始，同时把其它声音在 3 秒内淡出。
        /// volume 为最终线性音量（已含比例与游戏音乐音量）。
        /// </summary>
        internal Voice Play(AudioClip clip, bool loop, float volume, string tag)
        {
            if (clip == null) return null;
            try
            {
                // 同一事件、同一首曲正在淡出：取消淡出并把它“拉回”目标音量，
                // 而不是新建一个 voice 从头播——否则听感就是“淡到一半又从头开始”。
                for (int i = 0; i < _voices.Count; i++)
                {
                    Voice e = _voices[i];
                    if (e == null || e.Source == null) continue;
                    if (e.Tag != tag || e.Source.clip != clip) continue;
                    if (!e.FadingOut)
                    {
                        // 关键：voice 可能已经自然播完但还没被 Tick 回收。
                        // 直接返回它会让调用方以为“已经在播”，实际是一段静音。
                        if (!e.Source.isPlaying) continue;
                        e.Target = Mathf.Clamp01(volume);
                        e.Speed = SpeedTo(e);
                        FadeOutAll(tag);
                        return e;
                    }
                    e.FadingOut = false;
                    e.Target = Mathf.Clamp01(volume);
                    e.Speed = SpeedTo(e);
                    e.Elapsed = 0f;
                    FadeOutAll(tag);
                    Log.Info("恢复正在淡出的音乐：" + tag + "（避免从头重播）");
                    return e;
                }

                FadeOutAll(null);
                AudioSource src = CreateSource();
                src.clip = clip;
                src.loop = loop;
                src.volume = 0f;
                Voice v = new Voice();
                v.Source = src;
                v.Filter = src.GetComponent<AudioLowPassFilter>();
                v.Tag = tag;
                v.Target = Mathf.Clamp01(volume);
                v.StartVolume = v.Target;
                v.Speed = Mathf.Max(0.0001f, v.StartVolume / FadeInSeconds); // 淡入，避免生硬切入
                v.FadingOut = false;
                v.Elapsed = 0f;
                _voices.Add(v);
                src.Play();
                TrimVoices();
                return v;
            }
            catch (Exception ex)
            {
                Log.Error("播放音频失败：" + ex.Message, ex);
                return null;
            }
        }

        internal bool IsTagPlaying(string tag)
        {
            for (int i = 0; i < _voices.Count; i++)
            {
                Voice v = _voices[i];
                if (v.Tag != tag || v.FadingOut) continue;
                if (v.Source != null && v.Source.isPlaying) return true;
            }
            return false;
        }

        /// <summary>
        /// 是否还有任意一首（含正在淡出的）在出声。
        /// 用于菜单场景判断“音乐盒音乐是否已经接管”，从而决定要不要压掉原版菜单音乐。
        /// </summary>
        internal bool AnyAudible()
        {
            for (int i = 0; i < _voices.Count; i++)
            {
                Voice v = _voices[i];
                if (v.Source == null) continue;
                if (!v.Source.isPlaying) continue;
                if (v.Logical > 0.0005f) return true;
            }
            return false;
        }

        internal void SetVolumeForTag(string tag, float volume)
        {
            for (int i = 0; i < _voices.Count; i++)
            {
                Voice v = _voices[i];
                if (v.Tag != tag) continue;
                v.Target = Mathf.Clamp01(volume);
                v.StartVolume = v.Target;
                v.Speed = SpeedTo(v);
            }
        }

        /// <summary>
        /// 按“与目标的差值 / 0.25 秒”算变化速度，并设一个下限。
        /// 之前用的是 Target/0.25：当把音量调到 0（或很小）时，速度也趋近 0，
        /// 结果滑条拉到 0% 声音几乎不下降——看起来就是“单项音量调节无效”。
        /// </summary>
        private static float SpeedTo(Voice v)
        {
            float delta = Mathf.Abs(v.Target - v.Logical);
            return Mathf.Max(0.8f, delta / 0.25f);
        }

        internal void Tick(float dt)
        {
            if (dt <= 0f) return;
            _duckCur = Mathf.MoveTowards(_duckCur, Mathf.Clamp01(Duck), dt * 2.5f);
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                Voice v = _voices[i];
                if (v.Source == null) { _voices.RemoveAt(i); continue; }

                if (v.Filter != null)
                {
                    // 用非线性映射：duck=0.32 时 cutoff 约 2.2kHz，闷感明显；
                    // 线性映射只有约 8kHz，几乎听不出区别。
                    float shaped = Mathf.Pow(_duckCur, 2.0f);
                    float cutoff = Mathf.Lerp(MuffledCutoff, NormalCutoff, shaped);
                    if (CutoffCap > 0f && CutoffCap < cutoff) cutoff = CutoffCap;
                    v.Filter.cutoffFrequency = cutoff;
                }

                v.Elapsed += dt;
                if (v.FadingOut)
                {
                    v.Logical = Mathf.MoveTowards(v.Logical, 0f, v.Speed * dt);
                    v.Source.volume = v.Logical * _duckCur;
                    if (v.Logical <= 0.0005f || v.Elapsed >= FadeSeconds + 0.5f)
                    {
                        SafeStop(v.Source);
                        UnityEngine.Object.Destroy(v.Source.gameObject);
                        _voices.RemoveAt(i);
                    }
                }
                else
                {
                    v.Logical = Mathf.MoveTowards(v.Logical, v.Target, v.Speed * dt);
                    v.Source.volume = v.Logical * _duckCur;
                    if (!v.Source.isPlaying)
                    {
                        // 非循环音乐自然播完
                        UnityEngine.Object.Destroy(v.Source.gameObject);
                        _voices.RemoveAt(i);
                    }
                }
            }
        }

        private static void SafeStop(AudioSource src)
        {
            try { if (src != null) src.Stop(); }
            catch { }
        }

        private void TrimVoices()
        {
            if (_voices.Count <= MaxVoices) return;
            int guard = 0;
            while (_voices.Count > MaxVoices && guard++ < 32)
            {
                int idx = -1;
                for (int i = 0; i < _voices.Count; i++)
                {
                    if (_voices[i].FadingOut) { idx = i; break; }
                }
                if (idx < 0) idx = 0;
                Voice v = _voices[idx];
                if (v.Source != null)
                {
                    SafeStop(v.Source);
                    UnityEngine.Object.Destroy(v.Source.gameObject);
                }
                _voices.RemoveAt(idx);
            }
        }

        internal void StopAllImmediate()
        {
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                Voice v = _voices[i];
                if (v.Source != null)
                {
                    SafeStop(v.Source);
                    UnityEngine.Object.Destroy(v.Source.gameObject);
                }
            }
            _voices.Clear();
        }

        // ---- 仅供开发期自检使用 ----
        internal int VoiceCount { get { return _voices.Count; } }

        internal int FadingCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _voices.Count; i++) if (_voices[i].FadingOut) n++;
                return n;
            }
        }

        internal string DescribeVoices()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < _voices.Count; i++)
            {
                Voice v = _voices[i];
                sb.Append(v.Tag).Append(" vol=")
                  .Append(v.Source == null ? -1f : v.Source.volume)
                  .Append(v.FadingOut ? " [fading] " : " ");
            }
            return sb.ToString();
        }
    }
}