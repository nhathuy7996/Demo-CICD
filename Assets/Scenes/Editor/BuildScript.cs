using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class BuildScript
{
    // ==== CONFIG ====
    // Cho phép upload bằng rclone nếu RCLONE_REMOTE hoặc DRIVE_FOLDER_ID có mặt trong env
    private const string DefaultBuildRoot = "Builds";
    private const int MaxSlugLen = 48;

    // ==== PUBLIC MENUS ====
    [MenuItem("Build/Build Android")]
    public static void BuildAndroid()
    {
        try
        {
            string buildRoot = GetBuildRoot();
            string outputDir = Path.Combine(buildRoot, "Android");
            Directory.CreateDirectory(outputDir);

            // --- Tạo tên file từ commit/branch ---
            string appName = PlayerSettings.productName;
            string branch = Env("GITHUB_REF_NAME", "local");
            string sha = Env("GITHUB_SHA", Guid.NewGuid().ToString("N"));
            string shortSha = sha.Length >= 7 ? sha.Substring(0, 7) : sha;
            string commitSubject = Env("GIT_COMMIT_SUBJECT", "").Trim();
            string slug = string.IsNullOrEmpty(commitSubject) ? null : Slugify(commitSubject);
            if (!string.IsNullOrEmpty(slug) && slug.Length > MaxSlugLen) slug = slug.Substring(0, MaxSlugLen);

            // Tên cuối: App-branch-shortsha[-slug].apk
            var filenameOnly = string.IsNullOrEmpty(slug)
                ? $"{appName}-{branch}-{shortSha}.apk"
                : $"{appName}-{branch}-{shortSha}-{slug}.apk";

            string filePath = Path.Combine(outputDir, filenameOnly);

            // --- Scenes ---
            string[] scenes = GetEnabledScenes();
            if (scenes.Length == 0)
            {
                Debug.LogError("No scenes found in Build Settings! Please add scenes in File > Build Settings");
                EditorApplication.Exit(1);
                return;
            }

            // --- Build ---
            Debug.Log($"Building Android APK");
            Debug.Log($"Output path: {filePath}");
            Debug.Log($"Scenes: {string.Join(", ", scenes)}");

            var opts = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = filePath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(opts);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"✓ Build succeeded!");
                Debug.Log($"  Size: {summary.totalSize} bytes");
                Debug.Log($"  Output: {filePath}");
                Debug.Log($"  Build time: {summary.totalTime}");

                // --- Upload bằng rclone (tùy chọn) ---
                TryUploadViaRclone(filePath);
            }
            else
            {
                Debug.LogError($"✗ Build failed! Errors: {summary.totalErrors}, Warnings: {summary.totalWarnings}");
                EditorApplication.Exit(1);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Build exception: {e.Message}\n{e.StackTrace}");
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Build/Build WebGL")]
    public static void BuildWebGL()
    {
        string buildRoot = GetBuildRoot();
        string outputDir = Path.Combine(buildRoot, "WebGL");
        Directory.CreateDirectory(outputDir);

        string[] scenes = GetEnabledScenes();
        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputDir,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        };

        Debug.Log($"Building WebGL to: {outputDir}");

        var report = BuildPipeline.BuildPlayer(opts);
        var summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"Build succeeded: {summary.totalSize} bytes");
            Debug.Log($"Output: {outputDir}");
        }
        else
        {
            Debug.LogError("Build failed");
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Build/Build Windows")]
    public static void BuildWindows()
    {
        string buildRoot = GetBuildRoot();
        string outputDir = Path.Combine(buildRoot, "Windows");
        Directory.CreateDirectory(outputDir);

        string appName = PlayerSettings.productName;
        string filePath = Path.Combine(outputDir, $"{appName}.exe");

        string[] scenes = GetEnabledScenes();
        var opts = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = filePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };

        Debug.Log($"Building Windows to: {filePath}");

        var report = BuildPipeline.BuildPlayer(opts);
        var summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"Build succeeded: {summary.totalSize} bytes");
            Debug.Log($"Output: {filePath}");
        }
        else
        {
            Debug.LogError("Build failed");
            EditorApplication.Exit(1);
        }
    }

    // ==== HELPERS ====
    private static string GetBuildRoot()
    {
        // Thống nhất gốc Builds ở cạnh thư mục project (Assets/..)
        return Path.Combine(Application.dataPath, "..", DefaultBuildRoot);
    }

    private static string[] GetEnabledScenes()
    {
        var all = EditorBuildSettings.scenes;
        var list = new System.Collections.Generic.List<string>(all.Length);
        foreach (var s in all)
        {
            if (s.enabled) list.Add(s.path);
        }
        return list.ToArray();
    }

    private static string Env(string key, string fallback = null)
        => Environment.GetEnvironmentVariable(key) ?? fallback;

    private static string Slugify(string input)
    {
        var s = input.Trim();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-' || ch == '_' || ch == '.') sb.Append('-');
            // else skip
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private static void TryUploadViaRclone(string localFilePath)
    {
        // Điều kiện tối thiểu: có rclone + remote hoặc folderId
        string rclonePath = Env("RCLONE_PATH", "rclone");            // ví dụ: C:\rclone\rclone.exe  (mặc định: rclone trong PATH)
        string remote = Env("RCLONE_REMOTE", null);              // ví dụ: gdrive:/MyFolder
        string folderId = Env("DRIVE_FOLDER_ID", null);            // ví dụ: 1AbCDefGhijkLMNOPq (ID thư mục Drive)
        if (string.IsNullOrEmpty(remote) && string.IsNullOrEmpty(folderId))
        {
            Debug.Log("Rclone upload skipped: RCLONE_REMOTE or DRIVE_FOLDER_ID not set.");
            return;
        }

        try
        {
            // 1) Copy file lên Drive
            string copyArgs;
            if (!string.IsNullOrEmpty(remote))
            {
                // copy to a given remote:path
                copyArgs = $"copy \"{localFilePath}\" \"{remote}\" --progress";
            }
            else
            {
                // copy to root with folder as rootId
                copyArgs = $"copy \"{localFilePath}\" \"gdrive:\" --drive-root-folder-id \"{folderId}\" --progress";
            }
            var copyOk = Exec(rclonePath, copyArgs, out var copyOut, out var copyErr);
            Debug.Log($"[rclone copy] ok={copyOk}\n{copyOut}\n{copyErr}");

            // 2) Lấy share link
            string linkTarget;
            string linkArgs;
            var fileName = Path.GetFileName(localFilePath);

            if (!string.IsNullOrEmpty(remote))
            {
                // link remote:path/to/file
                // nếu remote là "gdrive:/MyFolder", linkTarget = "gdrive:/MyFolder/<file>"
                linkTarget = remote.EndsWith("/") ? $"{remote}{fileName}" : $"{remote}/{fileName}";
                linkArgs = $"link \"{linkTarget}\"";
            }
            else
            {
                // với folderId, dùng root folder id + tên file
                // rclone link sẽ tôn trọng --drive-root-folder-id
                linkTarget = $"gdrive:{fileName}";
                linkArgs = $"link \"{linkTarget}\" --drive-root-folder-id \"{folderId}\"";
            }

            var linkOk = Exec(rclonePath, linkArgs, out var linkOut, out var linkErr);
            if (linkOk && !string.IsNullOrWhiteSpace(linkOut))
            {
                var url = linkOut.Trim();
                Debug.Log($"📤 Uploaded to Google Drive: {url}");
            }
            else
            {
                Debug.LogWarning($"rclone link failed.\n{linkOut}\n{linkErr}\nBạn vẫn có thể tìm file theo tên trên Drive.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Rclone upload exception: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static bool Exec(string file, string args, out string stdOut, out string stdErr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var p = Process.Start(psi))
        {
            stdOut = p.StandardOutput.ReadToEnd();
            stdErr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }
}
