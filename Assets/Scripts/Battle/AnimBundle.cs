using System;
using System.Collections.Generic;
using System.Text;
using HeroDefense.Config;
using HeroDefense.Engine.Host;
using Newtonsoft.Json;
using UnityEngine;

namespace HeroDefense.Battle
{
    public sealed class BundleState
    {
        internal readonly BundleFrameData[] Frames;
        internal readonly BundleEventData[] Events;
        internal readonly BundleSelfKeyData[] SelfKeys;
        internal readonly BundleTrackData[] Tracks;

        public bool Loop { get; }
        public bool HideHp { get; }
        public bool Above { get; }
        public int FrameCount => Frames.Length;

        internal BundleState(bool loop, BundleFrameData[] frames, BundleEventData[] events,
            BundleSelfKeyData[] selfKeys, BundleTrackData[] tracks, bool hideHp, bool above)
        {
            Loop = loop;
            HideHp = hideHp;
            Above = above;
            Frames = frames;
            Events = events;
            SelfKeys = selfKeys;
            Tracks = tracks;
        }
    }

    internal struct BundleFrameData
    {
        internal int FramePoolIndex;
        internal float Duration;
    }

    internal struct BundleEventData
    {
        internal int TimelineIndex;
        internal string Name;
    }

    internal struct BundleSelfKeyData
    {
        internal int TimelineIndex;
        internal float Dx;
        internal float Dy;
        internal float Alpha;
    }

    internal struct BundleTrackCellData
    {
        internal int TimelineIndex;
        internal int FrameIndex;
        internal float Dx;
        internal float Dy;
        internal float ScaleX;
        internal float ScaleY;
        internal float Alpha;
    }

    internal sealed class BundleTrackData
    {
        internal string SourceBaseKey;
        internal string SourceState;
        internal bool Above;
        internal BundleTrackCellData[] Cells;
        internal bool ResolveWarningLogged;
    }

    public struct BundleSelfKey
    {
        public float Time;
        public float Dx;
        public float Dy;
        public float Alpha;
    }

    public sealed class BundleTrackClip
    {
        public Sprite[] Sprites;
        public int[] TimelineIndices;
        public float[] Times;
        public float[] Dx;
        public float[] Dy;
        public float[] ScaleX;
        public float[] ScaleY;
        public float[] Alpha;
        public float EndTime;
        public bool Above;
    }

    public sealed class BundleClip
    {
        public Sprite[] Frames;
        public float[] Durations;
        public bool Looping;
        public Action<int> OnFrameEnter;
        public BundleSelfKey[] SelfKeys;
        public BundleTrackClip[] Tracks;
        public int CanvasW;
        public int CanvasH;
        public float TotalDuration;
        public bool HideHp;
    }

    public sealed class RuntimeOverlayClip
    {
        public Sprite[] Frames;
        public float[] Durations;
        public bool Looping;
        public bool Above;
        public float TotalDuration;
    }

    /// <summary>
    /// 已解析的 .hdanim 数据。图集纹理和 Sprite 池与实例同寿命，由 AnimBundleCache 进程内持有。
    /// </summary>
    public sealed class AnimBundle
    {
        private readonly Texture2D[] _atlases;
        private readonly Sprite[] _frameSprites;
        private readonly Dictionary<string, BundleState> _states;

        public string BaseKey { get; }
        public int CanvasW { get; }
        public int CanvasH { get; }

        internal AnimBundle(string baseKey, int canvasW, int canvasH, Texture2D[] atlases,
            Sprite[] frameSprites, Dictionary<string, BundleState> states)
        {
            BaseKey = baseKey;
            CanvasW = canvasW;
            CanvasH = canvasH;
            _atlases = atlases;
            _frameSprites = frameSprites;
            _states = states;
        }

        public bool TryGetState(string stateName, out BundleState state)
        {
            if (string.IsNullOrEmpty(stateName))
            {
                state = null;
                return false;
            }
            return _states.TryGetValue(stateName, out state);
        }

        public Sprite GetFrameSprite(int framePoolIndex)
        {
            return framePoolIndex >= 0 && framePoolIndex < _frameSprites.Length
                ? _frameSprites[framePoolIndex]
                : null;
        }

        internal Sprite ResolveTrackSprite(BundleTrackData track, int sequenceIndex)
        {
            if (track == null || sequenceIndex < 0)
            {
                WarnTrackResolve(track, sequenceIndex, "帧序号非法");
                return null;
            }

            if (string.IsNullOrEmpty(track.SourceBaseKey))
            {
                var local = GetFrameSprite(sequenceIndex);
                if (local == null) WarnTrackResolve(track, sequenceIndex, "本 bundle 帧池引用越界");
                return local;
            }

            if (AnimBundleCache.TryGet(track.SourceBaseKey, out var external))
            {
                if (!external.TryGetState(track.SourceState, out var sourceState))
                {
                    WarnTrackResolve(track, sequenceIndex,
                        $"外部 bundle 缺 state '{track.SourceState}'");
                    return null;
                }
                if (sequenceIndex >= sourceState.Frames.Length)
                {
                    WarnTrackResolve(track, sequenceIndex, "外部 bundle 时间线引用越界");
                    return null;
                }

                var sprite = external.GetFrameSprite(sourceState.Frames[sequenceIndex].FramePoolIndex);
                if (sprite == null) WarnTrackResolve(track, sequenceIndex, "外部 bundle 帧池引用越界");
                return sprite;
            }

            var flat = LuaHost.LoadSprite(
                $"resources/art/{track.SourceBaseKey}_{track.SourceState}_{sequenceIndex}.png", false);
            if (flat == null) WarnTrackResolve(track, sequenceIndex, "外部 bundle 与扁平帧均未命中");
            return flat;
        }

        private void WarnTrackResolve(BundleTrackData track, int sequenceIndex, string reason)
        {
            if (track == null || track.ResolveWarningLogged) return;
            track.ResolveWarningLogged = true;
            Debug.LogWarning(
                $"[AnimBundle] 特效轨帧解析失败，相关 cell 将置空（base={BaseKey}, src={track.SourceBaseKey}, state={track.SourceState}, frame={sequenceIndex}）：{reason}");
        }
    }

    /// <summary>
    /// .hdanim 进程内缓存。null 同时表示文件不存在或无效，避免重复触碰热更文件系统。
    /// </summary>
    public static class AnimBundleCache
    {
        private const ushort ContainerVersion = 1;
        private const int DataVersion = 2;
        private const int MaxAtlasCount = 4;
        private const int MaxAtlasSize = 2048;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static readonly Dictionary<string, AnimBundle> Cache =
            new Dictionary<string, AnimBundle>(StringComparer.Ordinal);

        public static bool TryGet(string baseKey, out AnimBundle bundle)
        {
            bundle = null;
            string normalized = NormalizeBaseKey(baseKey);
            if (string.IsNullOrEmpty(normalized)) return false;
            if (Cache.TryGetValue(normalized, out bundle)) return bundle != null;

            string path = $"resources/art/{normalized}.hdanim";
            try
            {
                if (!ResourceHost.Exists(path))
                {
                    Cache[normalized] = null;
                    return false;
                }

                var bytes = ResourceHost.ReadBytes(path);
                if (bytes == null || bytes.Length == 0)
                    return CacheInvalid(normalized, path, "文件为空", out bundle);

                if (!TryParse(normalized, bytes, out bundle, out string reason))
                    return CacheInvalid(normalized, path, reason, out bundle);

                Cache[normalized] = bundle;
                return true;
            }
            catch (Exception e)
            {
                return CacheInvalid(normalized, path, e.Message, out bundle);
            }
        }

        private static bool CacheInvalid(string normalizedBaseKey, string path, string reason,
            out AnimBundle bundle)
        {
            bundle = null;
            Cache[normalizedBaseKey] = null;
            Debug.LogWarning($"[AnimBundle] 动画包无效，整包回落旧链（{path}）：{reason}");
            return false;
        }

        private static bool TryParse(string expectedBaseKey, byte[] bytes, out AnimBundle bundle,
            out string reason)
        {
            bundle = null;
            var cursor = new ByteCursor(bytes);

            if (!cursor.TryReadByte(out byte m0) || !cursor.TryReadByte(out byte m1)
                || !cursor.TryReadByte(out byte m2) || !cursor.TryReadByte(out byte m3)
                || m0 != (byte)'H' || m1 != (byte)'D' || m2 != (byte)'A' || m3 != (byte)'N')
            {
                reason = "magic 必须为 HDAN";
                return false;
            }
            if (!cursor.TryReadUInt16(out ushort version) || version != ContainerVersion)
            {
                reason = $"容器 version 必须为 {ContainerVersion}";
                return false;
            }
            if (!cursor.TryReadUInt16(out ushort reserved) || reserved != 0)
            {
                reason = "reserved 必须为 0";
                return false;
            }
            if (!cursor.TryReadUInt32(out uint jsonLengthRaw) || jsonLengthRaw == 0
                || jsonLengthRaw > int.MaxValue)
            {
                reason = "json_len 非法";
                return false;
            }

            int jsonLength = (int)jsonLengthRaw;
            if (!cursor.TryReadUtf8(jsonLength, out string json, out bool hasBom))
            {
                reason = "json_len 越界或 UTF-8 数据无效";
                return false;
            }
            if (hasBom)
            {
                reason = "json 必须为无 BOM UTF-8";
                return false;
            }

            BundleJsonDefinition definition;
            try
            {
                definition = JsonConvert.DeserializeObject<BundleJsonDefinition>(json);
            }
            catch (Exception e)
            {
                reason = $"json 反序列化失败：{e.Message}";
                return false;
            }

            if (!ValidateDefinition(definition, expectedBaseKey, out reason)) return false;

            int atlasCount = definition.Atlases.Count;
            var pngBlocks = new byte[atlasCount][];
            for (int i = 0; i < atlasCount; i++)
            {
                if (!cursor.TryReadUInt32(out uint pngLengthRaw) || pngLengthRaw == 0
                    || pngLengthRaw > int.MaxValue)
                {
                    reason = $"atlas[{i}] png_len 非法";
                    return false;
                }
                if (!cursor.TryReadBytes((int)pngLengthRaw, out pngBlocks[i]))
                {
                    reason = $"atlas[{i}] png_len 越界";
                    return false;
                }
            }
            if (cursor.Remaining != 0)
            {
                reason = "最后一张图集后存在未定义尾部数据";
                return false;
            }

            var textures = new Texture2D[atlasCount];
            var sprites = new Sprite[definition.Frames.Count];
            try
            {
                FilterMode filterMode = ResolveFilterMode();
                for (int i = 0; i < atlasCount; i++)
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    textures[i] = texture;
                    if (!texture.LoadImage(pngBlocks[i]))
                    {
                        reason = $"atlas[{i}] PNG 解码失败";
                        DestroyCreated(sprites, textures);
                        return false;
                    }
                    var declared = definition.Atlases[i];
                    if (texture.width != declared.Width || texture.height != declared.Height)
                    {
                        reason =
                            $"atlas[{i}] 尺寸不匹配：json={declared.Width}x{declared.Height}, png={texture.width}x{texture.height}";
                        DestroyCreated(sprites, textures);
                        return false;
                    }
                    texture.name = $"{expectedBaseKey}_atlas_{i}";
                    texture.filterMode = filterMode;
                    texture.wrapMode = TextureWrapMode.Clamp;
                }

                int canvasW = definition.Canvas[0];
                int canvasH = definition.Canvas[1];
                for (int i = 0; i < definition.Frames.Count; i++)
                {
                    var source = definition.Frames[i];
                    int x = source.Rect[0];
                    int y = source.Rect[1];
                    int w = source.Rect[2];
                    int h = source.Rect[3];
                    int ox = source.Offset[0];
                    int oy = source.Offset[1];
                    int atlasH = definition.Atlases[source.AtlasIndex].Height;

                    float unityY = atlasH - y - h;
                    float pivotX = (canvasW * 0.5f - ox) / w;
                    float pivotY = -(canvasH - oy - h) / (float)h;
                    var sprite = Sprite.Create(
                        textures[source.AtlasIndex],
                        new Rect(x, unityY, w, h),
                        new Vector2(pivotX, pivotY),
                        100f);
                    if (sprite == null)
                    {
                        reason = $"frame[{i}] Sprite.Create 失败";
                        DestroyCreated(sprites, textures);
                        return false;
                    }
                    sprite.name = $"{expectedBaseKey}_frame_{i}";
                    sprites[i] = sprite;
                }

                var states = BuildRuntimeStates(definition, expectedBaseKey);
                bundle = new AnimBundle(expectedBaseKey, canvasW, canvasH, textures, sprites, states);
                reason = null;
                return true;
            }
            catch (Exception e)
            {
                DestroyCreated(sprites, textures);
                reason = $"图集或 Sprite 创建失败：{e.Message}";
                return false;
            }
        }

        private static Dictionary<string, BundleState> BuildRuntimeStates(
            BundleJsonDefinition definition, string baseKey)
        {
            var result = new Dictionary<string, BundleState>(
                definition.States.Count, StringComparer.Ordinal);
            foreach (var pair in definition.States)
            {
                var source = pair.Value;

                var frames = new BundleFrameData[source.Frames.Count];
                for (int i = 0; i < frames.Length; i++)
                {
                    frames[i] = new BundleFrameData
                    {
                        FramePoolIndex = source.Frames[i].FrameIndex,
                        Duration = source.Frames[i].Duration,
                    };
                }

                BundleEventData[] events = null;
                if (source.Events != null && source.Events.Count > 0)
                {
                    events = new BundleEventData[source.Events.Count];
                    for (int i = 0; i < events.Length; i++)
                    {
                        events[i] = new BundleEventData
                        {
                            TimelineIndex = source.Events[i].TimelineIndex,
                            Name = source.Events[i].Name,
                        };
                    }
                }

                BundleSelfKeyData[] selfKeys = null;
                if (source.SelfKeys != null && source.SelfKeys.Count > 0)
                {
                    selfKeys = new BundleSelfKeyData[source.SelfKeys.Count];
                    for (int i = 0; i < selfKeys.Length; i++)
                    {
                        selfKeys[i] = new BundleSelfKeyData
                        {
                            TimelineIndex = source.SelfKeys[i].TimelineIndex,
                            Dx = source.SelfKeys[i].Dx,
                            Dy = source.SelfKeys[i].Dy,
                            Alpha = source.SelfKeys[i].Alpha,
                        };
                    }
                }

                BundleTrackData[] tracks = null;
                if (source.Tracks != null && source.Tracks.Count > 0)
                {
                    tracks = new BundleTrackData[source.Tracks.Count];
                    for (int i = 0; i < tracks.Length; i++)
                    {
                        var sourceTrack = source.Tracks[i];
                        var cells = new BundleTrackCellData[sourceTrack.Cells.Count];
                        for (int k = 0; k < cells.Length; k++)
                        {
                            var sourceCell = sourceTrack.Cells[k];
                            cells[k] = new BundleTrackCellData
                            {
                                TimelineIndex = sourceCell.TimelineIndex,
                                FrameIndex = sourceCell.FrameIndex,
                                Dx = sourceCell.Dx,
                                Dy = sourceCell.Dy,
                                ScaleX = sourceCell.ScaleX,
                                ScaleY = sourceCell.ScaleY,
                                Alpha = sourceCell.Alpha,
                            };
                        }
                        tracks[i] = new BundleTrackData
                        {
                            SourceBaseKey = NormalizeBaseKey(sourceTrack.SourceBaseKey),
                            SourceState = sourceTrack.SourceState,
                            Above = sourceTrack.Order == "above",
                            Cells = cells,
                        };
                    }
                }

                bool above = true;
                if (source.Order == "below")
                {
                    above = false;
                }
                else if (!string.IsNullOrEmpty(source.Order) && source.Order != "above")
                {
                    Debug.LogWarning(
                        $"[AnimBundle] state.order 非法，按 above 处理（base={baseKey}, state={pair.Key}, order={source.Order}）");
                }

                result[pair.Key] = new BundleState(
                    source.Loop, frames, events, selfKeys, tracks, source.HideHp, above);
            }
            return result;
        }

        private static bool ValidateDefinition(BundleJsonDefinition definition, string expectedBaseKey,
            out string reason)
        {
            if (definition == null)
            {
                reason = "根对象为空";
                return false;
            }
            if (definition.DataVersion != DataVersion)
            {
                reason = $"data_version 必须为 {DataVersion}，实际 {definition.DataVersion}";
                return false;
            }
            if (!string.Equals(definition.BaseKey, expectedBaseKey, StringComparison.Ordinal))
            {
                reason = $"base_key 必须精确匹配 {expectedBaseKey}";
                return false;
            }
            if (definition.Canvas == null || definition.Canvas.Length != 2
                || definition.Canvas[0] <= 0 || definition.Canvas[1] <= 0)
            {
                reason = "canvas 必须为两个有限正整数";
                return false;
            }
            if (definition.Atlases == null || definition.Atlases.Count == 0
                || definition.Atlases.Count > MaxAtlasCount)
            {
                reason = $"atlases 张数必须为 1..{MaxAtlasCount}";
                return false;
            }
            for (int i = 0; i < definition.Atlases.Count; i++)
            {
                var atlas = definition.Atlases[i];
                if (atlas == null || atlas.Width <= 0 || atlas.Height <= 0
                    || atlas.Width > MaxAtlasSize || atlas.Height > MaxAtlasSize)
                {
                    reason = $"atlas[{i}] 尺寸必须在 1..{MaxAtlasSize}";
                    return false;
                }
            }
            if (definition.Frames == null || definition.Frames.Count == 0)
            {
                reason = "frames 缺失或为空";
                return false;
            }
            for (int i = 0; i < definition.Frames.Count; i++)
            {
                var frame = definition.Frames[i];
                if (frame == null || frame.AtlasIndex < 0
                    || frame.AtlasIndex >= definition.Atlases.Count)
                {
                    reason = $"frame[{i}].a 越界";
                    return false;
                }
                if (frame.Rect == null || frame.Rect.Length != 4
                    || frame.Offset == null || frame.Offset.Length != 2)
                {
                    reason = $"frame[{i}] rect/off 维数非法";
                    return false;
                }

                int x = frame.Rect[0];
                int y = frame.Rect[1];
                int w = frame.Rect[2];
                int h = frame.Rect[3];
                var atlas = definition.Atlases[frame.AtlasIndex];
                if (x < 0 || y < 0 || w <= 0 || h <= 0
                    || (long)x + w > atlas.Width || (long)y + h > atlas.Height)
                {
                    reason = $"frame[{i}].rect 越出 atlas[{frame.AtlasIndex}]";
                    return false;
                }
            }
            if (definition.States == null || definition.States.Count == 0)
            {
                reason = "states 缺失或为空";
                return false;
            }

            foreach (var pair in definition.States)
            {
                string stateName = pair.Key;
                var state = pair.Value;
                if (string.IsNullOrWhiteSpace(stateName) || state == null)
                {
                    reason = "state 名为空或 state 对象为空";
                    return false;
                }
                if (state.Frames == null || state.Frames.Count == 0)
                {
                    reason = $"state '{stateName}' 的 frames 缺失或为空";
                    return false;
                }
                for (int i = 0; i < state.Frames.Count; i++)
                {
                    var frame = state.Frames[i];
                    if (frame == null || frame.FrameIndex < 0
                        || frame.FrameIndex >= definition.Frames.Count)
                    {
                        reason = $"state '{stateName}' frame[{i}].f 越界";
                        return false;
                    }
                    if (!IsFinitePositive(frame.Duration))
                    {
                        reason = $"state '{stateName}' frame[{i}].dur 必须为有限正数";
                        return false;
                    }
                }

                if (state.Events != null)
                {
                    for (int i = 0; i < state.Events.Count; i++)
                    {
                        var frameEvent = state.Events[i];
                        if (frameEvent == null || frameEvent.TimelineIndex < 0
                            || frameEvent.TimelineIndex >= state.Frames.Count)
                        {
                            reason = $"state '{stateName}' event[{i}].frame 越界";
                            return false;
                        }
                        if (string.IsNullOrWhiteSpace(frameEvent.Name))
                        {
                            reason = $"state '{stateName}' event[{i}].name 为空";
                            return false;
                        }
                    }
                }

                if (state.SelfKeys != null)
                {
                    for (int i = 0; i < state.SelfKeys.Count; i++)
                    {
                        var key = state.SelfKeys[i];
                        if (key == null || key.TimelineIndex < 0
                            || key.TimelineIndex >= state.Frames.Count)
                        {
                            reason = $"state '{stateName}' self_keys[{i}].at 越界";
                            return false;
                        }
                        if (!IsFinite(key.Dx) || !IsFinite(key.Dy)
                            || !IsUnitAlpha(key.Alpha))
                        {
                            reason = $"state '{stateName}' self_keys[{i}] 变换非法";
                            return false;
                        }
                    }
                }

                if (state.Tracks == null) continue;
                for (int i = 0; i < state.Tracks.Count; i++)
                {
                    var track = state.Tracks[i];
                    if (track == null || track.Cells == null || track.Cells.Count == 0)
                    {
                        reason = $"state '{stateName}' track[{i}] 缺 cells";
                        return false;
                    }
                    if (track.Order != "above" && track.Order != "below")
                    {
                        reason = $"state '{stateName}' track[{i}].order 必须为 above/below";
                        return false;
                    }
                    if (!string.IsNullOrEmpty(track.SourceBaseKey)
                        && string.IsNullOrWhiteSpace(track.SourceState))
                    {
                        reason = $"state '{stateName}' track[{i}] 外部 src 缺 src_state";
                        return false;
                    }

                    int previousAt = -1;
                    for (int k = 0; k < track.Cells.Count; k++)
                    {
                        var cell = track.Cells[k];
                        if (cell == null || cell.TimelineIndex < 0
                            || cell.TimelineIndex >= state.Frames.Count)
                        {
                            reason = $"state '{stateName}' track[{i}].cells[{k}].at 越界";
                            return false;
                        }
                        if (cell.TimelineIndex <= previousAt)
                        {
                            reason = $"state '{stateName}' track[{i}].cells.at 必须严格递增";
                            return false;
                        }
                        previousAt = cell.TimelineIndex;
                        if (cell.FrameIndex < 0
                            || (string.IsNullOrEmpty(track.SourceBaseKey)
                                && cell.FrameIndex >= definition.Frames.Count))
                        {
                            reason = $"state '{stateName}' track[{i}].cells[{k}].f 越界";
                            return false;
                        }
                        if (!IsFinite(cell.Dx) || !IsFinite(cell.Dy)
                            || !IsFinite(cell.ScaleX) || !IsFinite(cell.ScaleY)
                            || !IsUnitAlpha(cell.Alpha))
                        {
                            reason = $"state '{stateName}' track[{i}].cells[{k}] 变换非法";
                            return false;
                        }
                    }
                }
            }

            reason = null;
            return true;
        }

        private static FilterMode ResolveFilterMode()
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var row = cm.GetTableInfo("GameConfig", "key", "anim_bundle_filter_mode");
                    string value = row == null ? null : cm.GetValue<string>(row, "value", null);
                    if (string.Equals(value, "bilinear", StringComparison.OrdinalIgnoreCase))
                        return FilterMode.Bilinear;
                }
            }
            catch
            {
                // 配置未就绪或缺键均按格式契约静默回落 Point。
            }
            return FilterMode.Point;
        }

        private static string NormalizeBaseKey(string baseKey)
        {
            return string.IsNullOrEmpty(baseKey)
                ? string.Empty
                : baseKey.Replace('\\', '/').Trim('/');
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && IsFinite(value);
        }

        private static bool IsUnitAlpha(float value)
        {
            return value >= 0f && value <= 1f && IsFinite(value);
        }

        private static void DestroyCreated(Sprite[] sprites, Texture2D[] textures)
        {
            if (sprites != null)
            {
                for (int i = 0; i < sprites.Length; i++)
                {
                    if (sprites[i] != null) UnityEngine.Object.Destroy(sprites[i]);
                }
            }
            if (textures != null)
            {
                for (int i = 0; i < textures.Length; i++)
                {
                    if (textures[i] != null) UnityEngine.Object.Destroy(textures[i]);
                }
            }
        }

        private struct ByteCursor
        {
            private readonly byte[] _bytes;
            private int _offset;

            internal int Remaining => _bytes.Length - _offset;

            internal ByteCursor(byte[] bytes)
            {
                _bytes = bytes;
                _offset = 0;
            }

            internal bool TryReadByte(out byte value)
            {
                if (Remaining < 1)
                {
                    value = 0;
                    return false;
                }
                value = _bytes[_offset++];
                return true;
            }

            internal bool TryReadUInt16(out ushort value)
            {
                if (Remaining < 2)
                {
                    value = 0;
                    return false;
                }
                value = (ushort)(_bytes[_offset] | (_bytes[_offset + 1] << 8));
                _offset += 2;
                return true;
            }

            internal bool TryReadUInt32(out uint value)
            {
                if (Remaining < 4)
                {
                    value = 0;
                    return false;
                }
                value = (uint)(_bytes[_offset]
                    | (_bytes[_offset + 1] << 8)
                    | (_bytes[_offset + 2] << 16)
                    | (_bytes[_offset + 3] << 24));
                _offset += 4;
                return true;
            }

            internal bool TryReadUtf8(int count, out string value, out bool hasBom)
            {
                value = null;
                hasBom = false;
                if (count < 0 || Remaining < count) return false;
                hasBom = count >= 3
                    && _bytes[_offset] == 0xEF
                    && _bytes[_offset + 1] == 0xBB
                    && _bytes[_offset + 2] == 0xBF;
                try
                {
                    value = StrictUtf8.GetString(_bytes, _offset, count);
                    _offset += count;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            internal bool TryReadBytes(int count, out byte[] value)
            {
                value = null;
                if (count < 0 || Remaining < count) return false;
                value = new byte[count];
                Buffer.BlockCopy(_bytes, _offset, value, 0, count);
                _offset += count;
                return true;
            }
        }

        private sealed class BundleJsonDefinition
        {
            [JsonProperty("data_version", Required = Required.Always)]
            public int DataVersion { get; set; }

            [JsonProperty("base_key", Required = Required.Always)]
            public string BaseKey { get; set; }

            [JsonProperty("canvas", Required = Required.Always)]
            public int[] Canvas { get; set; }

            [JsonProperty("atlases", Required = Required.Always)]
            public List<BundleJsonAtlas> Atlases { get; set; }

            [JsonProperty("frames", Required = Required.Always)]
            public List<BundleJsonFrame> Frames { get; set; }

            [JsonProperty("states", Required = Required.Always)]
            public Dictionary<string, BundleJsonState> States { get; set; }
        }

        private sealed class BundleJsonAtlas
        {
            [JsonProperty("w", Required = Required.Always)] public int Width { get; set; }
            [JsonProperty("h", Required = Required.Always)] public int Height { get; set; }
        }

        private sealed class BundleJsonFrame
        {
            [JsonProperty("a", Required = Required.Always)] public int AtlasIndex { get; set; }
            [JsonProperty("rect", Required = Required.Always)] public int[] Rect { get; set; }
            [JsonProperty("off", Required = Required.Always)] public int[] Offset { get; set; }
        }

        private sealed class BundleJsonState
        {
            [JsonProperty("loop", Required = Required.Always)] public bool Loop { get; set; }
            [JsonProperty("frames", Required = Required.Always)] public List<BundleJsonStateFrame> Frames { get; set; }
            [JsonProperty("events")] public List<BundleJsonEvent> Events { get; set; }
            [JsonProperty("self_keys")] public List<BundleJsonSelfKey> SelfKeys { get; set; }
            [JsonProperty("tracks")] public List<BundleJsonTrack> Tracks { get; set; }
            [JsonProperty("hide_hp")] public bool HideHp { get; set; }
            [JsonProperty("order")] public string Order { get; set; }
        }

        private sealed class BundleJsonStateFrame
        {
            [JsonProperty("f", Required = Required.Always)] public int FrameIndex { get; set; }
            [JsonProperty("dur", Required = Required.Always)] public float Duration { get; set; }
        }

        private sealed class BundleJsonEvent
        {
            [JsonProperty("frame", Required = Required.Always)] public int TimelineIndex { get; set; }
            [JsonProperty("name", Required = Required.Always)] public string Name { get; set; }
        }

        private sealed class BundleJsonSelfKey
        {
            [JsonProperty("at", Required = Required.Always)] public int TimelineIndex { get; set; }
            [JsonProperty("dx", Required = Required.Always)] public float Dx { get; set; }
            [JsonProperty("dy", Required = Required.Always)] public float Dy { get; set; }
            [JsonProperty("alpha", Required = Required.Always)] public float Alpha { get; set; }
        }

        private sealed class BundleJsonTrack
        {
            [JsonProperty("src", Required = Required.Always)] public string SourceBaseKey { get; set; }
            [JsonProperty("src_state", Required = Required.Always)] public string SourceState { get; set; }
            [JsonProperty("order", Required = Required.Always)] public string Order { get; set; }
            [JsonProperty("cells", Required = Required.Always)] public List<BundleJsonTrackCell> Cells { get; set; }
        }

        private sealed class BundleJsonTrackCell
        {
            [JsonProperty("at", Required = Required.Always)] public int TimelineIndex { get; set; }
            [JsonProperty("f", Required = Required.Always)] public int FrameIndex { get; set; }
            [JsonProperty("dx", Required = Required.Always)] public float Dx { get; set; }
            [JsonProperty("dy", Required = Required.Always)] public float Dy { get; set; }
            [JsonProperty("sx", Required = Required.Always)] public float ScaleX { get; set; }
            [JsonProperty("sy", Required = Required.Always)] public float ScaleY { get; set; }
            [JsonProperty("alpha", Required = Required.Always)] public float Alpha { get; set; }
        }
    }
}
