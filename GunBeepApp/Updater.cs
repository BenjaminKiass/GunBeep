using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace GunBeep
{
    // อัปเดตตัวเองจาก GitHub Releases (github.com/BenjaminKiass/GunBeep)
    //   GitHub Actions build ทุกครั้งที่ push ขึ้น main → Release "v1.0.<run>" + ไฟล์ GunBeep-win-x64.zip
    //   GunBeep เทียบเวอร์ชันกับตัวเอง → ถ้าใหม่กว่า โหลด zip แล้วให้ PowerShell รอโปรแกรมปิด
    //   คัดลอกทับโฟลเดอร์โปรแกรม (ยกเว้น saved\) แล้วเปิดใหม่
    public static class Updater
    {
        public const string Repo = "BenjaminKiass/GunBeep";

        public class Release
        {
            public Version Version;
            public string Tag;
            public string ZipUrl;
        }

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GunBeep-Updater");
            return c;
        }

        static Version Norm(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        public static Version Current => Norm(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

        // คืน Release ถ้ามีเวอร์ชันใหม่กว่า, null ถ้าไม่มีหรือเช็กไม่ได้
        public static async Task<Release> CheckAsync()
        {
            try
            {
                string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                var j = JsonNode.Parse(json);
                string tag = j?["tag_name"]?.GetValue<string>();
                if (tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
                string zip = j["assets"]?.AsArray()
                    .Select(a => a?["browser_download_url"]?.GetValue<string>())
                    .FirstOrDefault(u => u != null && u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip == null) return null;
                v = Norm(v);
                return v > Current ? new Release { Version = v, Tag = tag, ZipUrl = zip } : null;
            }
            catch { return null; }
        }

        // โหลดและเตรียมไฟล์ แล้วสั่งตัวคัดลอกให้รอโปรแกรมปิด — ผู้เรียกต้องปิดโปรแกรมต่อทันที
        public static async Task StageAsync(Release r)
        {
            string work = Path.Combine(Path.GetTempPath(), "GunBeep_update_" + r.Version);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            string zipPath = Path.Combine(work, "update.zip");
            using (var resp = await http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs);
            }

            string files = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zipPath, files);
            if (!File.Exists(Path.Combine(files, "GunBeep.exe")))
                throw new InvalidDataException("ไฟล์อัปเดตไม่มี GunBeep.exe");

            string ps1 = Path.Combine(work, "apply.ps1");
            File.WriteAllText(ps1, @"param([int]$ProcId, [string]$Src, [string]$Dst)
Wait-Process -Id $ProcId -Timeout 30 -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700
robocopy $Src $Dst /E /XD saved /R:5 /W:1 /NFL /NDL /NJH /NJS | Out-Null
Start-Process (Join-Path $Dst 'GunBeep.exe') -WorkingDirectory $Dst
", new UTF8Encoding(true));

            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", ps1,
                "-ProcId", Environment.ProcessId.ToString(),
                "-Src", files,
                "-Dst", AppContext.BaseDirectory.TrimEnd('\\'),
            }) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
    }
}
