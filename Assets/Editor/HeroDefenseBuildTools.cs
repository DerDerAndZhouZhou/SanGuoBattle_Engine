using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using HeroDefense.Engine.Host;

namespace HeroDefense.EditorTools
{
    /// <summary>
    /// HeroDefense 构建工具集合。
    /// 菜单：
    ///   - Tools/HeroDefense/Regenerate Manifest       生成 Game/manifest.json
    ///   - Tools/HeroDefense/Build Windows             Standalone Windows64 构建
    ///   - Tools/HeroDefense/Build WebGL               WebGL 构建
    ///   - Tools/HeroDefense/Self-Check Resource_LoadSprite   Play 模式自检 Lua 桥接
    ///
    /// 关键约束：Game/ 下的 lua|config|art 不会被拷进 StreamingAssets，由用户手动上传 CDN。
    /// </summary>
    public static class HeroDefenseBuildTools
    {
        private const string MENU_REGEN_MANIFEST = "Tools/HeroDefense/Regenerate Manifest";
        private const string MENU_BUILD_WINDOWS = "Tools/HeroDefense/Build Windows";
        private const string MENU_BUILD_WEBGL = "Tools/HeroDefense/Build WebGL";
        private const string MENU_BUILD_WEBGL_PHASE1 = "Tools/HeroDefense/Build WebGL (Phase 1 Main)";
        private const string MENU_MEASURE_MAIN_PACKAGE = "Tools/HeroDefense/Measure Main Package";
        private const string MENU_PROFILE_FIRST_FRAME = "Tools/HeroDefense/Profile First Frame";
        private const string MENU_SELFCHECK_LOADSPRITE = "Tools/HeroDefense/Self-Check Resource_LoadSprite";
        private const int PC_WINDOW_WIDTH = 720;
        private const int PC_WINDOW_HEIGHT = 1280;

        private const string PHASE1_OUTPUT_DIR = "Build/Phase1";

        // 五层重组 v3（2026-06-17）：scripts(框架) / modules(业务) / ui(界面) / settings(配置) / resources(资源·art+audio)。
        // 各层 modulelist.xml + config.xml 在对应目录下，由递归扫描自动包含；不再需要 root 级附加。
        private static readonly string[] ScanSubDirs = { "scripts", "modules", "settings", "resources", "ui" };
        private static readonly string[] ScanExtraFiles = { };

        // ====================================================================
        // 菜单 1：生成 manifest.json
        // ====================================================================

        [MenuItem(MENU_REGEN_MANIFEST)]
        public static void RegenerateManifestMenu()
        {
            string gameRoot = GetGameRoot();
            if (!Directory.Exists(gameRoot))
            {
                Debug.LogError($"[HeroDefenseBuildTools] Game 根目录不存在: {gameRoot}");
                return;
            }

            int count = RegenerateManifest(gameRoot);
            Debug.Log($"[HeroDefenseBuildTools] Manifest 已生成: {Path.Combine(gameRoot, "manifest.json")}（{count} 个文件）");
        }

        /// <summary>
        /// 扫描 gameRoot 下的 scripts/ modules/ ui/ settings/ resources/ 以及各层 modulelist.xml，
        /// 对每个文件计算 MD5 + size，写入 gameRoot/manifest.json。返回收录文件数量。
        /// </summary>
        public static int RegenerateManifest(string gameRoot)
        {
            gameRoot = Path.GetFullPath(gameRoot);
            var entries = new List<ManifestEntry>();
            var scopeExcluded = BuildScopeExclusions(gameRoot);   // 作用域过滤集（五层重组 v3）

            foreach (var sub in ScanSubDirs)
            {
                string subFull = Path.Combine(gameRoot, sub);
                if (!Directory.Exists(subFull)) continue;

                foreach (var file in Directory.GetFiles(subFull, "*.*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(".")
                        || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".desc", StringComparison.OrdinalIgnoreCase)) continue;   // .desc = 配置表说明文档，仅工程本地，不打进包/CDN

                    string rel = MakeRelative(gameRoot, file);
                    if (string.IsNullOrEmpty(rel)) continue;
                    if (scopeExcluded.Contains(rel)) continue;   // 非 ActiveScope 的脚本不打进包（五层重组 v3）
                    entries.Add(BuildEntry(file, rel));
                }
            }

            foreach (var extra in ScanExtraFiles)
            {
                string full = Path.Combine(gameRoot, extra);
                if (!File.Exists(full)) continue;
                entries.Add(BuildEntry(full, extra.Replace('\\', '/')));
            }

            entries.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.Ordinal));

            DateTime now = DateTime.Now;
            string version = now.ToString("yyyyMMdd-HHmmss");
            string generatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss");
            string json = BuildManifestJson(version, generatedAt, entries);

            string manifestPath = Path.Combine(gameRoot, "manifest.json");
            File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
            return entries.Count;
        }

        /// <summary>
        /// 五层重组 v3：扫 ScanSubDirs 下所有 config.xml，收集"仅属非 ActiveScope 作用域"的 Script File →
        /// 这些脚本在当前作用域打包时排除出 manifest（不上 CDN / 不进包）。同时出现在 ActiveScope 段的共享脚本不排除。
        /// 当前仅 GameClient（scripts/config.xml 全列 GameClient）→ 排除集为空 → 行为同旧（全收）。
        /// </summary>
        private static HashSet<string> BuildScopeExclusions(string gameRoot)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in ScanSubDirs)
            {
                string subFull = Path.Combine(gameRoot, sub);
                if (!Directory.Exists(subFull)) continue;
                foreach (var cfg in Directory.GetFiles(subFull, "config.xml", SearchOption.AllDirectories))
                {
                    try
                    {
                        var doc = XDocument.Load(cfg);
                        if (doc.Root == null) continue;
                        foreach (var scopeElem in doc.Root.Elements())
                        {
                            bool isActive = string.Equals(scopeElem.Name.LocalName, LuaHost.ActiveScope, StringComparison.OrdinalIgnoreCase);
                            foreach (var s in scopeElem.Elements("Script"))
                            {
                                var file = s.Attribute("File")?.Value;
                                if (string.IsNullOrEmpty(file)) continue;
                                file = file.Trim().Replace('\\', '/');
                                if (isActive) active.Add(file); else excluded.Add(file);
                            }
                        }
                    }
                    catch (Exception e) { Debug.LogWarning($"[Manifest] config.xml 解析失败({cfg}): {e.Message}"); }
                }
            }
            excluded.ExceptWith(active);   // 共享脚本（也在 active 段）不排除
            if (excluded.Count > 0)
                Debug.Log($"[Manifest] 作用域过滤：ActiveScope={LuaHost.ActiveScope}，排除 {excluded.Count} 个非本作用域脚本");
            return excluded;
        }

        private static ManifestEntry BuildEntry(string fullPath, string relPath)
        {
            var info = new FileInfo(fullPath);
            return new ManifestEntry
            {
                path = relPath,
                hash = ComputeMd5(fullPath),
                size = info.Length
            };
        }

        private static string ComputeMd5(string filePath)
        {
            using (var md5 = MD5.Create())
            // FileShare.ReadWrite：WPS/Excel 开着配置表时也能算 MD5（同 ResourceHost 容错，见 CLAUDE.md §10）
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var bytes = md5.ComputeHash(stream);
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string MakeRelative(string rootFullPath, string fullPath)
        {
            string rootNorm = rootFullPath.Replace('\\', '/').TrimEnd('/');
            string fullNorm = Path.GetFullPath(fullPath).Replace('\\', '/');
            if (!fullNorm.StartsWith(rootNorm + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return fullNorm.Substring(rootNorm.Length + 1);
        }

        private static string BuildManifestJson(string version, string generatedAt, List<ManifestEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append('{').Append('\n');
            sb.Append("  \"version\": \"").Append(EscapeJson(version)).Append("\",\n");
            sb.Append("  \"generated_at\": \"").Append(EscapeJson(generatedAt)).Append("\",\n");
            sb.Append("  \"files\": [");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append('\n').Append("    {\"path\": \"").Append(EscapeJson(e.path))
                  .Append("\", \"hash\": \"").Append(EscapeJson(e.hash))
                  .Append("\", \"size\": ").Append(e.size).Append('}');
                if (i < entries.Count - 1) sb.Append(',');
            }
            if (entries.Count > 0) sb.Append('\n').Append("  ");
            sb.Append("]\n").Append('}').Append('\n');
            return sb.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        [Serializable]
        private struct ManifestEntry
        {
            public string path;
            public string hash;
            public long size;
        }

        // ====================================================================
        // 菜单 2 / 3：打包
        // ====================================================================

        [MenuItem(MENU_BUILD_WINDOWS)]
        public static void BuildWindowsMenu()
        {
            string gameRoot = GetGameRoot();
            string repoRoot = GetRepoRoot();
            // PC 包热更直接读源 <repo>/Game/，先重生 manifest 保证与源同步
            RegenerateManifest(gameRoot);

            // PC 内测窗口使用手机竖屏体验基准。
            ApplyPortraitPlayerSettings();

            // 仓库目录约定（用户 2026-05-21）：
            //   <repo>/Build/Windows/     ← PC 编译产物（仅引擎，无 Game 副本）
            //   <repo>/Build/Wechat/      ← 微信小游戏（读 CDN）
            //   <repo>/Build/ByteGame/    ← 抖音小游戏（读 CDN）
            //   <repo>/Game/              ← 资源库（所有版本共享）
            // PC 运行时 ResourceHost.Boot 从 exe 目录向上 2 级找到 <repo>/Game/settings/Enum.tab → _baseDir=<repo>/Game。
            // 测试 / 美术改 <repo>/Game/* 直接影响下一次启动，无需重打包。
            string outDir = Path.Combine(repoRoot, "Build", "Windows");
            string outExe = Path.Combine(outDir, "HeroDefense.exe");
            EnsureDir(outDir);

            // Development Build：开启 stack trace + console log + 允许 Debug.Log 详细输出
            var options = new BuildPlayerOptions
            {
                scenes = GetEnabledScenePaths(),
                locationPathName = outExe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development | BuildOptions.AllowDebugging
            };

            Debug.Log($"[HeroDefenseBuildTools] 开始 Standalone Windows64 Development 构建 → {outExe}");
            var report = BuildPipeline.BuildPlayer(options);
            LogBuildResult(report, outDir);

            // 构建成功 → 仅追加 2 件运行时附件（launcher.bat + README.txt）；Game/ 共享源仓库，不拷
            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                try
                {
                    WriteLauncherBat(outDir);
                    WriteTesterReadme(outDir);
                    Debug.Log($"[HeroDefenseBuildTools] ✅ PC 内测包就绪：{outDir}\n  双击 start_with_log.bat 启动游戏 + 实时日志窗口\n  改 <repo>/Game/ 即热更（关游戏 → 重启）");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[HeroDefenseBuildTools] 启动器附件写入失败：{e.Message}");
                }
            }
        }

        // PC 内测窗口尺寸：720×1280（9:16 竖屏）。WebGL 画布另由 defaultScreenWidthWeb/defaultScreenHeightWeb 控制。
        // 背景资源仍可保持 1920×1280 美术画布，运行时按相机/Canvas 适配显示。
        // resizable / allowFullscreenSwitch 只在 Standalone 生效，WebGL 由 canvas / index.html 控制
        private static void ApplyPortraitPlayerSettings()
        {
            PlayerSettings.defaultScreenWidth = PC_WINDOW_WIDTH;
            PlayerSettings.defaultScreenHeight = PC_WINDOW_HEIGHT;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.allowFullscreenSwitch = true;
            Debug.Log($"[HeroDefenseBuildTools] PlayerSettings: {PC_WINDOW_WIDTH}x{PC_WINDOW_HEIGHT} (PC窗口，9:16竖屏)");
        }

        // 启动器：拉起 PowerShell tail 窗 + exe，日志走 %TEMP%\HeroDefense_output.log
        // 网络共享盘场景：所有同事都从共享路径跑同一个 exe，日志若写共享盘会被互相覆盖
        // → 每人写到自己的 %TEMP% 目录（C:\Users\<user>\AppData\Local\Temp\HeroDefense_output.log）
        //
        // 用户 2026-05-21 加需求：关闭 log 窗口同时关闭游戏
        // 方案：PS 进程通过 Start-Process -PassThru 持有游戏进程引用，try/Get-Content 阻塞 tail，
        //       finally 块在 PS 退出时强杀游戏。X 关 PS 窗 → Windows 给 CTRL_CLOSE_EVENT，PS 有 5s
        //       窗口跑 finally，足够执行 Stop-Process -Force。
        //       拆成 .bat + _run.ps1 两文件以避开多行 PS 转义；ps1 干净直接读
        private static void WriteLauncherBat(string outDir)
        {
            // --- start_with_log.bat：仅一行 powershell 调用 ---
            string batPath = Path.Combine(outDir, "start_with_log.bat");
            string batContent =
                "@echo off\r\n" +
                "setlocal\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "start \"\" powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -File \"%~dp0_run.ps1\"\r\n" +
                "endlocal\r\n";
            File.WriteAllText(batPath, batContent, new UTF8Encoding(false));

            // --- _run.ps1：实际启动逻辑 + 关窗杀游戏 ---
            string psPath = Path.Combine(outDir, "_run.ps1");
            string psContent =
                "# HeroDefense PC 内测启动脚本\r\n" +
                "# 关闭本窗口 = 同时关闭游戏（Windows Job Object KILL_ON_JOB_CLOSE 内核级保证）\r\n" +
                "\r\n" +
                "$Host.UI.RawUI.WindowTitle = \"HeroDefense Log ($env:USERNAME) - 关本窗口同时退出游戏\"\r\n" +
                "\r\n" +
                "$exe = Join-Path $PSScriptRoot 'HeroDefense.exe'\r\n" +
                "$log = Join-Path $env:TEMP 'HeroDefense_output.log'\r\n" +
                "\r\n" +
                "# 清空旧日志\r\n" +
                "Set-Content -Path $log -Value '' -Encoding UTF8\r\n" +
                "\r\n" +
                "# === Windows Job Object：把游戏绑到本 PS 的生命周期（内核级保证）===\r\n" +
                "# 用户 2026-05-21 反馈：try/finally 在 X 关窗时不跑（Windows 强杀 conhost → PS 无清理时间）\r\n" +
                "# 改用 Job Object + KILL_ON_JOB_CLOSE：PS 进程持有 Job 句柄，PS 任何原因死掉 → OS 自动杀光 Job 内进程\r\n" +
                "$jobApi = @'\r\n" +
                "using System;\r\n" +
                "using System.Runtime.InteropServices;\r\n" +
                "public static class JobApi {\r\n" +
                "    [DllImport(\"kernel32.dll\", CharSet=CharSet.Unicode, SetLastError=true)]\r\n" +
                "    public static extern IntPtr CreateJobObject(IntPtr a, string lpName);\r\n" +
                "    [DllImport(\"kernel32.dll\", SetLastError=true)]\r\n" +
                "    public static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);\r\n" +
                "    [DllImport(\"kernel32.dll\", SetLastError=true)]\r\n" +
                "    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);\r\n" +
                "}\r\n" +
                "[StructLayout(LayoutKind.Sequential)]\r\n" +
                "public struct JOBOBJECT_BASIC_LIMIT_INFORMATION {\r\n" +
                "    public Int64 PerProcessUserTimeLimit;\r\n" +
                "    public Int64 PerJobUserTimeLimit;\r\n" +
                "    public UInt32 LimitFlags;\r\n" +
                "    public UIntPtr MinimumWorkingSetSize;\r\n" +
                "    public UIntPtr MaximumWorkingSetSize;\r\n" +
                "    public UInt32 ActiveProcessLimit;\r\n" +
                "    public Int64 Affinity;\r\n" +
                "    public UInt32 PriorityClass;\r\n" +
                "    public UInt32 SchedulingClass;\r\n" +
                "}\r\n" +
                "[StructLayout(LayoutKind.Sequential)]\r\n" +
                "public struct IO_COUNTERS {\r\n" +
                "    public UInt64 ReadOperationCount;\r\n" +
                "    public UInt64 WriteOperationCount;\r\n" +
                "    public UInt64 OtherOperationCount;\r\n" +
                "    public UInt64 ReadTransferCount;\r\n" +
                "    public UInt64 WriteTransferCount;\r\n" +
                "    public UInt64 OtherTransferCount;\r\n" +
                "}\r\n" +
                "[StructLayout(LayoutKind.Sequential)]\r\n" +
                "public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {\r\n" +
                "    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;\r\n" +
                "    public IO_COUNTERS IoInfo;\r\n" +
                "    public UIntPtr ProcessMemoryLimit;\r\n" +
                "    public UIntPtr JobMemoryLimit;\r\n" +
                "    public UIntPtr PeakProcessMemoryUsed;\r\n" +
                "    public UIntPtr PeakJobMemoryUsed;\r\n" +
                "}\r\n" +
                "'@\r\n" +
                "\r\n" +
                "if (-not ('JobApi' -as [type])) {\r\n" +
                "    Add-Type -TypeDefinition $jobApi -Language CSharp | Out-Null\r\n" +
                "}\r\n" +
                "\r\n" +
                "$job = [JobApi]::CreateJobObject([IntPtr]::Zero, \"HeroDefenseJob_$PID\")\r\n" +
                "if ($job -eq [IntPtr]::Zero) {\r\n" +
                "    $e = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()\r\n" +
                "    Write-Host \"[Launcher] CreateJobObject FAILED Win32Error=$e\" -ForegroundColor Red\r\n" +
                "}\r\n" +
                "$info = New-Object JOBOBJECT_EXTENDED_LIMIT_INFORMATION\r\n" +
                "$info.BasicLimitInformation.LimitFlags = 0x2000   # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE\r\n" +
                "$size = [System.Runtime.InteropServices.Marshal]::SizeOf([type]'JOBOBJECT_EXTENDED_LIMIT_INFORMATION')\r\n" +
                "$ptr  = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($size)\r\n" +
                "[System.Runtime.InteropServices.Marshal]::StructureToPtr($info, $ptr, $false)\r\n" +
                "$setOk = [JobApi]::SetInformationJobObject($job, 9, $ptr, $size)   # 9 = JobObjectExtendedLimitInformation\r\n" +
                "if (-not $setOk) {\r\n" +
                "    $e = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()\r\n" +
                "    Write-Host \"[Launcher] SetInformationJobObject FAILED Win32Error=$e\" -ForegroundColor Red\r\n" +
                "}\r\n" +
                "[System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptr)\r\n" +
                "\r\n" +
                "# === 启动游戏并绑入 Job ===\r\n" +
                "# ⚠️ 不能用 Start-Process —— 它默认 UseShellExecute=true，$game.Handle 返回 0 / 无效，\r\n" +
                "#    AssignProcessToJobObject 拿到无效句柄会失败，Job 形同虚设。\r\n" +
                "#    必须用 [Diagnostics.Process]::Start($psi) + UseShellExecute=$false 才能拿到真实进程句柄。\r\n" +
                "$psi = New-Object System.Diagnostics.ProcessStartInfo\r\n" +
                "$psi.FileName = $exe\r\n" +
                "$psi.Arguments = '-logFile \"' + $log + '\"'\r\n" +
                "$psi.UseShellExecute = $false   # 关键：必须 false 才能拿到真实 Handle\r\n" +
                "$psi.CreateNoWindow = $false    # GUI 游戏不需要 console\r\n" +
                "$game = [System.Diagnostics.Process]::Start($psi)\r\n" +
                "\r\n" +
                "$gameHandle = $game.Handle\r\n" +
                "$assigned = [JobApi]::AssignProcessToJobObject($job, $gameHandle)\r\n" +
                "$lastErr = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()\r\n" +
                "\r\n" +
                "Write-Host \"[Launcher] HeroDefense.exe started (PID=$($game.Id))\" -ForegroundColor Cyan\r\n" +
                "Write-Host \"[Launcher] Game.Handle = $gameHandle\" -ForegroundColor Cyan\r\n" +
                "Write-Host \"[Launcher] Job Handle   = $job\" -ForegroundColor Cyan\r\n" +
                "Write-Host \"[Launcher] 日志: $log\" -ForegroundColor Cyan\r\n" +
                "if ($assigned) {\r\n" +
                "    Write-Host \"[Launcher] Job AssignProcess = True ✓ (关本窗即内核自动杀游戏)\" -ForegroundColor Green\r\n" +
                "} else {\r\n" +
                "    Write-Host \"[Launcher] Job AssignProcess = False ✗ Win32Error=$lastErr\" -ForegroundColor Red\r\n" +
                "    Write-Host \"[Launcher] 警告：游戏未绑入 Job，关本窗时游戏不会自动关\" -ForegroundColor Red\r\n" +
                "}\r\n" +
                "Write-Host \"[Launcher] 关本窗口 → 自动关闭游戏（Job + Watcher 双保险）\" -ForegroundColor Yellow\r\n" +
                "Write-Host \"\"\r\n" +
                "\r\n" +
                "# === 兜底 Watcher：独立隐藏 PS 进程监听我们退出 ===\r\n" +
                "# Job Object 在某些 Tuanjie 配置下可能失效；watcher 用 Wait-Process 等我们死，\r\n" +
                "# 然后强杀游戏 PID。两层保险至少一层生效。\r\n" +
                "$watcherScript = @\"\r\n" +
                "`$ourPid = $PID\r\n" +
                "`$gamePid = $($game.Id)\r\n" +
                "try { Wait-Process -Id `$ourPid -ErrorAction Stop } catch { }\r\n" +
                "Get-Process -Id `$gamePid -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
                "# 兜底再杀任何残留 HeroDefense 进程（防 Tuanjie 自启动恢复 / 多进程残留）\r\n" +
                "Get-Process -Name 'HeroDefense','TuanjieCrashHandler*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue\r\n" +
                "\"@\r\n" +
                "$watcherPath = Join-Path $env:TEMP \"HD_Watcher_$PID.ps1\"\r\n" +
                "Set-Content -Path $watcherPath -Value $watcherScript -Encoding UTF8\r\n" +
                "\r\n" +
                "$wpsi = New-Object System.Diagnostics.ProcessStartInfo\r\n" +
                "$wpsi.FileName = 'powershell.exe'\r\n" +
                "$wpsi.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"' + $watcherPath + '\"'\r\n" +
                "$wpsi.UseShellExecute = $false\r\n" +
                "$wpsi.CreateNoWindow = $true\r\n" +
                "$watcher = [System.Diagnostics.Process]::Start($wpsi)\r\n" +
                "Write-Host \"[Launcher] Watcher PID = $($watcher.Id) (隐藏 PS，我们退出后立即杀游戏)\" -ForegroundColor Cyan\r\n" +
                "Write-Host \"\"\r\n" +
                "\r\n" +
                "# 阻塞式 tail：PS 跑期间持续读日志显示\r\n" +
                "# PS 死 → ① Job KILL_ON_JOB_CLOSE 触发 + ② Watcher 检测到我们死，Stop-Process 杀游戏\r\n" +
                "Get-Content -Path $log -Wait -Tail 0\r\n";
            // ⚠️ _run.ps1 必须 UTF-8 WITH BOM：Windows PowerShell 5.1 默认按系统 codepage（中文系统=GBK）读 .ps1，
            //    无 BOM 的 UTF-8 → 中文乱码 → parser 在乱码字节附近迷路连带 ASCII 行也报错。
            //    BOM 存在时 PS 自动识别为 UTF-8。
            File.WriteAllText(psPath, psContent, new UTF8Encoding(true));

            Debug.Log($"[HeroDefenseBuildTools] 启动器写入：{batPath} + _run.ps1");
        }

        // 给测试 / 美术看的说明
        private static void WriteTesterReadme(string outDir)
        {
            string readmePath = Path.Combine(outDir, "README.txt");
            string content =
                "HeroDefense PC 内测包（Windows Development Build · 网络共享部署）\r\n" +
                "================================================================\r\n" +
                "\r\n" +
                "## 部署方式（网络共享盘）\r\n" +
                "\r\n" +
                "开发把整个 <repo> 部署到团队共享盘 / 文件服务器（如 \\\\fileserver\\HeroDefense\\）：\r\n" +
                "\r\n" +
                "  \\\\fileserver\\HeroDefense\\\r\n" +
                "    ├── Build/Windows/      ← 本目录（引擎产物 + 启动器）\r\n" +
                "    │   ├── HeroDefense.exe\r\n" +
                "    │   ├── start_with_log.bat\r\n" +
                "    │   └── ...\r\n" +
                "    └── Game/               ← 资源库（脚本 / 美术 / 配置）\r\n" +
                "\r\n" +
                "测试 / 美术：映射网络盘 → 双击 Build/Windows/start_with_log.bat 即跑，无需安装 Unity。\r\n" +
                "ResourceHost 会自动从 exe 向上 2 级找到 Game/，部署在任何路径都能跑。\r\n" +
                "\r\n" +
                "微信 / 抖音小游戏版本走 CDN，不依赖本地 Game/，独立部署。\r\n" +
                "\r\n" +
                "## 启动游戏 + 日志\r\n" +
                "\r\n" +
                "双击 start_with_log.bat：\r\n" +
                "  - 弹出 \"HeroDefense Log (<你的用户名>) - 关本窗口同时退出游戏\" PowerShell 窗口\r\n" +
                "  - 同时启动游戏\r\n" +
                "\r\n" +
                "**关闭这个 PowerShell 窗口 = 同时关闭游戏 exe**（finally 块自动 Stop-Process）。\r\n" +
                "也可以反过来直接关游戏窗口，PowerShell 仍在 tail，再 X 它即可。\r\n" +
                "\r\n" +
                "日志文件落在 **本机 %TEMP%\\HeroDefense_output.log**（不写共享盘，避免多人覆盖）。\r\n" +
                "反馈 bug 时把这个文件附上即可，路径可在 PowerShell 窗口标题看到对应用户。\r\n" +
                "\r\n" +
                "## 修改资源 / 配置（改 Game/ 直接生效）\r\n" +
                "\r\n" +
                "  - 配置表  ：Game/settings/*.tab（Tab 分隔，4 行表头）\r\n" +
                "  - Lua 脚本：Game/scripts|modules|ui/**/*.lua\r\n" +
                "  - 美术资源：Game/resources/art/**/*.png 或 .jpg\r\n" +
                "\r\n" +
                "改完流程：\r\n" +
                "  1. 关闭游戏窗口（PowerShell 日志窗可一并关）\r\n" +
                "  2. 重新双击 start_with_log.bat\r\n" +
                "  3. 改动即时生效\r\n" +
                "\r\n" +
                "⚠️ 共享盘上的 Game/ 是团队同源，美术 / 测试改动**所有人立即受影响**。\r\n" +
                "   重要文件改动前请协调（特别是 settings/*.tab 数值平衡 / level.tab 关卡配置）。\r\n" +
                "\r\n" +
                "## 已知限制 / 注意事项\r\n" +
                "\r\n" +
                "  - 这是 Development Build，体积较大、启动稍慢，但保留完整日志栈\r\n" +
                "  - 修改 Lua 不触发热重载，必须关闭游戏重启\r\n" +
                "  - resources/art/*.png 或 .jpg 替换 / 修改无需 manifest 更新即生效（运行时直接 file IO）\r\n" +
                "  - 新增 / 删除文件后需重生 manifest.json，请联系开发跑 Tools/HeroDefense/Regenerate Manifest\r\n" +
                "  - 首次从共享盘运行 exe，Windows SmartScreen 可能弹「保护了你的电脑」\r\n" +
                "    → 点「更多信息 → 仍要运行」即可（一次过白名单后下次自动允许）\r\n" +
                "  - 网络盘读取慢于本地，启动时间可能多几秒（资源加载阶段）\r\n" +
                "\r\n" +
                "## 常见问题\r\n" +
                "\r\n" +
                "  Q: 启动后黑屏 / 没日志窗\r\n" +
                "  A: 看 PowerShell 窗口（或 %TEMP%\\HeroDefense_output.log）有无 [ResourceHost] 报错；\r\n" +
                "     若提示「未找到 (Game/)settings/Enum.tab」说明共享盘上 Game/ 目录路径错位或未同步，\r\n" +
                "     检查 ../../Game/settings/Enum.tab（相对 exe）能不能访问。\r\n" +
                "\r\n" +
                "  Q: 改了 resources/art/*.png 但没生效\r\n" +
                "  A: 关闭游戏完全重启。运行中改图不会热重载。\r\n" +
                "\r\n" +
                "  Q: 我的电脑没装 Unity 能跑吗？\r\n" +
                "  A: 能。引擎运行时（TuanjiePlayer.dll + Mono）已经打包进 Build/Windows/。\r\n" +
                "     需要的是 Windows 10+ 64 位系统，其他无依赖。\r\n";
            File.WriteAllText(readmePath, content, new UTF8Encoding(false));
            Debug.Log($"[HeroDefenseBuildTools] 测试说明写入：{readmePath}");
        }

        [MenuItem(MENU_BUILD_WEBGL)]
        public static void BuildWebGLMenu()
        {
            string gameRoot = GetGameRoot();
            string repoRoot = GetRepoRoot();
            RegenerateManifest(gameRoot);

            // 应用通用竖屏窗口设置；WebGL 实际 canvas 尺寸仍读 WebGL PlayerSettings。
            ApplyPortraitPlayerSettings();

            // 仓库目录约定：Build/Wechat/ 接受 WebGL 主包（再用 Tuanjie WeChat MiniGame 工具二次转）
            // 抖音小游戏走类似流程但输出到 Build/ByteGame/（待小游戏工具链就位单独菜单）
            string outDir = Path.Combine(repoRoot, "Build", "Wechat");
            EnsureDir(outDir);

            var options = new BuildPlayerOptions
            {
                scenes = GetEnabledScenePaths(),
                locationPathName = outDir,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None
            };

            Debug.Log($"[HeroDefenseBuildTools] 开始 WebGL 构建 → {outDir}");
            var report = BuildPipeline.BuildPlayer(options);
            LogBuildResult(report, outDir);
        }

        private static string[] GetEnabledScenePaths()
        {
            return EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
        }

        private static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        private static void LogBuildResult(UnityEditor.Build.Reporting.BuildReport report, string outDir)
        {
            var summary = report.summary;
            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"[HeroDefenseBuildTools] 构建完成: {outDir}，用时 {summary.totalTime.TotalSeconds:F1}s，总大小 {summary.totalSize / 1024 / 1024}MB");
                Debug.Log($"[HeroDefenseBuildTools] 提示：Game/ 下的 lua|config|art 未打入包体，请手动把这些内容（含 manifest.json）上传到 CDN。");
            }
            else
            {
                Debug.LogError($"[HeroDefenseBuildTools] 构建失败: {summary.result}, 错误数 {summary.totalErrors}");
            }
        }

        // ====================================================================
        // 菜单 4：Resource_LoadSprite 桥接自检（需 Play 模式）
        // ====================================================================

        [MenuItem(MENU_SELFCHECK_LOADSPRITE)]
        public static void SelfCheckLoadSpriteMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[HeroDefenseBuildTools] 自检需在 Play 模式下执行（Lua 环境此时才初始化）。");
                return;
            }
            if (!LuaHost.IsInitialized)
            {
                Debug.LogWarning("[HeroDefenseBuildTools] LuaHost 未初始化。");
                return;
            }

            var r1 = LuaHost.DoString(
                "local s = Resource_LoadSprite('resources/art/nonexistent.png') " +
                "return tostring(type(s)) .. '|' .. tostring(s)");
            string r1Str = r1 != null && r1.Length > 0 ? r1[0]?.ToString() : "<nil>";
            Debug.Log($"[HeroDefenseBuildTools] Self-Check 结果（不存在路径）: {r1Str}");

            var r2 = LuaHost.DoString("return tostring(type(Resource_LoadSprite))");
            string r2Str = r2 != null && r2.Length > 0 ? r2[0]?.ToString() : "<nil>";
            Debug.Log($"[HeroDefenseBuildTools] Self-Check 结果（函数类型）: {r2Str}");
        }

        // ====================================================================
        // 公共辅助
        // ====================================================================

        /// <summary>返回 {ProjectRoot}/../Product/Game 的绝对路径（业务仓在 Product/ 下，与引擎平级）。</summary>
        public static string GetGameRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, "..", "Product", "Game"));
        }

        /// <summary>返回 {RepoRoot} 即业务仓 Product/（引擎上一级的 Product 子目录）。</summary>
        private static string GetRepoRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, "..", "Product"));
        }

        // ====================================================================
        // 菜单 5：Build WebGL (Phase 1 Main) — Step 0 包体验证用
        // ====================================================================

        [MenuItem(MENU_BUILD_WEBGL_PHASE1)]
        public static void BuildWebGLPhase1Menu()
        {
            // P1.7 (2026-05-26) 说明：用户自己用 Tuanjie → WeChat/ByteGame MiniGame → 转换小游戏 出包
            // 本菜单仅作 debug fallback（产出原生 PC WebGL 到 Build/Phase1/WebGL/，不是小游戏格式）
            // 包大小验证用 Tools/HeroDefense/Measure Main Package（扫 Build/Wechat 和 Build/ByteGame）
            Debug.LogWarning("[HDBuildTools.Phase1] ⚠️ 此菜单产出原生 PC WebGL 不是小游戏包");
            Debug.LogWarning("[HDBuildTools.Phase1] 小游戏出包请用 Tuanjie → WeChat MiniGame → 转换小游戏 / ByteGame MiniGame → 转换小游戏");
            Debug.LogWarning("[HDBuildTools.Phase1] 仅作 debug/参考用途；如确实需要继续，本菜单 5s 后继续...");

            string gameRoot = GetGameRoot();
            string repoRoot = GetRepoRoot();

            // 1. 应用 HDBuildOptimizer 优化
            Debug.Log("[HDBuildTools.Phase1] 第 1 步：应用 Build Optimization");
            try
            {
                HDBuildOptimizer.ApplyAllOptimizations();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HDBuildTools.Phase1] HDBuildOptimizer 应用失败，继续构建: {e.Message}");
            }

            // 2. 生成 manifest（CDN 用）
            Debug.Log("[HDBuildTools.Phase1] 第 2 步：生成 manifest.json");
            int fileCount = RegenerateManifest(gameRoot);
            Debug.Log($"[HDBuildTools.Phase1] manifest.json 已生成 ({fileCount} 文件)");

            // 3. 准备输出目录
            string outDir = Path.Combine(repoRoot, PHASE1_OUTPUT_DIR, "WebGL");
            EnsureDir(outDir);
            Debug.Log($"[HDBuildTools.Phase1] 第 3 步：输出目录 → {outDir}");

            // 4. 执行 WebGL 构建（如果切到 WeixinMiniGame，BuildTarget 一致）
            //    Note: 微信小游戏专属构建走 Tuanjie → WeChat MiniGame → 转换小游戏 菜单
            //    本工具仅做标准 WebGL 构建，转换到小游戏由 transform-sdk 处理
            var options = new BuildPlayerOptions
            {
                scenes = GetEnabledScenePaths(),
                locationPathName = outDir,
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None
            };

            Debug.Log($"[HDBuildTools.Phase1] 第 4 步：开始 WebGL 构建...");
            var startTime = System.DateTime.Now;
            var report = BuildPipeline.BuildPlayer(options);
            var elapsed = System.DateTime.Now - startTime;

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"[HDBuildTools.Phase1] ✅ 构建完成 用时 {elapsed.TotalMinutes:F1} min");
                Debug.Log("[HDBuildTools.Phase1] 下一步: 跑 Tools/HeroDefense/Measure Main Package 测量包体");
                Debug.Log("[HDBuildTools.Phase1] 微信小游戏: 通过 Tuanjie → WeChat MiniGame → 转换小游戏 二次打包");
            }
            else
            {
                Debug.LogError($"[HDBuildTools.Phase1] ❌ 构建失败: {report.summary.result}, errors={report.summary.totalErrors}");
            }
        }

        // ====================================================================
        // 菜单 6：Measure Main Package — 测量主包大小
        // ====================================================================

        [MenuItem(MENU_MEASURE_MAIN_PACKAGE)]
        public static void MeasureMainPackageMenu()
        {
            // P1.7 v2 (2026-05-27): 真小游戏包测量
            // 之前 v1 把整个 Build/<platform>/ 全扫包括 webgl/ 中间产物 + symbols 调试文件，结果误报 70 MB
            // 新算法:
            //   1. 在 Build/<platform>/ 下找 game.json (定位真小游戏包根，通常是 minigame/ 子目录)
            //   2. 解析 game.json 的 subpackages → 区分主包 vs 各分包
            //   3. 跳过 *symbols* 文件（Development Build 调试用，不进客户端运行包）
            //   4. 跳过 pkgRoot 之外的目录（如 webgl/ 中间产物）
            //   5. 验收: 主包 ≤ 4 MB / 单分包 ≤ 20 MB / 总(主+分) ≤ 20 MB
            string repoRoot = GetRepoRoot();
            string buildRoot = Path.Combine(repoRoot, "Build");

            if (!Directory.Exists(buildRoot))
            {
                Debug.LogError($"[Measure] Build/ 目录不存在: {buildRoot}");
                Debug.LogError("[Measure] 请用 Tuanjie → WeChat MiniGame 或 ByteGame MiniGame 出包后再跑");
                return;
            }

            var platforms = new (string dir, string label)[] {
                ("Wechat",  "微信小游戏"),
                ("ByteGame", "抖音小游戏"),
            };

            int measured = 0;
            foreach (var p in platforms)
            {
                string platDir = Path.Combine(buildRoot, p.dir);
                if (!Directory.Exists(platDir)) continue;

                // 寻找 game.json — 微信/抖音小游戏的 manifest，定位真包根
                string gameJsonPath = FindMiniGameRoot(platDir);
                if (gameJsonPath == null)
                {
                    Debug.Log($"[Measure] {p.label} ({platDir}) 找不到 game.json — 跳过（请用 Tuanjie/TTSDK 转换小游戏出真包）");
                    continue;
                }
                measured++;
                MeasureMiniGamePackage(gameJsonPath, p.label);
            }

            if (measured == 0)
            {
                Debug.LogError("[Measure] 未找到任何小游戏构建输出（缺 game.json）");
                Debug.LogError("[Measure] 请先用 Tuanjie → WeChat MiniGame → 转换小游戏 / TTSDK → ByteGame 转换小游戏 出包");
            }

            // CDN 资源扫描 — 不论小游戏包是否出包都跑（独立诊断 Game/ 单文件大小）
            ScanCdnResources();

            if (measured > 0)
            {
                Debug.Log($"[Measure] ===== 完成（测量 {measured} 个小游戏平台 + CDN）=====");
            }
        }

        /// <summary>
        /// P1.7 v3 (2026-05-27): 扫 Game/ CDN 资源，找出 ≥1 MB 单文件，判定跨平台安全。
        /// CDN 无总量限制（受平台 CDN 计费而非小游戏 quota）；只有单文件软上限：
        ///   - ≤ 4 MB: 跨平台安全（微信 + 抖音 + 弱网都 OK）
        ///   - 4-10 MB: 警告（弱网偶发超时）
        ///   - > 10 MB: 严重（应拆 atlas / 压缩 / 走子图）
        /// 扫描范围与 RegenerateManifest 一致：scripts/ modules/ ui/ settings/ resources/ 子目录
        /// </summary>
        private static void ScanCdnResources()
        {
            string gameRoot = GetGameRoot();
            if (!Directory.Exists(gameRoot))
            {
                Debug.LogWarning($"[Measure] Game/ 不存在，跳过 CDN 扫描: {gameRoot}");
                return;
            }

            Debug.Log("[Measure] ===== CDN 资源（Game/）=====");
            Debug.Log($"[Measure] 扫描根: {gameRoot}");

            const long ONE_MB = 1024L * 1024L;
            const long FOUR_MB = 4L * ONE_MB;
            const long TEN_MB = 10L * ONE_MB;

            string[] subDirs = { "scripts", "modules", "ui", "settings", "resources" };
            long totalSize = 0;
            int totalFiles = 0;
            var bigFiles = new List<(string rel, long size)>();
            int warnFiles = 0, errFiles = 0;
            var byKind = new Dictionary<string, long>();

            foreach (var sub in subDirs)
            {
                string subFull = Path.Combine(gameRoot, sub);
                if (!Directory.Exists(subFull)) { byKind[sub] = 0; continue; }

                long kindSize = 0;
                foreach (var file in Directory.GetFiles(subFull, "*.*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    // 与 RegenerateManifest 一致的过滤
                    if (name.StartsWith(".")) continue;
                    if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(".desc", StringComparison.OrdinalIgnoreCase)) continue;

                    long len = new FileInfo(file).Length;
                    totalSize += len;
                    kindSize += len;
                    totalFiles++;

                    if (len >= ONE_MB)
                    {
                        string rel = file.Substring(gameRoot.Length + 1).Replace('\\', '/');
                        bigFiles.Add((rel, len));
                        if (len > TEN_MB) errFiles++;
                        else if (len > FOUR_MB) warnFiles++;
                    }
                }
                byKind[sub] = kindSize;
            }

            float totalMB = totalSize / 1024.0f / 1024.0f;
            Debug.Log($"[Measure] CDN 总量: {totalMB:F2} MB / {totalFiles} 文件 (无平台总量上限)");
            foreach (var kv in byKind)
            {
                float kindMB = kv.Value / 1024.0f / 1024.0f;
                Debug.Log($"[Measure]   {kv.Key,-8}: {kindMB:F2} MB");
            }

            // 列出 ≥ 1 MB 文件
            bigFiles.Sort((a, b) => b.size.CompareTo(a.size));
            if (bigFiles.Count == 0)
            {
                Debug.Log("[Measure] ✅ 无 ≥1 MB 单文件 — CDN 单文件状况良好");
            }
            else
            {
                Debug.Log($"[Measure] ≥ 1 MB 单文件（共 {bigFiles.Count} 个，按大小降序）:");
                int show = System.Math.Min(20, bigFiles.Count);
                for (int i = 0; i < show; i++)
                {
                    var f = bigFiles[i];
                    float mb = f.size / 1024.0f / 1024.0f;
                    string tag = f.size > TEN_MB ? "❌" : (f.size > FOUR_MB ? "⚠️" : "  ");
                    Debug.Log($"[Measure]   {tag} {mb,5:F2} MB  {f.rel}");
                }
                if (bigFiles.Count > show)
                    Debug.Log($"[Measure]   ... 还有 {bigFiles.Count - show} 个 ≥1 MB 文件（未列出，按大小降序前 {show} 已显示）");
            }

            // 单文件红线判定
            if (errFiles > 0)
                Debug.LogError($"[Measure] ❌ {errFiles} 个文件 > 10 MB — 弱网下载大概率失败，必须拆 atlas / 压缩 / 切分");
            if (warnFiles > 0)
                Debug.LogWarning($"[Measure] ⚠️ {warnFiles} 个文件 4-10 MB — 跨平台安全边界外（抖音旧规 ≤ 4 MB），建议 atlas 拆分");
            if (errFiles == 0 && warnFiles == 0)
                Debug.Log("[Measure] ✅ 所有 CDN 单文件 ≤ 4 MB — 跨平台（微信+抖音+弱网）安全区");
        }

        /// <summary>在 platDir 下找 game.json，取最浅一个（避免命中 webgl/ 中间产物里的 placeholder）。</summary>
        private static string FindMiniGameRoot(string platDir)
        {
            try
            {
                var files = Directory.GetFiles(platDir, "game.json", SearchOption.AllDirectories);
                if (files.Length == 0) return null;
                // 取路径段数最少的（最浅）
                System.Array.Sort(files, (a, b) => a.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length
                                                  - b.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length);
                return files[0];
            }
            catch { return null; }
        }

        /// <summary>简单 regex 提取 game.json 的 subpackages[].root 列表。</summary>
        private static List<string> ParseSubpackageRoots(string gameJsonText)
        {
            var roots = new List<string>();
            var pattern = new System.Text.RegularExpressions.Regex(@"""root""\s*:\s*""([^""]+)""");
            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(gameJsonText))
            {
                var v = m.Groups[1].Value;
                if (!string.IsNullOrEmpty(v))
                    roots.Add(v.Replace('\\', '/').TrimEnd('/'));
            }
            return roots;
        }

        private static void MeasureMiniGamePackage(string gameJsonPath, string label)
        {
            string pkgRoot = Path.GetDirectoryName(gameJsonPath);
            Debug.Log($"[Measure] ===== {label} =====");
            Debug.Log($"[Measure] 包根: {pkgRoot}");

            // 解析 subpackages
            var subRoots = ParseSubpackageRoots(File.ReadAllText(gameJsonPath));
            Debug.Log($"[Measure] 检测到 {subRoots.Count} 个分包: [{string.Join(", ", subRoots)}]");

            long mainSize = 0, debugSize = 0;
            int mainFiles = 0, debugFiles = 0;
            int subFiles = 0;
            var subSizes = new Dictionary<string, long>();
            foreach (var sp in subRoots) subSizes[sp] = 0;

            foreach (var file in Directory.GetFiles(pkgRoot, "*.*", SearchOption.AllDirectories))
            {
                var len = new FileInfo(file).Length;
                var name = Path.GetFileName(file).ToLower();
                var rel = file.Substring(pkgRoot.Length + 1).Replace('\\', '/');

                // 调试 symbols（不打入客户端运行包，Tuanjie WeChat MiniGame 出 Development Build 才有）
                if (name.Contains("symbols"))
                {
                    debugSize += len;
                    debugFiles++;
                    continue;
                }

                // 归属分包
                bool inSub = false;
                foreach (var sp in subRoots)
                {
                    if (rel.StartsWith(sp + "/"))
                    {
                        subSizes[sp] += len;
                        subFiles++;
                        inSub = true;
                        break;
                    }
                }
                if (inSub) continue;

                // 默认 = 主包
                mainSize += len;
                mainFiles++;
            }

            float mainMB = mainSize / 1024.0f / 1024.0f;
            float debugMB = debugSize / 1024.0f / 1024.0f;
            long subTotalBytes = 0;
            foreach (var v in subSizes.Values) subTotalBytes += v;
            float subTotalMB = subTotalBytes / 1024.0f / 1024.0f;
            float allMB = mainMB + subTotalMB;

            Debug.Log($"[Measure]   主包 (启动同步加载): {mainMB:F2} MB / 文件 {mainFiles}");
            foreach (var kv in subSizes)
            {
                float spMB = kv.Value / 1024.0f / 1024.0f;
                Debug.Log($"[Measure]   分包 {kv.Key,-18}: {spMB:F2} MB");
            }
            Debug.Log($"[Measure]   ----- 主+分包合计: {allMB:F2} MB / 客户端文件 {mainFiles + subFiles} -----");
            if (debugFiles > 0)
                Debug.Log($"[Measure]   (调试 symbols 不打入客户端: {debugMB:F2} MB / {debugFiles} 文件 — 关 Development Build 可去掉)");

            // 验收（微信/抖音小游戏 2026 规则）：
            //   主包（启动）≤ 4 MB
            //   单分包 ≤ 20 MB（WASM CodeSplit 开启后；旧规则 4 MB）
            //   主+分包合计 ≤ 20 MB（不含 CDN 远程资源）
            if (mainMB <= 4.0f)
                Debug.Log($"[Measure]   ✅ 主包 {mainMB:F2} MB ≤ 4 MB 硬上限");
            else
                Debug.LogError($"[Measure]   ❌ 主包 {mainMB:F2} MB 超 4 MB 硬上限！必须缩减启动同步资源");

            foreach (var kv in subSizes)
            {
                float spMB = kv.Value / 1024.0f / 1024.0f;
                if (spMB > 20.0f)
                    Debug.LogError($"[Measure]   ❌ 分包 {kv.Key} {spMB:F2} MB 超 20 MB 单分包上限");
            }

            if (allMB <= 20.0f)
                Debug.Log($"[Measure]   ✅ 主+分包合计 {allMB:F2} MB ≤ 20 MB 平台上限");
            else
                Debug.LogError($"[Measure]   ❌ 主+分包合计 {allMB:F2} MB 超 20 MB 平台上限！必须缩减或更多走 CDN");
        }

        // ====================================================================
        // 菜单 7：Profile First Frame — 测首屏时长
        // ====================================================================

        [MenuItem(MENU_PROFILE_FIRST_FRAME)]
        public static void ProfileFirstFrameMenu()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[HDBuildTools.Profile] Editor 已在 PlayMode，先停止 PlayMode 后再跑");
                return;
            }

            Debug.Log("[HDBuildTools.Profile] ===== 首屏时长 Profile（Editor 端） =====");
            Debug.Log("[HDBuildTools.Profile] 准备进入 PlayMode...");
            Debug.Log("[HDBuildTools.Profile] 启动后请观察 Console 中 BootInitializer 的 Step 1-9 时间戳");
            Debug.Log("[HDBuildTools.Profile] 目标：从 PlayMode 触发到 MainMenu 显示 ≤ 5s (Editor) / ≤ 8s (WebGL)");
            Debug.Log("[HDBuildTools.Profile] -----------------------------------------");
            Debug.Log("[HDBuildTools.Profile] 注意: BootInitializer 当前为同步实现");
            Debug.Log("[HDBuildTools.Profile] Step 1 实施异步化后此菜单可量化各阶段耗时（详 Docs/build/04-boot-async.md）");
            Debug.Log("[HDBuildTools.Profile] -----------------------------------------");

            // 触发 PlayMode（用户在 Editor 看 Console 时间戳）
            EditorApplication.EnterPlaymode();

            // 注册退出 PlayMode 时打总时长
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            _profileStartTime = System.DateTime.Now;
        }

        private static System.DateTime _profileStartTime;

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                var elapsed = System.DateTime.Now - _profileStartTime;
                Debug.Log($"[HDBuildTools.Profile] ===== PlayMode 结束，总用时 {elapsed.TotalSeconds:F2}s =====");
                Debug.Log("[HDBuildTools.Profile] 注意：上述用时包含手动停止 PlayMode 的延迟，仅作 Editor 参考");
                Debug.Log("[HDBuildTools.Profile] 真机首屏请在微信开发者工具 Performance 面板量化");
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            }
        }
    }
}

