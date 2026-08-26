using AnimeLocalTracker.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Core.ViewModels;

public partial class DescargasViewModel : ObservableObject, IRecipient<DescargaProgresoMensaje>
{
    private readonly IDownloadService _downloadService;

    public ObservableCollection<DescargaItem> ColaDescargas { get; } = [];

    [ObservableProperty]
    private int _conteoActivas;

    [ObservableProperty]
    private bool _tieneDescargas;

    [ObservableProperty]
    private bool _todasPausadas;

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
        AnimeLocalTracker.Core.Services.CoreDispatcher.Invoke(() =>
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
                item.IsPaused = message.IsPaused;
                item.RutaArchivo = message.RutaArchivo;
                item.Error = message.Error;

                if (message.IsCompleted)
                {
                    // Remover de la cola de activas después de un pequeño retraso
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            await System.Threading.Tasks.Task.Delay(1500);
                            AnimeLocalTracker.Core.Services.CoreDispatcher.Invoke(() =>
                            {
                                ColaDescargas.Remove(item);
                                ActualizarConteo();
                            });
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("DescargasViewModel", $"Error al remover descarga completada: {ex.Message}");
                        }
                    });
                }
                else if (!string.IsNullOrEmpty(message.Error))
                {
                    ColaDescargas.Remove(item);
                    ActualizarConteo();
                }
                else
                {
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
                    IsCompleted = false,
                    IsPaused = message.IsPaused
                };
                ColaDescargas.Add(nuevo);
                ActualizarConteo();
            }
        });
    }

    private void ActualizarConteo()
    {
        ConteoActivas = ColaDescargas.Count(d => d.IsDownloading && !d.IsPaused);
        TieneDescargas = ColaDescargas.Count > 0;
        TodasPausadas = ColaDescargas.Count > 0 && ColaDescargas.All(d => d.IsPaused);
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
    private void AlternarPausaDescarga(DescargaItem item)
    {
        if (item == null) return;
        if (item.IsPaused)
        {
            item.IsPaused = false;
            _downloadService.ReanudarDescarga(item.AniListId, item.NumeroEpisodio);
        }
        else
        {
            item.IsPaused = true;
            _downloadService.PausarDescarga(item.AniListId, item.NumeroEpisodio);
        }
        ActualizarConteo();
    }

    [RelayCommand]
    private void AlternarPausaTodas()
    {
        bool pausar = ColaDescargas.Any(d => !d.IsPaused);
        if (pausar)
        {
            foreach (var d in ColaDescargas)
            {
                d.IsPaused = true;
            }
            _downloadService.PausarTodas();
        }
        else
        {
            foreach (var d in ColaDescargas)
            {
                d.IsPaused = false;
            }
            _downloadService.ReanudarTodas();
        }
        ActualizarConteo();
    }

    [RelayCommand]
    private void CancelarTodas()
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
