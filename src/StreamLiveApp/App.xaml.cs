using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace StreamLiveApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly Lazy<string> LogPathLazy = new(() => AppPaths.GetFilePath("error.log"));

    private static string LogPath => LogPathLazy.Value;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Sem isso, uma exceção na thread de UI fecha a janela em silêncio e deixa o
        // processo vivo em segundo plano — o usuário só vê o app "sumir".
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            Log("AppDomain", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log("Task", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);

        System.Windows.MessageBox.Show(
            "Ocorreu um erro inesperado, mas o app continua funcionando.\n\n" +
            e.Exception.Message + "\n\nDetalhes em:\n" + LogPath,
            "Erro", MessageBoxButton.OK, MessageBoxImage.Warning);

        // Mantém o app de pé: o erro não deve derrubar a transmissão nem as lives abertas.
        e.Handled = true;
    }

    /// <summary>
    /// Fechar uma conexao cancela recepcoes pendentes: o socket lanca 995 (operacao abortada).
    /// E esperado ao encerrar uma live e so polui o log.
    /// </summary>
    private static bool IsExpectedSocketAbort(Exception ex)
    {
        foreach (var inner in Flatten(ex))
        {
            if (inner is System.Net.Sockets.SocketException se && se.ErrorCode == 995) return true;
        }
        return false;
    }

    private static System.Collections.Generic.IEnumerable<Exception> Flatten(Exception? ex)
    {
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions) yield return inner;
            yield break;
        }

        while (ex != null) { yield return ex; ex = ex.InnerException; }
    }

    private static void Log(string origin, Exception? ex)
    {
        if (ex == null) return;
        if (IsExpectedSocketAbort(ex)) return;

        try
        {
            var logDir = Path.GetDirectoryName(LogPath);
            if (logDir != null) Directory.CreateDirectory(logDir);
            File.AppendAllText(LogPath,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{origin}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
