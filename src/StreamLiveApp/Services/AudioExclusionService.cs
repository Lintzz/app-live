using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace StreamLiveApp.Services
{
    /// <summary>Um programa que pode ser excluído da captura de áudio.</summary>
    public sealed class AudioExclusionOption
    {
        /// <summary>Nome do processo. Vazio significa "não excluir nada".</summary>
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Descobre quais programas podem ter o áudio excluído da transmissão e resolve a escolha
    /// para um PID. A escolha é guardada pelo NOME do processo, não pelo PID: entre uma
    /// transmissão e outra o programa pode ter sido reaberto com outro identificador.
    /// </summary>
    public static class AudioExclusionService
    {
        public const string NoneDisplayName = "Nenhum (capturar todo o áudio)";

        /// <summary>Lista os programas com janela, com a escolha atual garantida na lista.</summary>
        public static List<AudioExclusionOption> ListOptions(string? currentSelection)
        {
            var options = new List<AudioExclusionOption>
            {
                new AudioExclusionOption { Name = string.Empty, DisplayName = NoneDisplayName }
            };

            try
            {
                var running = Process.GetProcesses()
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.ProcessName))
                    .Select(p => p.ProcessName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

                foreach (var name in running)
                {
                    options.Add(new AudioExclusionOption { Name = name, DisplayName = name });
                }
            }
            catch { }

            // Mantém a escolha atual mesmo que o programa não esteja rodando agora.
            if (!string.IsNullOrEmpty(currentSelection) &&
                !options.Any(o => string.Equals(o.Name, currentSelection, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new AudioExclusionOption
                {
                    Name = currentSelection,
                    DisplayName = currentSelection + " (não está em execução)"
                });
            }

            return options;
        }

        /// <summary>
        /// Traduz o nome escolhido para um PID. Devolve 0 (capturar tudo) quando o programa
        /// não está rodando — quem chama avisa o usuário, em vez de o isolamento falhar calado.
        /// </summary>
        public static uint ResolvePid(string? processName)
        {
            if (string.IsNullOrEmpty(processName)) return 0;

            try
            {
                var processes = Process.GetProcessesByName(processName);
                var withWindow = processes.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                var target = withWindow ?? processes.FirstOrDefault();
                return target == null ? 0u : (uint)target.Id;
            }
            catch
            {
                return 0;
            }
        }
    }
}
