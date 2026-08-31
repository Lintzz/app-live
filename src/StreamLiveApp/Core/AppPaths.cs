using System;
using System.IO;
using System.Threading;

namespace StreamLiveApp
{
    /// <summary>
    /// Pasta de dados do app em %LOCALAPPDATA% — onde vivem friends.json, settings.json,
    /// error.log e audio_error.log. Ficou centralizada aqui por causa do rename do app: a
    /// pasta mudou de nome e a migração dos dados antigos precisa rodar uma vez só, antes de
    /// qualquer leitura ou gravação. Com o caminho repetido em cinco arquivos, bastava um
    /// deles criar a pasta nova primeiro para a migração nunca acontecer.
    /// </summary>
    public static class AppPaths
    {
        private const string FolderName = "StreamLiveApp";

        // Nome da pasta antes do rename. Existe só para a migração abaixo: sem ela, quem já
        // usava a versão anterior abriria o app com a lista de amigos e as configurações
        // zeradas. Pode sair quando não houver mais instalação antiga em campo.
        private const string LegacyFolderName = "RadminStreamApp";

        private static readonly Lazy<string> DataDirectoryLazy =
            new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Pasta de dados, já criada e já migrada da versão anterior.</summary>
        public static string DataDirectory => DataDirectoryLazy.Value;

        /// <summary>Caminho de um arquivo dentro da pasta de dados.</summary>
        public static string GetFilePath(string fileName) => Path.Combine(DataDirectory, fileName);

        private static string Resolve()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, FolderName);

            try
            {
                // Só migra quando a pasta nova ainda não existe: uma vez migrada (ou criada
                // do zero numa instalação nova), a pasta antiga deixa de ser consultada.
                var legacy = Path.Combine(localAppData, LegacyFolderName);
                if (!Directory.Exists(dir) && Directory.Exists(legacy))
                {
                    Directory.Move(legacy, dir);
                }
            }
            catch
            {
                // Migração é conveniência, não requisito: se o Move falhar (pasta em uso,
                // permissão), o app segue com a pasta nova vazia em vez de não abrir.
            }

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // Quem chama já trata a falha de leitura/gravação que vem depois.
            }

            return dir;
        }
    }
}
