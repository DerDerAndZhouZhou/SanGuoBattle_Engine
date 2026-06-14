using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace HeroDefense.Engine.Host
{
    /// <summary>
    /// CDN 热更协程：比对远端 / 本地 manifest，拉取 diff，写入 persistentDataPath/Game/。
    /// API 最小实现：单线程顺序下载，不做并发，不做重试。超时用 UnityWebRequest 默认。
    ///
    /// 使用：
    ///   StartCoroutine(HotUpdateHost.CheckAndDownload("https://cdn.example.com/herodefense", (ok, msg) => {
    ///       if (ok) Debug.Log("新版本: " + msg);
    ///       else    Debug.LogError("热更失败: " + msg);
    ///   }));
    ///
    /// 原子化策略：
    ///   所有文件先下到 persistentDataPath/Game.new/，全部成功后执行"覆盖式合并"到 Game/；
    ///   最后把远端 manifest 写到 Game/manifest.json 作为新基线。
    ///   若中途任何文件失败，Game.new/ 被删除，Game/ 保持不动。
    /// </summary>
    public static class HotUpdateHost
    {
        private const string MANIFEST_NAME = "manifest.json";

        /// <summary>本地基线目录（热更写入目标）。</summary>
        public static string LocalGameRoot => Path.Combine(Application.persistentDataPath, "Game");

        /// <summary>暂存目录（原子化更新用）。</summary>
        public static string StagingRoot => Path.Combine(Application.persistentDataPath, "Game.new");

        /// <param name="cdnBaseUrl">CDN 根 URL，不带尾斜杠。例：https://cdn.example.com/herodefense</param>
        /// <param name="onComplete">完成回调。success=true 时 msg=版本号；success=false 时 msg=错误原因</param>
        public static IEnumerator CheckAndDownload(string cdnBaseUrl, Action<bool, string> onComplete)
        {
            onComplete = onComplete ?? ((_, __) => { });
            if (string.IsNullOrEmpty(cdnBaseUrl))
            {
                onComplete(false, "cdnBaseUrl 为空");
                yield break;
            }
            cdnBaseUrl = cdnBaseUrl.TrimEnd('/');

            // 1. 拉远端 manifest
            Debug.Log($"[HotUpdate] 请求远端 manifest: {cdnBaseUrl}/{MANIFEST_NAME}");
            string remoteManifestText = null;
            yield return GetString(cdnBaseUrl + "/" + MANIFEST_NAME, (ok, text, err) =>
            {
                if (!ok) { Debug.LogError($"[HotUpdate] 拉取远端 manifest 失败: {err}"); }
                else { remoteManifestText = text; }
            });
            if (remoteManifestText == null)
            {
                onComplete(false, "远端 manifest 拉取失败");
                yield break;
            }

            // 2. 解析远端 / 本地 manifest
            string remoteVersion;
            var remoteMap = ParseManifest(remoteManifestText, out remoteVersion);
            if (remoteMap.Count == 0)
            {
                onComplete(false, "远端 manifest 为空或解析失败");
                yield break;
            }
            Debug.Log($"[HotUpdate] 远端 manifest 版本 {remoteVersion}, 条目 {remoteMap.Count}");

            var localMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string localManifestPath = Path.Combine(LocalGameRoot, MANIFEST_NAME);
            if (File.Exists(localManifestPath))
            {
                string localText = File.ReadAllText(localManifestPath);
                string lv;
                localMap = ParseManifest(localText, out lv);
                Debug.Log($"[HotUpdate] 本地 manifest 版本 {lv}, 条目 {localMap.Count}");
            }
            else
            {
                Debug.Log("[HotUpdate] 本地 manifest 不存在，首次全量下载");
            }

            // 3. 计算 diff
            var diff = new List<string>();
            foreach (var kv in remoteMap)
            {
                if (!localMap.TryGetValue(kv.Key, out string localHash) || localHash != kv.Value)
                    diff.Add(kv.Key);
            }
            Debug.Log($"[HotUpdate] 需要更新 {diff.Count} 个文件");

            // 4. 下载到 staging
            if (Directory.Exists(StagingRoot))
            {
                try { Directory.Delete(StagingRoot, true); } catch { }
            }
            if (!Directory.Exists(StagingRoot)) Directory.CreateDirectory(StagingRoot);

            int i = 0;
            foreach (var relPath in diff)
            {
                i++;
                string url = cdnBaseUrl + "/" + relPath;
                byte[] bytes = null;
                string err = null;
                // 重试 3 次（2026-06-07）：隧道/弱网半路丢包很常见；原子下载"一个失败就全回滚"，
                // 必须靠重试兜住瞬时失败，否则 960/355 文件里随便一个超时整包就废 → 真机没资源。
                for (int attempt = 1; attempt <= 3 && bytes == null; attempt++)
                {
                    err = null;
                    yield return GetBytes(url, (ok, data, e) =>
                    {
                        if (!ok) err = e;
                        else bytes = data;
                    });
                    if (bytes == null && attempt < 3)
                    {
                        Debug.LogWarning($"[HotUpdate] 重试 {attempt}/3 [{relPath}]: {err}");
                        for (int w = 0; w < 30; w++) yield return null;   // ~0.5s 退避（WebGL 无 Thread.Sleep）
                    }
                }
                if (bytes == null)
                {
                    try { if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true); } catch { }
                    onComplete(false, $"下载失败(重试3次) [{relPath}]: {err}");
                    yield break;
                }
                string dst = Path.Combine(StagingRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                string dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
                try { File.WriteAllBytes(dst, bytes); }
                catch (Exception we)
                {
                    try { if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true); } catch { }
                    onComplete(false, $"写文件失败 [{relPath}]: {we.Message}");
                    yield break;
                }
                if (i % 10 == 0 || i == diff.Count)
                    Debug.Log($"[HotUpdate] 进度 {i}/{diff.Count}");
            }

            // 5. 合并 staging → LocalGameRoot
            try
            {
                if (!Directory.Exists(LocalGameRoot)) Directory.CreateDirectory(LocalGameRoot);
                MergeDirectory(StagingRoot, LocalGameRoot);
            }
            catch (Exception me)
            {
                onComplete(false, $"合并 staging 失败: {me.Message}");
                yield break;
            }
            finally
            {
                try { if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true); } catch { }
            }

            // 6. 把远端 manifest 落盘作为新基线
            try { File.WriteAllText(localManifestPath, remoteManifestText, new UTF8Encoding(false)); }
            catch (Exception we)
            {
                onComplete(false, $"写 manifest 失败: {we.Message}");
                yield break;
            }

            Debug.Log($"[HotUpdate] 完成，版本 {remoteVersion}，更新 {diff.Count} 个文件");
            onComplete(true, remoteVersion);
        }

        // ------------------------------------------------------------------
        // 内部：HTTP 请求
        // ------------------------------------------------------------------

        private static IEnumerator GetString(string url, Action<bool, string, string> done)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 30;
                yield return req.SendWebRequest();
#if UNITY_2020_2_OR_NEWER
                bool isError = req.result != UnityWebRequest.Result.Success;
#else
                bool isError = req.isHttpError || req.isNetworkError;
#endif
                if (isError) done(false, null, req.error);
                else done(true, req.downloadHandler.text, null);
            }
        }

        private static IEnumerator GetBytes(string url, Action<bool, byte[], string> done)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 30;
                yield return req.SendWebRequest();
#if UNITY_2020_2_OR_NEWER
                bool isError = req.result != UnityWebRequest.Result.Success;
#else
                bool isError = req.isHttpError || req.isNetworkError;
#endif
                if (isError) done(false, null, req.error);
                else done(true, req.downloadHandler.data, null);
            }
        }

        // ------------------------------------------------------------------
        // 内部：manifest 解析（与 ResourceHost 的简易 parser 同源思路，此处自带以避免耦合）
        // ------------------------------------------------------------------

        public static Dictionary<string, string> ParseManifest(string json, out string version)
        {
            version = "";
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return dict;

            version = ExtractTopString(json, "version") ?? "";

            int i = 0;
            while (i < json.Length)
            {
                int braceOpen = json.IndexOf('{', i);
                if (braceOpen < 0) break;
                if (braceOpen == 0) { i = braceOpen + 1; continue; }
                int braceClose = json.IndexOf('}', braceOpen + 1);
                if (braceClose < 0) break;
                string segment = json.Substring(braceOpen + 1, braceClose - braceOpen - 1);
                string path = ExtractTopString(segment, "path");
                string hash = ExtractTopString(segment, "hash");
                if (!string.IsNullOrEmpty(path))
                    dict[path.Replace('\\', '/')] = hash ?? "";
                i = braceClose + 1;
            }
            return dict;
        }

        private static string ExtractTopString(string segment, string key)
        {
            string needle = "\"" + key + "\"";
            int k = segment.IndexOf(needle, StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = segment.IndexOf(':', k + needle.Length);
            if (colon < 0) return null;
            int q1 = segment.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = segment.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return segment.Substring(q1 + 1, q2 - q1 - 1);
        }

        // ------------------------------------------------------------------
        // 内部：目录合并
        // ------------------------------------------------------------------

        private static void MergeDirectory(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            if (!Directory.Exists(dst)) Directory.CreateDirectory(dst);
            foreach (var subDir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            {
                string rel = subDir.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                string target = Path.Combine(dst, rel);
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);
            }
            foreach (var file in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                string target = Path.Combine(dst, rel);
                string targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                File.Copy(file, target, true);
            }
        }
    }
}
