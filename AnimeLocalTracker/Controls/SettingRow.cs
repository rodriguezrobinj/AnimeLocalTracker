using System.Windows;
using System.Windows.Controls;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Fila de ajuste de la pestaña Configuración: título y descripción a la izquierda y, a la derecha,
/// el control que se coloque como contenido (switch, combo, botón…). La plantilla visual vive en
/// <c>ConfiguracionView.xaml</c> para que todas las filas compartan el mismo alto, tipografía y márgenes.
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
