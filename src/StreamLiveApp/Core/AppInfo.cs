using System;
using System.Reflection;

namespace StreamLiveApp
{
    /// <summary>
    /// Identidade do app em um lugar só. A versão vem do assembly (que por sua vez vem de
    /// &lt;Version&gt; no .csproj), em vez de ficar repetida à mão no XAML e no setup.iss.
    /// </summary>
    public static class AppInfo
    {
        public const string RepositoryOwner = "Lintzz";
        // O repositório já foi renomeado mais de uma vez até chegar aqui. O GitHub redireciona
        // os nomes antigos, mas a API de releases (usada pelo auto-update) fica mais estável
        // apontando direto para o atual — ao renomear de novo, atualize esta constante no
        // mesmo commit, senão o auto-update passa a consultar um repositório que não existe.
        public const string RepositoryName = "stream-live";
        public const string RepositoryUrl = "https://github.com/" + RepositoryOwner + "/" + RepositoryName;

        public static string Version { get; } = ReadVersion();

        private static string ReadVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
