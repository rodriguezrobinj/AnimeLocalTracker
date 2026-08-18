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
    
    private const string ArchivoToken = "token.txt";
    public string? Token { get; private set; }
    
    // Ruta donde guardaremos el token para que no inicies sesión cada vez que abras la app
    private readonly string _rutaToken = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeLocalTracker", "anilist_token.txt");

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
        catch
        {
            // Podría ser un token viejo en texto plano, intentamos leerlo.
            string plain = File.ReadAllText(_rutaToken);
            if (!string.IsNullOrWhiteSpace(plain) && plain.Length > 20)
            {
                // Es un token viejo, vamos a cifrarlo para el futuro
                byte[] plaintext = Encoding.UTF8.GetBytes(plain);
                byte[] ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_rutaToken, ciphertext);
                return plain;
            }
        }
        
        return string.Empty;
    }

    public async Task<bool> IniciarSesionAsync()
    {
        if (EstaAutenticado()) return true;

        // Levantamos el servidor invisible en el puerto 5050
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5050/");
        listener.Start();

        // Lanzamos el navegador web del usuario pidiendo permisos
        var url = $"https://anilist.co/api/v2/oauth/authorize?client_id={ClientId}&response_type=token";
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

        string? tokenCapturado = null;

        try
        {
            while (tokenCapturado == null)
            {
                var context = await listener.GetContextAsync();
                var request = context.Request;
                var response = context.Response;

                if (request.Url?.AbsolutePath == "/callback")
                {
                    // El navegador llegó. Le inyectamos el JavaScript espía.
                    string html = @"
                        <html>
                        <head><meta charset='UTF-8'><title>Conectando...</title></head>
                        <body style='font-family: sans-serif; text-align: center; padding: 50px; background: #121212; color: white;'>
                            <h2 id='mensaje'>Completando autenticación...</h2>
                            <script>
                                var hash = window.location.hash;
                                if (hash.includes('access_token')) {
                                    var token = new URLSearchParams(hash.substring(1)).get('access_token');
                                    // Le devolvemos el token a C# por la puerta trasera (/token)
                                    fetch('/token?val=' + token)
                                        .then(() => document.getElementById('mensaje').innerText = '¡Éxito! Ya puedes cerrar esta pestaña y volver a la aplicación.')
                                        .catch(() => document.getElementById('mensaje').innerText = 'Error interno.');
                                } else {
                                    document.getElementById('mensaje').innerText = 'Autorización denegada.';
                                }
                            </script>
                        </body>
                        </html>";
                    
                    byte[] buffer = Encoding.UTF8.GetBytes(html);
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                }
                else if (request.Url?.AbsolutePath == "/token")
                {
                    // ¡El JavaScript nos acaba de entregar la llave!
                    tokenCapturado = request.QueryString["val"];
                    response.StatusCode = 200;
                    response.OutputStream.Close();
                    break; // Salimos de la matriz.
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
                File.WriteAllBytes(_rutaToken, ciphertext); // Guardamos la llave cifrada de forma segura
                
                // NOTIFICAMOS A TODA LA APP QUE ALGUIEN SE LOGEÓ
                WeakReferenceMessenger.Default.Send(new UsuarioLogeadoMensaje());
                
                return true;
            }
        }
        catch (Exception) { /* Fallo silencioso si cierran la app antes de terminar */ }
        finally
        {
            listener.Stop();
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
            System.IO.File.Delete(_rutaToken);
        }
        if (System.IO.File.Exists(ArchivoToken))
        {
            System.IO.File.Delete(ArchivoToken);
        }
        
        WeakReferenceMessenger.Default.Send(new UsuarioDesconectadoMensaje());
    }
}