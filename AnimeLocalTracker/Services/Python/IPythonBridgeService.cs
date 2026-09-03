using System;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Python
{
    public interface IPythonBridgeService : IDisposable
    {
        /// <summary>
        /// Verifica si el motor de Python (ejecutable compilado o script local) está disponible.
        /// </summary>
        Task<bool> IsAvailableAsync();

        /// <summary>
        /// Ejecuta un comando en el motor de herramientas Python enviando y recibiendo JSON tipado.
        /// </summary>
        Task<TResponse?> ExecuteCommandAsync<TRequest, TResponse>(string command, TRequest payload, CancellationToken ct = default);

        /// <summary>
        /// Ejecuta un comando en un proceso one-shot dedicado (nunca bloquea el daemon
        /// persistente) y mata el proceso con su árbol si se cancela. Para comandos
        /// largos como la descarga HLS (download-stream).
        /// </summary>
        Task<TResponse?> ExecuteCommandOneShotAsync<TRequest, TResponse>(string command, TRequest payload, CancellationToken ct = default);
    }
}
