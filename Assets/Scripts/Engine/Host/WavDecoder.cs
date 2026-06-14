using System;
using UnityEngine;

namespace HeroDefense.Engine.Host
{
    /// <summary>
    /// 简易 WAV 解码器：从字节流解出 AudioClip。
    /// 只支持 PCM 16-bit（mono / stereo）。
    /// 不支持 MP3 / OGG / 24bit / float — 这些走 UnityWebRequestMultimedia 异步路径。
    /// </summary>
    public static class WavDecoder
    {
        /// <param name="bytes">完整 WAV 文件字节</param>
        /// <param name="name">用于 AudioClip.name（调试）</param>
        public static AudioClip Decode(byte[] bytes, string name = "wav_clip")
        {
            if (bytes == null || bytes.Length < 44)
            {
                Debug.LogWarning($"[WavDecoder] 字节流过短或为空: {name}");
                return null;
            }

            // RIFF header 校验
            if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
                bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
            {
                Debug.LogWarning($"[WavDecoder] 非 RIFF/WAVE 文件: {name}");
                return null;
            }

            // 解析 fmt chunk（在 RIFF 之后第一个 chunk）
            int fmtOffset = FindChunk(bytes, "fmt ");
            if (fmtOffset < 0) { Debug.LogWarning($"[WavDecoder] 找不到 fmt chunk: {name}"); return null; }

            int audioFormat = BitConverter.ToInt16(bytes, fmtOffset + 8);
            int channels    = BitConverter.ToInt16(bytes, fmtOffset + 10);
            int sampleRate  = BitConverter.ToInt32(bytes, fmtOffset + 12);
            int bitsPerSample = BitConverter.ToInt16(bytes, fmtOffset + 22);

            if (audioFormat != 1)
            {
                Debug.LogWarning($"[WavDecoder] 仅支持 PCM (audioFormat=1)，文件 {name} 是 {audioFormat}");
                return null;
            }
            if (bitsPerSample != 16)
            {
                Debug.LogWarning($"[WavDecoder] 仅支持 16-bit PCM，文件 {name} 是 {bitsPerSample}-bit");
                return null;
            }

            // data chunk
            int dataOffset = FindChunk(bytes, "data");
            if (dataOffset < 0) { Debug.LogWarning($"[WavDecoder] 找不到 data chunk: {name}"); return null; }

            int dataSize = BitConverter.ToInt32(bytes, dataOffset + 4);
            int sampleCount = dataSize / 2; // 16-bit = 2 bytes per sample
            int frameCount  = sampleCount / channels;

            // 16-bit PCM → float[-1, 1]
            float[] samples = new float[sampleCount];
            int srcOffset = dataOffset + 8;
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(bytes, srcOffset + i * 2);
                samples[i] = s / 32768f;
            }

            var clip = AudioClip.Create(name, frameCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static int FindChunk(byte[] bytes, string fourCC)
        {
            // 从 RIFF header 之后（offset 12）开始扫
            int offset = 12;
            while (offset < bytes.Length - 8)
            {
                if (bytes[offset] == fourCC[0] && bytes[offset+1] == fourCC[1] &&
                    bytes[offset+2] == fourCC[2] && bytes[offset+3] == fourCC[3])
                {
                    return offset;
                }
                int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
                offset += 8 + chunkSize;
                if (chunkSize <= 0) break;
            }
            return -1;
        }
    }
}
