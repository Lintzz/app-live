using System;
using System.Reflection;

namespace RadminStreamApp
{
    /// <summary>
    /// Identidade do app em um lugar só. A versão vem do assembly (que por sua vez vem de
    /// &lt;Version&gt; no .csproj), em vez de ficar repetida à mão no XAML e no setup.iss.
    /// </summary>
    public static class AppInfo
    {
        public const string RepositoryOwner = "Lintzz";
        // O repositório foi renomeado de "app-live" para este nome. O GitHub redireciona o
        // nome antigo, mas a API de releases (usada pelo auto-update) fica mais estável
        // apontando direto para o atual.
        public const string RepositoryName = "radmin-stream-live";
        public const string RepositoryUrl = "https://github.com/" + RepositoryOwner + "/" + RepositoryName;

        public static string Version { get; } = ReadVersion();

        private static string ReadVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
