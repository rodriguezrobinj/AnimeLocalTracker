using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services.Discord;

/// <summary>
/// Conexión con el cliente de Discord abierto en este PC por su tubería local (<c>\\.\pipe\discord-ipc-N</c>). Cada mensaje es
/// opcode (int32 LE) + longitud (int32 LE) + JSON UTF-8: 0 = saludo, 1 = comando/respuesta, 2 = cierre. Por cada mensaje que se
/// manda se lee su respuesta: si no se leyeran, se acumularían en la tubería durante toda la sesión. No lanza: un fallo cierra la
/// conexión y devuelve false, y quien la usa decide cuándo reintentar. Lo usa un solo hilo a la vez.
/// </summary>
internal sealed class ClienteIpcDiscord : IDisposable
{
    private const string Origen = "Discord";
    private static readonly TimeSpan EsperaRespuesta = TimeSpan.FromSeconds(3);

    private NamedPipeClientStream? _tuberia;
    private bool _rechazoAvisado;

    public bool Conectado => _tuberia is { IsConnected: true };

    internal static byte[] CodificarTrama(int opcode, string json)
    {
        int longitud = Encoding.UTF8.GetByteCount(json);
        var trama = new byte[8 + longitud];
        BinaryPrimitives.WriteInt32LittleEndian(trama.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(trama.AsSpan(4, 4), longitud);
        Encoding.UTF8.GetBytes(json, trama.AsSpan(8));
        return trama;
    }

    internal static (int Opcode, int Longitud) LeerCabecera(ReadOnlySpan<byte> cabecera)
        => (BinaryPrimitives.ReadInt32LittleEndian(cabecera[..4]), BinaryPrimitives.ReadInt32LittleEndian(cabecera.Slice(4, 4)));

    /// <summary>Busca Discord en sus 10 tuberías posibles y lo saluda. False si no está abierto o rechaza el ID.</summary>
    public async Task<bool> ConectarAsync(string clientId, CancellationToken ct)
    {
        Cerrar();
        for (int i = 0; i < 10; i++)
        {
            var tuberia = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await tuberia.ConnectAsync(200, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tuberia.Dispose();
                continue;
            }

            _tuberia = tuberia;
            var respuesta = await IntercambiarAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = clientId }.ToJsonString(), ct).ConfigureAwait(false);
            if (respuesta is (1, var cuerpo) && cuerpo.Contains("\"READY\"", StringComparison.Ordinal))
            {
                _rechazoAvisado = false;
                AppLogger.Info(Origen, $"Conectado a Discord (discord-ipc-{i}).");
                return true;
            }

            AvisarRechazo($"Discord rechazó la conexión: {respuesta?.Cuerpo ?? "sin respuesta"}");
            Cerrar();
            return false;
        }
        return false;
    }

    /// <summary>Manda un comando SET_ACTIVITY ya serializado. False si se perdió la conexión (queda cerrada).</summary>
    public async Task<bool> EnviarActividadAsync(string comando, CancellationToken ct)
    {
        var respuesta = await IntercambiarAsync(1, comando, ct).ConfigureAwait(false);
        if (respuesta is not (1, var cuerpo))
        {
            if (_tuberia != null) AppLogger.Info(Origen, "Se perdió la conexión con Discord.");
            Cerrar();
            return false;
        }

        if (cuerpo.Contains("\"ERROR\"", StringComparison.Ordinal)) AvisarRechazo($"Discord rechazó el estado: {cuerpo}");
        return true;
    }

    private async Task<(int Opcode, string Cuerpo)?> IntercambiarAsync(int opcode, string json, CancellationToken ct)
    {
        if (_tuberia == null) return null;

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(EsperaRespuesta);
        try
        {
            await _tuberia.WriteAsync(CodificarTrama(opcode, json), limite.Token).ConfigureAwait(false);

            var cabecera = new byte[8];
            await _tuberia.ReadExactlyAsync(cabecera, limite.Token).ConfigureAwait(false);
            var (op, longitud) = LeerCabecera(cabecera);
            if (longitud is < 0 or > 1 << 20) return null;

            var cuerpo = new byte[longitud];
            await _tuberia.ReadExactlyAsync(cuerpo, limite.Token).ConfigureAwait(false);
            return (op, Encoding.UTF8.GetString(cuerpo));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Un ID inválido se repetiría en cada reintento: se apunta una vez hasta la próxima conexión buena.</summary>
    private void AvisarRechazo(string mensaje)
    {
        if (_rechazoAvisado) return;
        _rechazoAvisado = true;
        AppLogger.Warn(Origen, mensaje);
    }

    public void Cerrar()
    {
        _tuberia?.Dispose();
        _tuberia = null;
    }

    public void Dispose() => Cerrar();
}
