using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

public partial class DescargasViewModel : ObservableObject, IRecipient<DescargaProgresoMensaje>
{
    private readonly IDownloadService _downloadService;

    public ObservableCollection<DescargaItem> ColaDescargas { get; } = [];

    [ObservableProperty]
    private int _conteoActivas;

    [ObservableProperty]
    private bool _tieneDescargas;

    public DescargasViewModel(IDownloadService downloadService)
    {
        _downloadService = downloadService;
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);

        CargarDescargas();
    }

    public void CargarDescargas()
    {
        ColaDescargas.Clear();
        var activas = _downloadService.ObtenerDescargasActivas();
        foreach (var d in activas)
        {
            ColaDescargas.Add(d);
        }
        ActualizarConteo();
    }

    public void Receive(DescargaProgresoMensaje message)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var item = ColaDescargas.FirstOrDefault(d => d.AniListId == message.AniListId && d.NumeroEpisodio == message.NumeroEpisodio);

            if (item != null)
            {
                if (!string.IsNullOrWhiteSpace(message.AnimeTitulo) && (string.IsNullOrEmpty(item.AnimeTitulo) || item.AnimeTitulo == "Descarga"))
                {
                    item.AnimeTitulo = message.AnimeTitulo;
                }
                item.Progreso = message.Progreso;
                item.IsDownloading = message.IsDownloading;
                item.IsCompleted = message.IsCompleted;
                item.RutaArchivo = message.RutaArchivo;
                item.Error = message.Error;

                if (message.IsCompleted)
                {
                    // Remover de la cola de activas después de un pequeño retraso
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(1500);
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            ColaDescargas.Remove(item);
                            ActualizarConteo();
                        });
                    });
                }
                else if (!string.IsNullOrEmpty(message.Error))
                {
                    ColaDescargas.Remove(item);
                    ActualizarConteo();
                }
            }
            else if (message.IsDownloading)
            {
                var nuevo = new DescargaItem
                {
                    AniListId = message.AniListId,
                    AnimeTitulo = !string.IsNullOrWhiteSpace(message.AnimeTitulo) ? message.AnimeTitulo : "Descarga",
                    NumeroEpisodio = message.NumeroEpisodio,
                    Progreso = message.Progreso,
                    IsDownloading = true,
                    IsCompleted = false
                };
                ColaDescargas.Add(nuevo);
                ActualizarConteo();
            }
        });
    }

    private void ActualizarConteo()
    {
        ConteoActivas = ColaDescargas.Count(d => d.IsDownloading);
        TieneDescargas = ColaDescargas.Count > 0;
    }

    [RelayCommand]
    private void CancelarDescarga(DescargaItem item)
    {
        if (item == null) return;
        _downloadService.CancelarDescarga(item.AniListId, item.NumeroEpisodio);
        ColaDescargas.Remove(item);
        ActualizarConteo();
    }

    [RelayCommand]
    private void PararTodas()
    {
        _downloadService.CancelarTodas();
        ColaDescargas.Clear();
        ActualizarConteo();
    }

    [RelayCommand]
    private void Volver()
    {
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
    }
}
