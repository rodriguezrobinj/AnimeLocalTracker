using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.Services;

public class AuthService : IAuthService
{
    // 1. PEGA TU NÚMERO DE CLIENTE AQUÍ:
    private const string ClientId = "48217";
    
    public string? Token { get; private set; }
    
    // Ruta donde guardaremos el token para que no inicies sesión cada vez que abras la app.
    // Ubicado en la carpeta de datos (fuera del directorio de instalación de Velopack).
    private readonly string _rutaToken = AppDataPaths.TokenPath;

    public bool EstaAutenticado() => File.Exists(_rutaToken) && ObtenerTokenGuardado() != string.Empty;
    
    public string ObtenerTokenGuardado()
    {
        if (!File.Exists(_rutaToken)) return string.Empty;
        
        try
        {
            byte[] ciphertext = File.ReadAllBytes(_rutaToken);
            byte[] plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            // SEC-07: nunca leer el token en claro. Si DPAPI falla (token corrupto/manipulado),
            // se pide re-login: el flujo OAuth es barato y evita exponer credenciales en disco.
            AppLogger.Warn("AuthService", $"No se pudo desencriptar el token con DPAPI ({ex.Message}). Se requiere iniciar sesión de nuevo.");
        }
        
        return string.Empty;
    }

    public async Task<bool> IniciarSesionAsync()
    {
        if (EstaAutenticado()) return true;

        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add("http://localhost:5050/");
            listener.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error("AuthService", "No se pudo iniciar el listener local en el puerto 5050. Puede que el puerto esté ocupado por otra instancia.", ex);
            return false;
        }

        string expectedState = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        // Lanzamos el navegador web del usuario pidiendo permisos con state criptográfico
        var url = $"https://anilist.co/api/v2/oauth/authorize?client_id={ClientId}&response_type=token&state={expectedState}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error("AuthService", "No se pudo abrir el navegador web para la autenticación OAuth", ex);
        }

        string? tokenCapturado = null;
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            while (tokenCapturado == null && !cts.Token.IsCancellationRequested)
            {
                var contextTask = listener.GetContextAsync();
                var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cts.Token));

                if (completedTask != contextTask)
                {
                    AppLogger.Warn("AuthService", "Tiempo de espera de autenticación OAuth expirado (timeout de 2 minutos).");
                    break;
                }

                var context = await contextTask;
                var request = context.Request;
                var response = context.Response;

                if (request.Url?.AbsolutePath == "/callback")
                {
                    string html = @"
                        <html>
                        <head><meta charset='UTF-8'><title>Conectando...</title></head>
                        <body style='font-family: sans-serif; text-align: center; padding: 50px; background: #121212; color: white;'>
                            <h2 id='mensaje'>Completando autenticación...</h2>
                            <script>
                                var hash = window.location.hash;
                                if (hash.includes('access_token')) {
                                    var params = new URLSearchParams(hash.substring(1));
                                    var token = params.get('access_token');
                                    var state = params.get('state') || '';
                                    
                                    fetch('/token', {
                                        method: 'POST',
                                        headers: { 'Content-Type': 'application/json' },
                                        body: JSON.stringify({ token: token, state: state })
                                    })
                                    .then(r => {
                                        if (r.ok) {
                                            document.getElementById('mensaje').innerText = '¡Éxito! Ya puedes cerrar esta pestaña y volver a la aplicación.';
                                        } else {
                                            document.getElementById('mensaje').innerText = 'Error de seguridad: el parámetro de estado no coincide.';
                                        }
                                    })
                                    .catch(() => document.getElementById('mensaje').innerText = 'Error interno de comunicación.');
                                } else {
                                    document.getElementById('mensaje').innerText = 'Autorización denegada.';
                                }
                            </script>
                        </body>
                        </html>";
                    
                    byte[] buffer = Encoding.UTF8.GetBytes(html);
                    response.ContentType = "text/html; charset=utf-8";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer.AsMemory(0, buffer.Length));
                    response.OutputStream.Close();
                }
                else if (request.Url?.AbsolutePath == "/token" && request.HttpMethod == "POST")
                {
                    // SEC-01 (defensa en profundidad): solo aceptar POST provenientes de la
                    // propia página de callback servida en http://localhost:5050. Los navegadores
                    // envían el header Origin en todo POST (mismo o cross-origin); sin Origin ni
                    // Referer válidos (p.ej. script local, DNS rebinding) se rechaza.
                    string origin = request.Headers["Origin"] ?? string.Empty;
                    string referer = request.Headers["Referer"] ?? string.Empty;
                    if (!EsOrigenLocal(origin) && !EsOrigenLocal(referer))
                    {
                        AppLogger.Warn("AuthService", "POST /token rechazado: Origin/Referer no coincide con el listener local.");
                        response.StatusCode = 403;
                        response.OutputStream.Close();
                        continue;
                    }

                    try
                    {
                        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        using var doc = System.Text.Json.JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        
                        string? receivedState = root.TryGetProperty("state", out var sProp) ? sProp.GetString() : null;
                        string? receivedToken = root.TryGetProperty("token", out var tProp) ? tProp.GetString() : null;

                        if (!string.IsNullOrEmpty(receivedState) && receivedState == expectedState && !string.IsNullOrWhiteSpace(receivedToken))
                        {
                            tokenCapturado = receivedToken;
                            response.StatusCode = 200;
                            byte[] okMsg = Encoding.UTF8.GetBytes("{\"success\":true}");
                            response.ContentType = "application/json";
                            response.ContentLength64 = okMsg.Length;
                            await response.OutputStream.WriteAsync(okMsg.AsMemory(0, okMsg.Length));
                            response.OutputStream.Close();
                            break;
                        }
                        else
                        {
                            AppLogger.Warn("AuthService", "Fallo de validación de seguridad (state no coincide o token vacío).");
                            response.StatusCode = 400;
                            response.OutputStream.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("AuthService", "Error al procesar payload de token POST", ex);
                        response.StatusCode = 500;
                        response.OutputStream.Close();
                    }
                }
                else
                {
                    response.StatusCode = 404;
                    response.OutputStream.Close();
                }
            }
            
            if (!string.IsNullOrEmpty(tokenCapturado))
            {
                byte[] plaintext = Encoding.UTF8.GetBytes(tokenCapturado);
                byte[] ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_rutaToken, ciphertext);
                
                WeakReferenceMessenger.Default.Send(new UsuarioLogeadoMensaje());
                AppLogger.Info("AuthService", "Sesión iniciada y token guardado correctamente.");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AuthService", "Error durante el flujo de autenticación", ex);
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("AuthService", $"Listener stop no-op: {ex.Message}");
            }
        }

        return false;
    }
    
    public string? ObtenerToken()
    {
        // 1. Si ya lo tenemos en memoria, lo usamos
        if (!string.IsNullOrEmpty(Token))
            return Token;

        // 2. Si no está en memoria, buscamos en el disco duro cifrado (sesión guardada)
        if (System.IO.File.Exists(_rutaToken))
        {
            Token = ObtenerTokenGuardado();
            if (!string.IsNullOrEmpty(Token)) return Token;
        }

        // 3. Si no existe, el usuario no está conectado
        return null; 
    }

    public void CerrarSesion()
    {
        Token = null;
        if (System.IO.File.Exists(_rutaToken))
        {
            try
            {
                System.IO.File.Delete(_rutaToken);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("AuthService", $"No se pudo eliminar el archivo de token: {ex.Message}");
            }
        }
        
        WeakReferenceMessenger.Default.Send(new UsuarioDesconectadoMensaje());
    }

    /// <summary>
    /// Valida que un header Origin/Referer sea exactamente el listener local del flujo
    /// OAuth (http://localhost:5050). Una comparación por prefijo de cadena aceptaría
    /// hosts evasivos como "localhost:5050.evil.com"; aquí se compara el Uri parseado
    /// (esquema + host + puerto exactos).
    /// </summary>
    internal static bool EsOrigenLocal(string? valor)
    {
        return Uri.TryCreate(valor, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttp
               && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               && uri.Port == 5050;
    }
}