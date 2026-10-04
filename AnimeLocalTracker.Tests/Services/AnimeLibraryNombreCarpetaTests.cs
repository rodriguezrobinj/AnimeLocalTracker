using System.IO;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>El título de un anime llega de fuera y acaba siendo el nombre de una carpeta: nunca puede salirse de la biblioteca.</summary>
public class AnimeLibraryNombreCarpetaTests
{
    [Theory]
    [InlineData("Sousou no Frieren", "Sousou no Frieren")]
    [InlineData("Re:Zero kara Hajimeru Isekai Seikatsu", "Re_Zero kara Hajimeru Isekai Seikatsu")]
    [InlineData("Fate/stay night", "Fate_stay night")]
    [InlineData("Gintama.", "Gintama")]                 // Windows recorta el punto final: la ruta guardada debe coincidir
    [InlineData("  Bleach  ", "Bleach")]
    [InlineData("..", "Anime 42")]
    [InlineData(".", "Anime 42")]
    [InlineData(" . . ", "Anime 42")]
    [InlineData("???", "___")]
    [InlineData("NUL", "_NUL")]
    [InlineData("con", "_con")]
    [InlineData("COM1.5", "_COM1.5")]
    [InlineData("Conan", "Conan")]
    public void NombreDeCarpeta_NuncaSaleDeLaBibliotecaNiChocaConWindows(string titulo, string esperado)
    {
        string nombre = AnimeLibraryService.NombreDeCarpeta(titulo, 42);

        nombre.Should().Be(esperado);
        Path.GetFullPath(Path.Combine(@"D:\Anime", nombre)).Should().StartWith(@"D:\Anime\", "la carpeta del anime siempre queda DENTRO de la biblioteca");
    }
}
