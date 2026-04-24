using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DBManager.Pages;

public class IndexModel : PageModel
{
    [BindProperty]
    public string? FolderPath { get; set; }

    [BindProperty]
    public string? SelectedPath { get; set; }

    public string? Message { get; set; }

    public List<DriveInfo> Drives { get; set; } = new();

    public void OnGet()
    {
        Drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .ToList();
    }

    public IActionResult OnPost()
    {
        if (!string.IsNullOrEmpty(SelectedPath))
        {
            Message = $"選擇的路徑: {SelectedPath}";
        }
        else
        {
            Message = "請選擇資料夾";
        }
        
        Drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .ToList();
        return Page();
    }

    public IActionResult OnGetFolders(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new { 
                        name = d.Name, 
                        path = d.RootDirectory.FullName,
                        isDrive = true 
                    });
                return new JsonResult(drives);
            }

            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists) return new JsonResult(new List<object>());

            var folders = dirInfo.GetDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden) && 
                           !d.Attributes.HasFlag(FileAttributes.System))
                .Select(d => new { name = d.Name, path = d.FullName, isDrive = false })
                .ToList();
            
            return new JsonResult(folders);
        }
        catch (Exception)
        {
            return new JsonResult(new List<object>());
        }
    }

    public IActionResult OnGetVideos(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return new JsonResult(new List<object>());
            }

            string[] videoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp" };
            
            var files = new DirectoryInfo(path)
                .GetFiles()
                .Where(f => videoExtensions.Contains(f.Extension.ToLowerInvariant()))
                .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Attributes.HasFlag(FileAttributes.System))
                .Select(f => new { 
                    name = f.Name, 
                    path = f.FullName,
                    size = f.Length,
                    sizeFormatted = FormatFileSize(f.Length)
                })
                .ToList();
            
            return new JsonResult(files);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message });
        }
    }

    public IActionResult OnGetVideoMetadata(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                return new JsonResult(new { error = "檔案不存在" });
            }

            var ffprobePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffprobe.exe");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new JsonResult(new { error = "無法執行 ffprobe" });
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                string error = process.StandardError.ReadToEnd();
                return new JsonResult(new { error = error });
            }

            var jsonDoc = JsonDocument.Parse(output);
            var root = jsonDoc.RootElement;

            var result = new Dictionary<string, object>();
            
            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("tags", out var tags))
                {
                    foreach (var tag in tags.EnumerateObject())
                    {
                        result[tag.Name] = tag.Value.ToString();
                    }
                }
                
                if (format.TryGetProperty("duration", out var duration))
                {
                    double durationValue = duration.ValueKind == JsonValueKind.String 
                        ? double.Parse(duration.GetString()!) 
                        : duration.GetDouble();
                    result["duration"] = durationValue;
                    result["durationFormatted"] = FormatDuration(durationValue);
                }
                
                if (format.TryGetProperty("size", out var size))
                {
                    long sizeValue = size.ValueKind == JsonValueKind.String 
                        ? long.Parse(size.GetString()!) 
                        : size.GetInt64();
                    result["size"] = sizeValue;
                }
                
                if (format.TryGetProperty("format_name", out var formatName))
                {
                    result["format_name"] = formatName.GetString() ?? "";
                }
            }

            return new JsonResult(result);
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message });
        }
    }

    public IActionResult OnPostUpdateMetadata(string path, string title, string artist, string comment, string description)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var ffmpegPath = Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
            
            if (string.IsNullOrEmpty(path))
            {
                var exists = System.IO.File.Exists(ffmpegPath);
                return new JsonResult(new { success = false, error = "路徑為空", baseDir, ffmpegExists = exists });
            }
            
            if (!System.IO.File.Exists(path))
            {
                return new JsonResult(new { success = false, error = $"檔案不存在: {path}" });
            }

            if (!System.IO.File.Exists(ffmpegPath))
            {
                return new JsonResult(new { success = false, error = $"ffmpeg 不存在", ffmpegPath, baseDir });
            }

            bool canWrite = FileSystemCanWrite(path);
            var fileInfo = new FileInfo(path);
            var dirInfo = new DirectoryInfo(Path.GetDirectoryName(path) ?? "");
            var tempPath = path + ".tmp";
            
            var arguments = $"-y -i \"{path}\" -c copy";
            
            if (!string.IsNullOrEmpty(title))
                arguments += $" -metadata title=\"{EscapeMetadata(title)}\"";
            if (!string.IsNullOrEmpty(artist))
                arguments += $" -metadata artist=\"{EscapeMetadata(artist)}\"";
            if (!string.IsNullOrEmpty(comment))
                arguments += $" -metadata comment=\"{EscapeMetadata(comment)}\"";
            if (!string.IsNullOrEmpty(description))
                arguments += $" -metadata description=\"{EscapeMetadata(description)}\"";
            
            arguments += $" \"{tempPath}\"";

            var workingDir = Path.GetDirectoryName(path) ?? "";
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new JsonResult(new { success = false, error = "無法執行 ffmpeg", debug = new { path, ffmpegPath, canWrite }});
            }

            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return new JsonResult(new { success = false, error = error, command = $"{ffmpegPath} {arguments}", exitCode = process.ExitCode, debug = new { path, ffmpegPath, canWrite, workingDir }});
            }

            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(path);
                System.IO.File.Move(tempPath, path);
                return new JsonResult(new { success = true, message = "已成功更新影片註解" });
            }
            else
            {
                return new JsonResult(new { success = false, error = "臨時檔案未建立", tempPath = tempPath, workingDir = workingDir, debug = new { path, ffmpegPath, canWrite }});
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return new JsonResult(new { success = false, error = $"權限不足: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message, stack = ex.StackTrace });
        }
    }

    private static bool FileSystemCanWrite(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                return false;
            }
            var testFile = Path.Combine(dir ?? "", ".write_test_" + Guid.NewGuid().ToString("N"));
            using var fs = System.IO.File.Create(testFile, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 
            ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}" 
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string EscapeMetadata(string value)
    {
        return value.Replace("\"", "\\\"");
    }
}