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
        /// Ejecuta un comando en el motor de herramientas Python enviando y recibiendo JSON tipado. Varias llamadas pueden
        /// estar en curso a la vez, y cancelar una no afecta a las demás.
        /// </summary>
        Task<TResponse?> ExecuteCommandAsync<TRequest, TResponse>(string command, TRequest payload, CancellationToken ct = default);

        /// <summary>
        /// Ejecuta un comando en un proceso one-shot dedicado y mata el proceso con su árbol
        /// si se cancela. Para comandos largos que deben poder pararse de verdad, como la
        /// descarga HLS (download-stream).
        /// </summary>
        /// <param name="avance">Recibe el porcentaje (0 a 100) y la velocidad en bytes por segundo, si el comando los va dando.</param>
        Task<TResponse?> ExecuteCommandOneShotAsync<TRequest, TResponse>(string command, TRequest payload,
            IProgress<(double Progreso, double Velocidad)>? avance = null, CancellationToken ct = default);
    }
}
