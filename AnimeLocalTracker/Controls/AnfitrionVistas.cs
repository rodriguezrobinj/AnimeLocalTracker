using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AnimeLocalTracker.ViewModels;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Zona de contenido que muestra una vista según su ViewModel (por las DataTemplates VM→Vista) SIN reconstruirla en cada
/// cambio. Un ContentControl tira la vista anterior y construye la nueva desde cero; aquí cada vista se construye una vez y
/// después solo se alterna cuál se ve (medido: de 130-440 ms por cambio de pestaña a unos pocos ms), conservando su
/// desplazamiento. Se usa en la ventana principal (pestañas) y dentro de los minijuegos (un juego u otro).
/// <para>
/// Hay una vista por ViewModel (las pestañas son únicas) salvo para los que implementan <see cref="IVistaReutilizable"/>,
/// que se crean nuevos en cada visita: de esos hay una vista por TIPO, que pasa de un ViewModel al siguiente.
/// </para>
/// <para>
/// Una vista conservada NO recibe Loaded/Unloaded al entrar o salir: lo que deba pasar ahí va en IsVisibleChanged (o en
/// DataContextChanged si es reutilizable).
/// </para>
/// </summary>
public sealed class AnfitrionVistas : Grid
{
    public static readonly DependencyProperty VistaProperty = DependencyProperty.Register(
        nameof(Vista), typeof(object), typeof(AnfitrionVistas),
        new PropertyMetadata(null, (d, e) => ((AnfitrionVistas)d).Mostrar(e.NewValue)));

    /// <summary>ViewModel de la vista que se muestra (null = ninguna).</summary>
    public object? Vista
    {
        get => GetValue(VistaProperty);
        set => SetValue(VistaProperty, value);
    }

    private readonly Dictionary<object, ContentPresenter> _porViewModel = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, ContentPresenter> _porTipo = new();
    private ContentPresenter? _actual;

    private void Mostrar(object? vista)
    {
        var anterior = _actual;
        ContentPresenter? siguiente = null;

        if (vista != null)
        {
            siguiente = Obtener(vista, out _);
            siguiente.Visibility = Visibility.Visible;
        }

        if (anterior != null && !ReferenceEquals(anterior, siguiente)) anterior.Visibility = Visibility.Collapsed;
        _actual = siguiente;
    }

    private ContentPresenter Obtener(object vista, out bool creada)
    {
        creada = false;
        if (vista is IVistaReutilizable)
        {
            if (_porTipo.TryGetValue(vista.GetType(), out var reutilizada))
            {
                // Mismo tipo, misma plantilla: WPF conserva la vista y solo le cambia el DataContext.
                reutilizada.Content = vista;
                return reutilizada;
            }
        }
        else if (_porViewModel.TryGetValue(vista, out var existente))
        {
            return existente;
        }

        creada = true;
        var nueva = new ContentPresenter { Content = vista };
        if (vista is IVistaReutilizable) _porTipo[vista.GetType()] = nueva;
        else _porViewModel[vista] = nueva;
        Children.Add(nueva);
        return nueva;
    }

    private bool YaExiste(object vista) =>
        vista is IVistaReutilizable ? _porTipo.ContainsKey(vista.GetType()) : _porViewModel.ContainsKey(vista);

    /// <summary>
    /// Construye por adelantado una vista aún no creada, sin mostrarla: se mide y se coloca (Hidden) para que la primera
    /// visita ya la encuentre hecha. Devuelve false si ya existía (no se toca).
    /// </summary>
    public bool Precalentar(object vista)
    {
        if (YaExiste(vista)) return false;

        var presentador = Obtener(vista, out _);
        presentador.Visibility = Visibility.Hidden;
        UpdateLayout();
        // Entre tanto pudo navegarse a ella: solo se oculta del todo si sigue sin ser la que está en pantalla.
        if (!ReferenceEquals(presentador, _actual)) presentador.Visibility = Visibility.Collapsed;
        return true;
    }
}
