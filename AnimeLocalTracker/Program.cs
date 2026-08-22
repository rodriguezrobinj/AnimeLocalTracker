using System;
using Velopack;

namespace AnimeLocalTracker;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Inicializar los hooks de Velopack (atajos de escritorio, inicio, actualizaciones)
            // Si la aplicación se inició con un parámetro de instalador/actualizador, Run() terminará el proceso aquí.
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Velopack] Error en arranque de VelopackApp: {ex.Message}");
        }

        // Iniciar la aplicación WPF normal
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
