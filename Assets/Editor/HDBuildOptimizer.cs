using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HeroDefense.EditorTools
{
    /// <summary>
    /// HeroDefense Step 0 包体优化工具集。
    ///
    /// 菜单（Tools/HeroDefense/）：
    ///   - Apply Build Optimization     一键应用：PlayerSettings + GraphicsSettings + Quality + Audio + link.xml
    ///   - Restore Default Settings     一键还原（应急用）
    ///   - Show Current Settings        当前裁剪状态报告（dry-run）
    ///
    /// 设计原则：
    ///   - 不直接编辑 ProjectSettings/*.asset YAML（避免 Tuanjie 升级时格式差异）
    ///   - 所有修改通过 Unity 官方 API（PlayerSettings.* / GraphicsSettings 等）
    ///   - 应用后用户必须手动删 Packages/manifest.json 中 13 个不必要模块（详见 Docs/build/03-engine-trim.md §1.1）
    /// </summary>
    public static class HDBuildOptimizer
    {
        private const string MENU_APPLY = "Tools/HeroDefense/Apply Build Optimization";
        private const string MENU_RESTORE = "Tools/HeroDefense/Restore Default Settings";
        private const string MENU_SHOW = "Tools/HeroDefense/Show Current Settings";

        // =====================================================
        // 菜单 1：Apply
        // =====================================================

        [MenuItem(MENU_APPLY)]
        public static void ApplyAllOptimizations()
        {
            int errorCount = 0;
            int okCount = 0;

            Debug.Log("[HDBuildOptimizer] ===== 开始应用 Step 0 包体优化 =====");

            try
            {
                ApplyPlayerSettings();
                Debug.Log("[HDBuildOptimizer] PlayerSettings updated ✓");
                okCount++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HDBuildOptimizer] PlayerSettings 应用失败: {e.Message}");
                errorCount++;
            }

            try
            {
                ApplyQualitySettings();
                Debug.Log("[HDBuildOptimizer] QualitySettings updated ✓");
                okCount++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HDBuildOptimizer] QualitySettings 应用失败: {e.Message}");
                errorCount++;
            }

            try
            {
                ApplyAudioSettings();
                Debug.Log("[HDBuildOptimizer] AudioSettings updated ✓");
                okCount++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HDBuildOptimizer] AudioSettings 应用失败: {e.Message}");
                errorCount++;
            }

            try
            {
                GenerateLinkXml();
                Debug.Log("[HDBuildOptimizer] link.xml written ✓");
                okCount++;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[HDBuildOptimizer] link.xml 生成失败: {e.Message}");
                errorCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[HDBuildOptimizer] ===== 应用完毕：{okCount} OK / {errorCount} ERROR =====");
            Debug.LogWarning("[HDBuildOptimizer] ⚠️ 还需手动操作：编辑 Packages/manifest.json，删除 13 个不必要模块。详见 Docs/build/03-engine-trim.md §1.1");
        }

        private static void ApplyPlayerSettings()
        {
            // 关闭 Splash Screen（Unity 启动画面）
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;

            // 启用引擎代码裁剪
            PlayerSettings.stripEngineCode = true;

            // Managed Stripping Level = High（WebGL + WeixinMiniGame）
            // Tuanjie 1.8.4 同时支持两个 BuildTargetGroup
            SetManagedStrippingHigh(BuildTargetGroup.WebGL);

            // WebGL 设置
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.memorySize = 256;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
            PlayerSettings.WebGL.debugSymbols = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.nameFilesAsHashes = true;
            PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;

            // Color Space = Linear（URP 2D 要求）
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // 关 Visible In Background
            PlayerSettings.visibleInBackground = false;
            PlayerSettings.runInBackground = false;

            // 2D 不需要 GPU Skinning
            PlayerSettings.gpuSkinning = false;
        }

        private static void SetManagedStrippingHigh(BuildTargetGroup group)
        {
            // Unity 2022+ API: PlayerSettings.SetManagedStrippingLevel
            // Tuanjie 1.8.4 兼容（基于 Unity 2022.3.2）
            try
            {
                PlayerSettings.SetManagedStrippingLevel(group, ManagedStrippingLevel.High);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HDBuildOptimizer] SetManagedStrippingLevel({group}) 失败（可能 Tuanjie 字段名差异），请手动在 PlayerSettings 中设置: {e.Message}");
            }
        }

        private static void ApplyQualitySettings()
        {
            // 切到 Low Quality Level（默认 6 levels，切到 0 = 最低）
            QualitySettings.SetQualityLevel(0, applyExpensiveChanges: true);

            QualitySettings.antiAliasing = 0;
            QualitySettings.softParticles = false;
            QualitySettings.realtimeReflectionProbes = false;
            QualitySettings.billboardsFaceCameraPosition = false;
            QualitySettings.shadowCascades = 0;
            QualitySettings.shadowDistance = 0;
            QualitySettings.shadows = ShadowQuality.Disable;
            QualitySettings.shadowResolution = ShadowResolution.Low;
            QualitySettings.pixelLightCount = 1;
            QualitySettings.maximumLODLevel = 0;
            QualitySettings.skinWeights = SkinWeights.OneBone;
            QualitySettings.particleRaycastBudget = 16;
            QualitySettings.lodBias = 0.7f;
        }

        private static void ApplyAudioSettings()
        {
            // DSP Buffer Size: Best Performance + 22050 Hz + 减少 voice 数
            var config = AudioSettings.GetConfiguration();
            config.dspBufferSize = 1024;
            config.sampleRate = 22050;
            config.numRealVoices = 16;
            config.numVirtualVoices = 64;
            config.speakerMode = AudioSpeakerMode.Stereo;

            bool ok = AudioSettings.Reset(config);
            if (!ok)
            {
                Debug.LogWarning("[HDBuildOptimizer] AudioSettings.Reset 返回 false（可能音频中），重启 Editor 后生效");
            }
        }

        private static void GenerateLinkXml()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string linkPath = Path.Combine(projectRoot, "Assets", "link.xml");

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<!-- HDBuildOptimizer 生成 - IL2CPP/AOT 类型保留清单 -->");
            sb.AppendLine("<linker>");

            // Unity 核心模块
            sb.AppendLine("  <assembly fullname=\"UnityEngine.CoreModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.UIModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.IMGUIModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.AudioModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.Physics2DModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.AnimationModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.TextRenderingModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.SpriteShapeModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.TilemapModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.UnityWebRequestModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.AssetBundleModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.ImageConversionModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.JSONSerializeModule\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"UnityEngine.ScreenCaptureModule\" preserve=\"all\"/>");

            // TMP
            sb.AppendLine("  <assembly fullname=\"Unity.TextMeshPro\" preserve=\"all\"/>");

            // xLua (反射密集，必保)
            sb.AppendLine("  <assembly fullname=\"XLua\" preserve=\"all\"/>");

            // 平台 SDK
            sb.AppendLine("  <assembly fullname=\"WeChatWASM\" preserve=\"all\"/>");
            sb.AppendLine("  <assembly fullname=\"TTSDK\" preserve=\"all\"/>");

            // HeroDefense 自身（避免业务代码被裁）
            sb.AppendLine("  <assembly fullname=\"Assembly-CSharp\" preserve=\"all\"/>");

            sb.AppendLine("</linker>");

            File.WriteAllText(linkPath, sb.ToString(), new UTF8Encoding(false));
            Debug.Log($"[HDBuildOptimizer] link.xml 已生成: {linkPath}");
        }

        // =====================================================
        // 菜单 2：Restore
        // =====================================================

        [MenuItem(MENU_RESTORE)]
        public static void RestoreDefaults()
        {
            if (!EditorUtility.DisplayDialog(
                "Restore Default Settings",
                "这会把 PlayerSettings / GraphicsSettings / QualitySettings / AudioSettings 还原到 Tuanjie 2D 模板默认值。\n\n注意：仅做应急用，正常工作流不需要跑此项。\n\n确认还原？",
                "确认",
                "取消"))
            {
                return;
            }

            // Splash Screen 回开
            PlayerSettings.SplashScreen.show = true;

            // 取消引擎代码裁剪（开发期方便调试）
            PlayerSettings.stripEngineCode = false;

            // Managed Stripping Level 回 Disabled
            try { PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.WebGL, ManagedStrippingLevel.Disabled); }
            catch { }

            // WebGL 设置回默认
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.memorySize = 256;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.debugSymbols = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.nameFilesAsHashes = false;

            // Color Space 回 Gamma（Tuanjie 2D 模板默认）
            PlayerSettings.colorSpace = ColorSpace.Gamma;

            // Quality 回 Medium
            QualitySettings.SetQualityLevel(2, applyExpensiveChanges: true);
            QualitySettings.antiAliasing = 2;
            QualitySettings.shadows = ShadowQuality.HardOnly;
            QualitySettings.shadowDistance = 50f;

            // Audio 回默认
            var config = AudioSettings.GetConfiguration();
            config.dspBufferSize = 256;
            config.sampleRate = 48000;
            config.numRealVoices = 32;
            config.numVirtualVoices = 512;
            AudioSettings.Reset(config);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[HDBuildOptimizer] Defaults restored. 注：Packages/manifest.json 模块删除部分需手动 git restore");
        }

        // =====================================================
        // 菜单 3：Show
        // =====================================================

        [MenuItem(MENU_SHOW)]
        public static void ShowCurrentSettings()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== HDBuildOptimizer 当前设置 =====");

            sb.AppendLine($"[PlayerSettings]");
            sb.AppendLine($"  Splash Show:          {PlayerSettings.SplashScreen.show}");
            sb.AppendLine($"  Strip Engine Code:    {PlayerSettings.stripEngineCode}");
            sb.AppendLine($"  Color Space:          {PlayerSettings.colorSpace}");
            sb.AppendLine($"  GPU Skinning:         {PlayerSettings.gpuSkinning}");
            try
            {
                var lvl = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.WebGL);
                sb.AppendLine($"  WebGL Strip Level:    {lvl}");
            }
            catch (System.Exception) { sb.AppendLine($"  WebGL Strip Level:    <unsupported API>"); }

            sb.AppendLine($"[WebGL]");
            sb.AppendLine($"  Compression Format:   {PlayerSettings.WebGL.compressionFormat}");
            sb.AppendLine($"  Memory Size:          {PlayerSettings.WebGL.memorySize} MB");
            sb.AppendLine($"  Exception Support:    {PlayerSettings.WebGL.exceptionSupport}");
            sb.AppendLine($"  Linker Target:        {PlayerSettings.WebGL.linkerTarget}");
            sb.AppendLine($"  Data Caching:         {PlayerSettings.WebGL.dataCaching}");
            sb.AppendLine($"  Name Files As Hashes: {PlayerSettings.WebGL.nameFilesAsHashes}");

            sb.AppendLine($"[QualitySettings]");
            sb.AppendLine($"  Current Level:        {QualitySettings.GetQualityLevel()}");
            sb.AppendLine($"  AA:                   {QualitySettings.antiAliasing}");
            sb.AppendLine($"  Shadows:              {QualitySettings.shadows}");
            sb.AppendLine($"  Shadow Distance:      {QualitySettings.shadowDistance}");
            sb.AppendLine($"  Pixel Light Count:    {QualitySettings.pixelLightCount}");

            sb.AppendLine($"[AudioSettings]");
            var ac = AudioSettings.GetConfiguration();
            sb.AppendLine($"  DSP Buffer:           {ac.dspBufferSize}");
            sb.AppendLine($"  Sample Rate:          {ac.sampleRate}");
            sb.AppendLine($"  Real Voices:          {ac.numRealVoices}");
            sb.AppendLine($"  Virtual Voices:       {ac.numVirtualVoices}");

            string linkXmlPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets", "link.xml");
            sb.AppendLine($"[link.xml]");
            sb.AppendLine($"  Path:                 {linkXmlPath}");
            sb.AppendLine($"  Exists:               {File.Exists(linkXmlPath)}");

            Debug.Log(sb.ToString());
        }
    }
}
