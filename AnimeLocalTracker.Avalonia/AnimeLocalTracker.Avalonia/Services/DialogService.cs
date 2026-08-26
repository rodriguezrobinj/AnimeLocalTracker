using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Services;
using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Avalonia.Services;

/// <summary>
/// Implementación real del diálogo (portada del WPF): envía un
/// MostrarDialogoRequestMessage al MainViewModel, que muestra el overlay
/// con DialogoVisible/DialogoTitulo/DialogoMensaje y responde por TCS.
/// </summary>
public class DialogService : IDialogService
{
    public async Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5")
    {
        try
        {
            var message = new MostrarDialogoRequestMessage(titulo, mensaje, esConfirmacion, icono, color);
            return await WeakReferenceMessenger.Default.Send(message);
        }
        catch (Exception ex)
        {
            AppLogger.Error("DialogService", $"Error mostrando diálogo '{titulo}'", ex);
            return !esConfirmacion;
        }
    }

    public string? SeleccionarCarpeta(string titulo, string rutaInicial = "")
    {
        // Selector de carpeta nativo de Avalonia (StorageProvider de la ventana principal)
        try
        {
            var topLevel = GetMainWindow();
            if (topLevel == null) return null;

            var task = topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = titulo,
                AllowMultiple = false
            });

            var result = task.GetAwaiter().GetResult();
            if (result != null && result.Count > 0)
            {
                return result[0].Path.LocalPath;
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("DialogService", $"Error seleccionando carpeta: {ex.Message}", ex);
            return null;
        }
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window window)
        {
            return window;
        }
        return null;
    }
}
