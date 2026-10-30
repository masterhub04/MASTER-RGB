using System.Windows;
using Velopack;

namespace MasterRgb;

public partial class App : Application
{
    public App()
    {
        // Deve executar antes da inicialização da interface para tratar instalação,
        // atualização e desinstalação do Velopack sem carregar a janela WPF.
        VelopackApp.Build().Run();
    }
}