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
    }
}
