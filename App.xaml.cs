using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RadminStreamApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadminStreamApp", "error.log");

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

    private static void Log(string origin, Exception ex)
    {
        if (ex == null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{origin}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
