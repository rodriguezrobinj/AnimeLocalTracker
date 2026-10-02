using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Views;

/// <summary>
/// La ficha está repartida en piezas (<c>Episodios</c>, <c>Musica</c>, <c>Seguimiento</c>) y sus vistas las alcanzan por rutas
/// escritas a mano ("DataContext.Episodios.ReproducirEpisodioCommand"). Un enlace con la ruta mal no da error al compilar ni al
/// ejecutar: el botón simplemente deja de hacer nada. Estas pruebas comprueban que cada ruta existe en su clase.
/// </summary>
public class EnlacesDeLaFichaTests
{
    private static string LeerVista(string nombre)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidato = Path.Combine(dir.FullName, "AnimeLocalTracker", "Views", nombre);
            if (File.Exists(candidato)) return File.ReadAllText(candidato);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("No se encontró la vista " + nombre);
    }

    private static bool Existe(Type tipo, string miembro) =>
        tipo.GetProperty(miembro, BindingFlags.Public | BindingFlags.Instance) != null;

    /// <summary>Rutas "prefijo.A.B": devuelve las que no existen recorriendo las propiedades desde <paramref name="raiz"/>.</summary>
    private static List<string> RutasRotas(string xaml, string prefijo, Type raiz)
    {
        var rotas = new List<string>();
        foreach (Match m in Regex.Matches(xaml, Regex.Escape(prefijo) + @"((?:\.[A-Za-z_]\w*)+)"))
        {
            Type? actual = raiz;
            foreach (string tramo in m.Groups[1].Value.TrimStart('.').Split('.'))
            {
                var propiedad = actual?.GetProperty(tramo, BindingFlags.Public | BindingFlags.Instance);
                if (propiedad == null)
                {
                    rotas.Add(m.Value);
                    break;
                }

                // Más allá de una colección o de un tipo de fuera de la app (Count, Titulo de un modelo…) no se sigue.
                actual = propiedad.PropertyType;
                if (actual.Namespace?.StartsWith("AnimeLocalTracker.ViewModels", StringComparison.Ordinal) != true || actual.IsGenericType) break;
            }
        }
        return rotas.Distinct().ToList();
    }

    [Fact]
    public void Ficha_LasRutasHaciaSusPiezas_Existen()
    {
        string xaml = LeerVista("DetalleView.xaml");

        RutasRotas(xaml, "{Binding Episodios", typeof(EpisodiosFichaViewModel)).Should().BeEmpty();
        RutasRotas(xaml, "{Binding Musica", typeof(MusicaFichaViewModel)).Should().BeEmpty();
        RutasRotas(xaml, "{Binding Seguimiento", typeof(SeguimientoEditorViewModel)).Should().BeEmpty();
        RutasRotas(xaml, "Path=\"Episodios", typeof(EpisodiosFichaViewModel)).Should().BeEmpty();

        // Desde las filas y los menús, que llegan a la ficha por el control o por el Tag de la fila.
        RutasRotas(xaml, "{Binding DataContext", typeof(DetalleViewModel)).Should().BeEmpty();
        RutasRotas(xaml, "PlacementTarget.Tag", typeof(DetalleViewModel)).Should().BeEmpty();
    }

    [Fact]
    public void Ficha_UsaSusTresPiezas()
    {
        // Si el detector dejara de encontrar rutas, la prueba anterior pasaría en vacío.
        string xaml = LeerVista("DetalleView.xaml");

        Regex.Matches(xaml, @"DataContext\.Episodios\.\w+Command").Count.Should().BeGreaterThan(3);
        Regex.Matches(xaml, @"PlacementTarget\.Tag\.Episodios\.\w+Command").Count.Should().BeGreaterThan(3);
        xaml.Should().Contain("{Binding Episodios.EpisodiosDelAnime}").And.Contain("{Binding Musica.ToggleMusicaCommand}")
            .And.Contain("{Binding Seguimiento.AbrirEditorSeguimientoCommand}");
    }

    [Fact]
    public void VentanasDeLaFicha_LosComandosDesdeSusFilas_Existen()
    {
        RutasRotas(LeerVista("PanelMusicaView.xaml"), "{Binding DataContext", typeof(MusicaFichaViewModel)).Should().BeEmpty();
        RutasRotas(LeerVista("EditorSeguimientoView.xaml"), "{Binding DataContext", typeof(SeguimientoEditorViewModel)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{Binding DataContext.Episodios.NoExisteCommand, RelativeSource=...}", "{Binding DataContext", 1)]
    [InlineData("{Binding DataContext.Episodios.ReproducirEpisodioCommand, RelativeSource=...}", "{Binding DataContext", 0)]
    [InlineData("{Binding DataContext.ReproducirEpisodioCommand}", "{Binding DataContext", 1)] // ya no está en la ficha
    public void ElDetector_DistingueUnaRutaRotaDeUnaBuena(string xaml, string prefijo, int rotasEsperadas)
    {
        RutasRotas(xaml, prefijo, typeof(DetalleViewModel)).Should().HaveCount(rotasEsperadas);
    }

    [Fact]
    public void Existe_SoloVePropiedadesPublicas()
    {
        Existe(typeof(EpisodiosFichaViewModel), nameof(EpisodiosFichaViewModel.EpisodiosDelAnime)).Should().BeTrue();
        Existe(typeof(EpisodiosFichaViewModel), "NoExiste").Should().BeFalse();
    }
}
