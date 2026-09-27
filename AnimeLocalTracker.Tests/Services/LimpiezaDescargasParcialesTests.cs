using System;
using System.IO;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Regresión: los escáneres de carpetas borraban el parcial de una descarga en pausa (o esperando
/// un reintento) al abrir la ficha; al reanudar, el .state huérfano daba por descargados trozos
/// que ahora eran ceros y el episodio terminaba corrupto.
/// </summary>
public class LimpiezaDescargasParcialesTests : IDisposable
{
    private readonly DirectoryInfo _carpeta = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"limpieza_parciales_{Guid.NewGuid():N}"));

    public void Dispose()
    {
        try { _carpeta.Delete(recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string CrearParcial(string nombre, DateTime ultimaEscrituraUtc)
    {
        string ruta = Path.Combine(_carpeta.FullName, nombre);
        File.WriteAllBytes(ruta, new byte[] { 1, 2, 3 });
        File.WriteAllText(ruta + ".state", "{}");
        File.SetLastWriteTimeUtc(ruta, ultimaEscrituraUtc);
        return ruta;
    }

    [Fact]
    public void LimpiarAbandonados_ConParcialReciente_LoConservaJuntoASuEstado()
    {
        var ahora = DateTime.UtcNow;
        string parcial = CrearParcial("Episodio 05.mp4.downloading", ahora.AddHours(-3));

        LimpiezaDescargasParciales.LimpiarAbandonados(_carpeta, ahora);

        File.Exists(parcial).Should().BeTrue("una descarga en pausa debe poder reanudarse");
        File.Exists(parcial + ".state").Should().BeTrue();
    }

    [Fact]
    public void LimpiarAbandonados_ConParcialDeHaceMasDeUnaSemana_LoBorraConSuEstado()
    {
        var ahora = DateTime.UtcNow;
        string parcial = CrearParcial("Episodio 05.mp4.downloading", ahora - LimpiezaDescargasParciales.AntiguedadMinima - TimeSpan.FromHours(1));

        LimpiezaDescargasParciales.LimpiarAbandonados(_carpeta, ahora);

        File.Exists(parcial).Should().BeFalse();
        File.Exists(parcial + ".state").Should().BeFalse("un .state sin su parcial reanudaría sobre un archivo vacío");
    }

    [Fact]
    public void LimpiarAbandonados_NoTocaArchivosQueNoSonParcialesPropios()
    {
        var ahora = DateTime.UtcNow;
        string video = Path.Combine(_carpeta.FullName, "Episodio 05.mp4");
        File.WriteAllBytes(video, new byte[] { 1 });
        File.SetLastWriteTimeUtc(video, ahora.AddYears(-1));

        LimpiezaDescargasParciales.LimpiarAbandonados(_carpeta, ahora);

        File.Exists(video).Should().BeTrue();
    }
}
