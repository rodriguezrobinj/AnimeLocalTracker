using System;
using System.Collections.Generic;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Conexiones abiertas a cada servidor de video (host:puerto) entre TODAS las descargas, y el tope
/// que ese servidor ha demostrado aceptar. Visto en uso real con MP4Upload: con varias conexiones
/// abiertas al mismo servidor (a4.mp4upload.com), las de más reciben 403 — no porque el enlace haya
/// caducado, sino porque el servidor limita las conexiones simultáneas por usuario. Antes ese 403 se
/// tomaba por "enlace rechazado": se cortaba toda la descarga, se buscaba otro enlace y se tiraba lo
/// descargado (o, con dos episodios del mismo servidor a la vez, el segundo fallaba sin remedio).
/// Un 403 solo se toma por "demasiadas conexiones" si hay OTRAS conexiones abiertas al mismo servidor
/// y además pruebas de que el enlace funciona: alguna está recibiendo datos, o el servidor respondió
/// bien hace poco (al empezar, las 8 conexiones se abren a la vez y ninguna recibe datos aún, pero el
/// sondeo acaba de contestar). Sin esas pruebas, el problema es el enlace.
/// Lógica pura (el reloj llega por parámetro) para poder testearla sin red.
/// </summary>
internal sealed class LimitadorPorServidor
{
    internal const int TopeMinimo = 2;
    /// <summary>El tope aprendido caduca: la carga del servidor cambia a lo largo del día.</summary>
    internal static readonly TimeSpan VigenciaTope = TimeSpan.FromMinutes(10);
    /// <summary>Cuánto vale como prueba de "el enlace funciona" una respuesta correcta del servidor.</summary>
    internal static readonly TimeSpan VigenciaExito = TimeSpan.FromSeconds(60);

    private sealed class EstadoServidor
    {
        public int Abiertas;
        public int Transfiriendo;
        public int? Tope;
        public DateTime TopeHasta;
        public DateTime? UltimoExito;
    }

    /// <summary>Una conexión con un servidor; hay que cerrarla siempre (Cerrar es idempotente).</summary>
    internal sealed class Conexion
    {
        private readonly LimitadorPorServidor _dueno;
        internal readonly string Servidor;
        internal bool Transfiriendo;
        internal bool Cerrada;

        internal Conexion(LimitadorPorServidor dueno, string servidor)
        {
            _dueno = dueno;
            Servidor = servidor;
        }

        /// <summary>El servidor aceptó la petición y está enviando datos.</summary>
        public void MarcarTransfiriendo(DateTime ahora) => _dueno.MarcarTransfiriendo(this, ahora);

        public void Cerrar() => _dueno.Cerrar(this);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, EstadoServidor> _servidores = new(StringComparer.OrdinalIgnoreCase);

    private EstadoServidor Obtener(string servidor, DateTime? ahora = null)
    {
        if (!_servidores.TryGetValue(servidor, out var estado))
        {
            estado = new EstadoServidor();
            _servidores[servidor] = estado;
        }
        if (ahora.HasValue && estado.Tope.HasValue && ahora.Value >= estado.TopeHasta) estado.Tope = null;
        return estado;
    }

    /// <summary>Tope vigente para el servidor (null si no se ha aprendido ninguno o ya caducó).</summary>
    public int? Tope(string servidor, DateTime ahora)
    {
        lock (_lock) return Obtener(servidor, ahora).Tope;
    }

    public int Abiertas(string servidor)
    {
        lock (_lock) return Obtener(servidor).Abiertas;
    }

    /// <summary>El servidor respondió bien hace poco (el enlace funciona).</summary>
    public bool HayExitoReciente(string servidor, DateTime ahora)
    {
        lock (_lock) return Obtener(servidor).UltimoExito is DateTime exito && ahora - exito < VigenciaExito;
    }

    /// <summary>Abre una conexión si el tope del servidor lo permite; null = esperar y volver a intentar.</summary>
    public Conexion? IntentarAbrir(string servidor, DateTime ahora)
    {
        lock (_lock)
        {
            var estado = Obtener(servidor, ahora);
            if (estado.Tope is int tope && estado.Abiertas >= tope) return null;
            estado.Abiertas++;
            return new Conexion(this, servidor);
        }
    }

    private void MarcarTransfiriendo(Conexion conexion, DateTime ahora)
    {
        lock (_lock)
        {
            var estado = Obtener(conexion.Servidor);
            estado.UltimoExito = ahora;
            if (conexion.Cerrada || conexion.Transfiriendo) return;
            conexion.Transfiriendo = true;
            estado.Transfiriendo++;
        }
    }

    private void Cerrar(Conexion conexion)
    {
        lock (_lock)
        {
            if (conexion.Cerrada) return;
            conexion.Cerrada = true;
            var estado = Obtener(conexion.Servidor);
            if (estado.Abiertas > 0) estado.Abiertas--;
            if (conexion.Transfiriendo && estado.Transfiriendo > 0) estado.Transfiriendo--;
        }
    }

    /// <summary>
    /// La <paramref name="conexion"/> (aún abierta) recibió 403. Si había otras conexiones abiertas al
    /// mismo servidor y el enlace funciona (ver resumen de la clase), es el servidor limitando conexiones:
    /// se aprende un tope (las que había abiertas sin contar esta, y nunca más que el tope anterior) y se
    /// devuelve. Si no, el 403 es del enlace (caducado o bloqueado): null.
    /// </summary>
    public int? RegistrarRechazo(Conexion conexion, DateTime ahora)
    {
        lock (_lock)
        {
            var estado = Obtener(conexion.Servidor, ahora);
            int otrasAbiertas = estado.Abiertas - 1;
            int otrasFuncionando = estado.Transfiriendo - (conexion.Transfiriendo ? 1 : 0);
            bool enlaceFunciona = otrasFuncionando > 0
                                  || (estado.UltimoExito is DateTime exito && ahora - exito < VigenciaExito);
            if (otrasAbiertas <= 0 || !enlaceFunciona) return null;

            int nuevoTope = Math.Max(TopeMinimo, estado.Abiertas - 1);
            if (estado.Tope is int anterior) nuevoTope = Math.Min(nuevoTope, anterior);
            estado.Tope = nuevoTope;
            estado.TopeHasta = ahora + VigenciaTope;
            return nuevoTope;
        }
    }
}
