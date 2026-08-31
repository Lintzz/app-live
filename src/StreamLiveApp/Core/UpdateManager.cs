using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace StreamLiveApp
{
    public static class UpdateManager
    {
        private static readonly string LatestReleaseUrl =
            $"https://api.github.com/repos/{AppInfo.RepositoryOwner}/{AppInfo.RepositoryName}/releases/latest";

        // Um HttpClient para todo o processo: um por chamada esgota portas efêmeras.
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            // O GitHub API exige um User-Agent
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StreamLiveApp", "1.0"));
            return client;
        }

        public class GitHubReleaseInfo
        {
            public string? tag_name { get; set; }
            public GitHubAsset[]? assets { get; set; }
        }

        public class GitHubAsset
        {
            public string? name { get; set; }
            public string? browser_download_url { get; set; }
        }

        public class UpdateCheckResult
        {
            public bool HasUpdate { get; set; }
            public string? LatestVersion { get; set; }
            public string? DownloadUrl { get; set; }

            /// <summary>URL do arquivo .sha256 publicado junto do instalador, quando existe.</summary>
            public string? ChecksumUrl { get; set; }
        }

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            try
            {
                var response = await Http.GetAsync(LatestReleaseUrl);
                if (!response.IsSuccessStatusCode)
                    return new UpdateCheckResult { HasUpdate = false };

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var releaseInfo = JsonSerializer.Deserialize<GitHubReleaseInfo>(jsonResponse);

                if (releaseInfo == null || string.IsNullOrEmpty(releaseInfo.tag_name))
                    return new UpdateCheckResult { HasUpdate = false };

                // O tag pode ter "v" no começo, ex: "v1.0.1"
                string latestVersionStr = releaseInfo.tag_name.TrimStart('v', 'V');

                if (Version.TryParse(latestVersionStr, out Version? latestVersion))
                {
                    Version? currentVersion = Assembly.GetExecutingAssembly().GetName().Version;

                    if (currentVersion != null && latestVersion > currentVersion)
                    {
                        var installerAsset = releaseInfo.assets?.FirstOrDefault(a =>
                            a.name != null && a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                        if (installerAsset?.browser_download_url != null)
                        {
                            var checksumAsset = releaseInfo.assets?.FirstOrDefault(a =>
                                a.name != null && a.name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));

                            return new UpdateCheckResult
                            {
                                HasUpdate = true,
                                LatestVersion = releaseInfo.tag_name,
                                DownloadUrl = installerAsset.browser_download_url,
                                ChecksumUrl = checksumAsset?.browser_download_url
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

        /// <summary>
        /// Baixa o instalador e só executa depois de conferir o SHA-256 publicado na release.
        /// Antes o .exe era executado direto: um download corrompido (ou trocado) rodava com
        /// privilégio de instalação sem nenhuma checagem.
        /// </summary>
        public static async Task DownloadAndInstallUpdateAsync(string downloadUrl, string? checksumUrl)
        {
            string? fullPath = null;
            try
            {
                var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                string tempPath = Path.GetTempPath();
                string fileName = $"StreamLive_Setup_Update_{Guid.NewGuid().ToString().Substring(0, 8)}.exe";
                fullPath = Path.Combine(tempPath, fileName);

                using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }

                if (!await VerifyDownloadAsync(fullPath, checksumUrl))
                {
                    TryDelete(fullPath);
                    return;
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
                if (fullPath != null) TryDelete(fullPath);
                System.Windows.MessageBox.Show($"Erro ao baixar a atualização: {ex.Message}", "Erro de Atualização", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Confere o hash quando a release publica um .sha256. Sem esse arquivo não há como
        /// validar o download, então pedimos confirmação explícita em vez de executar calado.
        /// </summary>
        private static async Task<bool> VerifyDownloadAsync(string filePath, string? checksumUrl)
        {
            if (string.IsNullOrEmpty(checksumUrl))
            {
                var choice = System.Windows.MessageBox.Show(
                    "Esta versão não publicou um arquivo de verificação (.sha256), então não é " +
                    "possível confirmar que o instalador baixado é autêntico.\n\nDeseja executá-lo mesmo assim?",
                    "Atualização não verificada",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                return choice == MessageBoxResult.Yes;
            }

            try
            {
                var expectedRaw = await Http.GetStringAsync(checksumUrl);

                // Formato usual do sha256sum: "<hash>  <nome do arquivo>".
                var expected = expectedRaw.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;

                string actual;
                using (var stream = File.OpenRead(filePath))
                {
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                }

                if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return true;

                System.Windows.MessageBox.Show(
                    "O instalador baixado não confere com o hash publicado na release e foi descartado.\n\n" +
                    $"Esperado: {expected}\nObtido:   {actual}",
                    "Atualização rejeitada", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Não foi possível verificar o instalador baixado: {ex.Message}\n\nA atualização foi cancelada.",
                    "Atualização rejeitada", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
