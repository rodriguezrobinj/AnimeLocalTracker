using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using Velopack;

namespace AnimeLocalTracker.Avalonia.Services;

public class UpdateService : IUpdateService
{
    public string ObtenerVersionActual() => "1.0.0-avalonia";

    public bool EstaInstaladoPorVelopack() => false;

    public Task<UpdateInfo?> ComprobarActualizacionesAsync(bool esManual = false) => Task.FromResult<UpdateInfo?>(null);

    public Task<bool> DescargarActualizacionAsync(UpdateInfo updateInfo, Action<int>? onProgreso = null) => Task.FromResult(false);

    public void AplicarActualizacionYReiniciar(UpdateInfo updateInfo) { }

    public void IniciarVerificacionSegundoPlano(TimeSpan intervalo) { }

    public Task<ReleaseInfo> ObtenerInfoUltimaVersionAsync(bool forzarActualizacion = false) => Task.FromResult(new ReleaseInfo { NotasVersion = "Dummy", Titulo = "Dummy" });
}
