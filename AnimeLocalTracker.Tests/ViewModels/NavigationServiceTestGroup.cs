using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Toda clase de test que construya NavigationService se registra en WeakReferenceMessenger.Default
/// (mensajero estático de proceso). Si dos de esas clases corren en paralelo, un mensaje enviado
/// por una es recibido también por la NavigationService de la otra, que puede tener un
/// IServiceProvider de prueba distinto (sin todos los ViewModels registrados) y lanzar una
/// excepción. Esta colección desactiva la paralelización SOLO entre esas clases — el resto de la
/// suite sigue corriendo en paralelo sin cambios.
/// </summary>
[CollectionDefinition("NavigationServiceTests", DisableParallelization = true)]
public class NavigationServiceTestGroup
{
}
