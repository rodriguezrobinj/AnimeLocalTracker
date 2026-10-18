using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Torrents;

namespace AnimeLocalTracker.Views;

/// <summary>Arrastrar, Ctrl+V y el botón de Descargas: todas llevan al cuadro "Descargar torrent".</summary>
internal static class EntradaTorrent
{
    public static bool EsAceptable(IDataObject datos) => ArchivosTorrent(datos).Length > 0 || ImportadorTorrent.PareceMagnet(Texto(datos));

    public static async Task SoltarAsync(IImportarTorrentService servicio, IDataObject datos)
    {
        var archivos = ArchivosTorrent(datos);
        if (archivos.Length > 0) await servicio.ImportarArchivoAsync(archivos[0], archivos.Length - 1);
        else if (Texto(datos) is { } texto && ImportadorTorrent.PareceMagnet(texto)) await servicio.ImportarMagnetAsync(texto.Trim());
    }

    /// <summary>El magnet del portapapeles, o null. El portapapeles puede estar bloqueado por otro programa.</summary>
    public static string? MagnetDelPortapapeles()
    {
        try
        {
            string texto = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            return ImportadorTorrent.PareceMagnet(texto) ? texto.Trim() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            AppLogger.Debug("EntradaTorrent", $"No se pudo leer el portapapeles: {ex.Message}");
            return null;
        }
    }

    /// <summary>Ctrl+V solo es nuestro si el foco no está donde se escribe.</summary>
    public static bool FocoEnCajaDeTexto() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox || Keyboard.FocusedElement is ComboBox { IsEditable: true };

    /// <summary>Botón de Descargas: el magnet copiado si lo hay; si no, elegir un archivo .torrent.</summary>
    public static async Task PortapapelesOArchivoAsync(IImportarTorrentService servicio)
    {
        if (MagnetDelPortapapeles() is { } magnet)
        {
            await servicio.ImportarMagnetAsync(magnet);
            return;
        }
        var dialogo = new Microsoft.Win32.OpenFileDialog { Filter = LocalizationService.T("Desc_ImportarFiltro"), Multiselect = false };
        if (dialogo.ShowDialog() == true) await servicio.ImportarArchivoAsync(dialogo.FileName);
    }

    private static string[] ArchivosTorrent(IDataObject datos) =>
        datos.GetDataPresent(DataFormats.FileDrop) && datos.GetData(DataFormats.FileDrop) is string[] archivos
            ? archivos.Where(a => string.Equals(Path.GetExtension(a), ".torrent", StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

    private static string? Texto(IDataObject datos) =>
        datos.GetDataPresent(DataFormats.UnicodeText) ? datos.GetData(DataFormats.UnicodeText) as string
        : datos.GetDataPresent(DataFormats.Text) ? datos.GetData(DataFormats.Text) as string
        : null;
}
