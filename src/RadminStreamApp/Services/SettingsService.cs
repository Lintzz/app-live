using System;
using System.IO;
using System.Text.Json;

namespace RadminStreamApp.Services
{
    /// <summary>Preferências do app que precisam sobreviver ao fechamento da janela.</summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// Programa cujo áudio fica FORA da transmissão. O padrão é o Discord: sem isso a
        /// mesa inteira se escuta, porque a voz dos outros sai pelos seus alto-falantes,
        /// o loopback recaptura e volta para eles.
        /// </summary>
        public string ExcludedAudioProcessName { get; set; } = DefaultExcludedAudioProcessName;

        /// <summary>Escolha de fábrica, aplicada quando ainda não há arquivo de configuração.</summary>
        public const string DefaultExcludedAudioProcessName = "Discord";

        // As três opções abaixo viviam só nos CheckBox do XAML: valiam para a sessão e
        // voltavam ao padrão a cada abertura. O caso que doía era o GDI — quem precisa dele
        // precisa dele toda vez, e tinha de redescobrir a opção depois de cada reinício.

        /// <summary>
        /// Modo leve: preset de encoder mais rápido, escala por vizinho mais próximo e
        /// prioridade de processo abaixo do normal. Ligado de fábrica, como sempre esteve.
        /// </summary>
        public bool LightweightMode { get; set; } = true;

        /// <summary>Só IPs da lista de amigos conseguem conectar.</summary>
        public bool RestrictToFriends { get; set; } = true;

        /// <summary>Força o caminho GDI de captura, ignorando o Desktop Duplication.</summary>
        public bool ForceGdiCapture { get; set; }
    }

    /// <summary>
    /// Lê e grava as preferências em <c>settings.json</c>, ao lado do <c>friends.json</c>.
    ///
    /// Antes a escolha do áudio excluído só existia em memória: valia para a sessão e sumia
    /// ao fechar o app, então toda abertura voltava a "capturar todo o áudio".
    /// </summary>
    public static class SettingsService
    {
        /// <summary>Disparado quando ler ou gravar falha, no mesmo espírito do FriendsService.</summary>
        public static event Action<string>? OnPersistenceError;

        private static string GetFilePath()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadminStreamApp");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "settings.json");
        }

        public static AppSettings Load()
        {
            try
            {
                var file = GetFilePath();
                if (File.Exists(file))
                {
                    var json = File.ReadAllText(file);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                OnPersistenceError?.Invoke($"Não foi possível carregar as configurações: {ex.Message}");
            }

            // Sem arquivo (primeira execução) as preferências de fábrica valem — inclusive a
            // exclusão do Discord, que é o comportamento essencial e não pode depender de o
            // usuário achar a opção nas configurações.
            return new AppSettings();
        }

        /// <summary>
        /// Grava num arquivo temporário e só então substitui o definitivo: uma falha no meio
        /// da escrita não deixa o settings.json truncado.
        /// </summary>
        public static bool Save(AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var file = GetFilePath();
            var temp = file + ".tmp";

            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(temp, json);
                File.Move(temp, file, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                OnPersistenceError?.Invoke($"Não foi possível salvar as configurações: {ex.Message}");
                return false;
            }
        }
    }
}
