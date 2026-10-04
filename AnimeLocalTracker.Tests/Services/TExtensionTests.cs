using System.Windows.Data;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary><c>{loc:T Clave}</c> debe equivaler al binding de localización de siempre, y siempre de solo lectura.</summary>
public class TExtensionTests
{
    [Fact]
    public void ProvideValue_DevuelveElBindingDeLocalizacionUnidireccional()
    {
        // Sin proveedor de servicios (fuera de una carga de XAML) el binding se devuelve tal cual, sin aplicar.
        var valor = new TExtension("Player_Subtitulos").ProvideValue(null!);

        var binding = valor.Should().BeOfType<Binding>().Subject;
        binding.Path.Path.Should().Be("[Player_Subtitulos]");
        binding.Source.Should().BeSameAs(LocalizationService.Instance);
        binding.Mode.Should().Be(BindingMode.OneWay, "en propiedades de doble sentido por defecto (Run.Text) un binding al indexador falla en ejecución");
    }

    [Fact]
    public void ElBindingResuelveElTextoDeLaClave()
    {
        var binding = (Binding)new TExtension("Player_Subtitulos").ProvideValue(null!);

        // Mismo camino que sigue WPF: el indexador de LocalizationService con la clave.
        LocalizationService.Instance["Player_Subtitulos"].Should().Be(LocalizationService.T("Player_Subtitulos"));
        binding.Path.Path.Should().Be("[Player_Subtitulos]");
    }
}
