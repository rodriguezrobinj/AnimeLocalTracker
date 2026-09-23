namespace AnimeLocalTracker.Services;

/// <summary>Gestiona el arranque automático de AnimeLocalTracker con Windows (registro
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run). Ver <see cref="StartupService"/>.</summary>
public interface IStartupService
{
    /// <summary>El registro es la fuente de verdad (no un campo aparte en AppSettings): así, si el
    /// usuario lo desactiva desde el Administrador de tareas de Windows, la app no cree erróneamente
    /// que sigue activo.</summary>
    bool EstaHabilitado();

    /// <summary>Crea o borra la entrada del registro según <paramref name="habilitado"/>.</summary>
    void Sincronizar(bool habilitado);
}
