using System.Windows;
using Application = System.Windows.Application;
using Velopack;
using MasterRgb.Services;

namespace MasterRgb;

public partial class App : Application
{
    public App()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 4 && args[1] == "--masterhub-ai-policy")
        {
            int code = 1;
            try
            {
                if (args[3] is not ("disable" or "restore")) throw new ArgumentException("Operação inválida.");
                new OptimizeSystemService().SetAiOption(args[2], args[3] == "disable");
                code = 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Falha na política de IA: " + ex);
            }
            Environment.Exit(code);
        }
        // Deve executar antes da inicialização da interface para tratar instalação,
        // atualização e desinstalação do Velopack sem carregar a janela WPF.
        VelopackApp.Build().Run();
    }
}