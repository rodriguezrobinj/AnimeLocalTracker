namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Marca los ViewModels que se crean nuevos en cada visita (la ficha de un anime). Su vista no se construye cada vez: hay
/// UNA por tipo de ViewModel y pasa de un ViewModel al siguiente (ver <see cref="Controls.AnfitrionVistas"/>). La vista
/// recibe el cambio por <c>DataContextChanged</c> y ahí debe dejar su estado propio (desplazamiento, menús abiertos,
/// cajas de texto) como recién abierta. Mientras está oculta sigue enlazada al último ViewModel que mostró.
/// </summary>
public interface IVistaReutilizable
{
}
