using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace RadminStreamApp
{
    public static class UpdateManager
    {
        private const string GitHubRepoOwner = "Lintzz";
        private const string GitHubRepoName = "app-live";
        private const string LatestReleaseUrl = $"https://api.github.com/repos/{GitHubRepoOwner}/{GitHubRepoName}/releases/latest";

        public class GitHubReleaseInfo
        {
            public string tag_name { get; set; }
            public GitHubAsset[] assets { get; set; }
        }

        public class GitHubAsset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
        }

        public class UpdateCheckResult
        {
            public bool HasUpdate { get; set; }
            public string LatestVersion { get; set; }
            public string DownloadUrl { get; set; }
        }

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                // O GitHub API exige um User-Agent
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RadminStreamApp", "1.0"));

                var response = await client.GetAsync(LatestReleaseUrl);
                if (!response.IsSuccessStatusCode)
                    return new UpdateCheckResult { HasUpdate = false };

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var releaseInfo = JsonSerializer.Deserialize<GitHubReleaseInfo>(jsonResponse);

                if (releaseInfo == null || string.IsNullOrEmpty(releaseInfo.tag_name))
                    return new UpdateCheckResult { HasUpdate = false };

                // O tag pode ter "v" no começo, ex: "v1.0.1"
                string latestVersionStr = releaseInfo.tag_name.TrimStart('v', 'V');
                
                if (Version.TryParse(latestVersionStr, out Version latestVersion))
                {
                    Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                    
                    if (latestVersion > currentVersion)
                    {
                        var installerAsset = releaseInfo.assets?.FirstOrDefault(a => a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                        
                        if (installerAsset != null)
                        {
                            return new UpdateCheckResult
                            {
                                HasUpdate = true,
                                LatestVersion = releaseInfo.tag_name,
                                DownloadUrl = installerAsset.browser_download_url
                            };
                        }
                    }
                }
                
                return new UpdateCheckResult { HasUpdate = false };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao verificar atualizações: {ex.Message}");
                return new UpdateCheckResult { HasUpdate = false };
            }
        }

        public static async Task DownloadAndInstallUpdateAsync(string downloadUrl)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RadminStreamApp", "1.0"));

                var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                string tempPath = Path.GetTempPath();
                string fileName = $"RadminStream_Setup_Update_{Guid.NewGuid().ToString().Substring(0,8)}.exe";
                string fullPath = Path.Combine(tempPath, fileName);

                using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }

                // Executa o instalador (sem modo silencioso, usuário avança normalmente)
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true
                });

                // Fecha a aplicação atual para o instalador poder sobrescrever os arquivos
                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                {
                    System.Windows.Application.Current.Shutdown();
                });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Erro ao baixar a atualização: {ex.Message}", "Erro de Atualização", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
