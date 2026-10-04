using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Texto traducido en XAML: <c>{loc:T Clave}</c>. Equivale a
/// <c>{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}, Mode=OneWay}</c>, que antes se escribía entero
/// en cada sitio. Al ser el mismo binding, el texto cambia solo al cambiar de idioma; y al ir siempre en un sentido cumple
/// por construcción la regla 1 de wpf-mvvm.md (un binding de doble sentido al indexador falla en ejecución, p. ej. en Run.Text).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string clave) => Clave = clave;

    /// <summary>Clave del diccionario de <see cref="LocalizationService"/>.</summary>
    [ConstructorArgument("clave")]
    public string Clave { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Clave}]") { Source = LocalizationService.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
