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
    private const string DefaultBuildRoot = "Builds";
    private const int MaxSlugLen = 48;

    [MenuItem("Build/Build Android")]
    public static void BuildAndroid()
    {
        try
        {
            string buildRoot = GetBuildRoot();
            string outputDir = Path.Combine(buildRoot, "Android");
            Directory.CreateDirectory(outputDir);

            // --- File name from branch/sha/commit subject ---
            string appName = PlayerSettings.productName;
            string branch = Env("GITHUB_REF_NAME", "local");
            string sha = Env("GITHUB_SHA", Guid.NewGuid().ToString("N"));
            string shortSha = sha.Length >= 7 ? sha.Substring(0, 7) : sha;
            string commitSubject = Env("GIT_COMMIT_SUBJECT", "").Trim();
            string slug = string.IsNullOrEmpty(commitSubject) ? null : Slugify(commitSubject);
            if (!string.IsNullOrEmpty(slug) && slug.Length > MaxSlugLen) slug = slug.Substring(0, MaxSlugLen);

            string filenameOnly = string.IsNullOrEmpty(slug)
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

            Debug.Log("Building Android APK");
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
                Debug.Log("✓ Build succeeded!");
                Debug.Log($"  Size: {summary.totalSize} bytes");
                Debug.Log($"  Output: {filePath}");
                Debug.Log($"  Build time: {summary.totalTime}");

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
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    private static string RcloneConfigArg()
    {
        // Cách C: chỉ định config cố định để không phụ thuộc account (NetworkService / user)
        string cfg = Env("RCLONE_CONFIG", null);
        if (string.IsNullOrWhiteSpace(cfg)) return "";
        return $" --config \"{cfg}\"";
    }

    private static void TryUploadViaRclone(string localFilePath)
    {
        string rclonePath = Env("RCLONE_PATH", "rclone");
        string remote = Env("RCLONE_REMOTE", null);       // ví dụ: gdrive:/UnityBuilds/APK
        string folderId = Env("DRIVE_FOLDER_ID", null);   // optional
        string cfg = Env("RCLONE_CONFIG", null);

        if (string.IsNullOrEmpty(remote) && string.IsNullOrEmpty(folderId))
        {
            Debug.Log("Rclone upload skipped: RCLONE_REMOTE or DRIVE_FOLDER_ID not set.");
            return;
        }

        Debug.Log($"[rclone] exe={rclonePath}");
        Debug.Log($"[rclone] config={(string.IsNullOrWhiteSpace(cfg) ? "(default profile)" : cfg)}");
        Debug.Log($"[rclone] remote={(string.IsNullOrWhiteSpace(remote) ? "(null)" : remote)}");
        Debug.Log($"[rclone] folderId={(string.IsNullOrWhiteSpace(folderId) ? "(null)" : folderId)}");

        try
        {
            // 1) Copy file lên Drive
            string copyArgs;
            if (!string.IsNullOrEmpty(remote))
            {
                copyArgs = $"copy \"{localFilePath}\" \"{remote}\" --progress{RcloneConfigArg()}";
            }
            else
            {
                // NOTE: mặc định remote gdrive:
                copyArgs = $"copy \"{localFilePath}\" \"gdrive:\" --drive-root-folder-id \"{folderId}\" --progress{RcloneConfigArg()}";
            }

            var copyOk = Exec(rclonePath, copyArgs, out var copyOut, out var copyErr);
            Debug.Log($"[rclone copy] ok={copyOk}\n{copyOut}\n{copyErr}");
            if (!copyOk)
            {
                Debug.LogWarning("Rclone copy failed. Check rclone output above.");
                return;
            }

            // 2) Lấy share link
            string linkTarget;
            string linkArgs;
            string fileName = Path.GetFileName(localFilePath);

            if (!string.IsNullOrEmpty(remote))
            {
                linkTarget = remote.EndsWith("/") ? $"{remote}{fileName}" : $"{remote}/{fileName}";
                linkArgs = $"link \"{linkTarget}\"{RcloneConfigArg()}";
            }
            else
            {
                linkTarget = $"gdrive:{fileName}";
                linkArgs = $"link \"{linkTarget}\" --drive-root-folder-id \"{folderId}\"{RcloneConfigArg()}";
            }

            var linkOk = Exec(rclonePath, linkArgs, out var linkOut, out var linkErr);
            if (linkOk && !string.IsNullOrWhiteSpace(linkOut))
            {
                var url = linkOut.Trim();
                Debug.Log($"📤 Uploaded to Google Drive: {url}");
                // In ra rõ ràng để dễ search trong Actions log
                Debug.Log($"::notice title=Google Drive::APK Link: {url}");
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
