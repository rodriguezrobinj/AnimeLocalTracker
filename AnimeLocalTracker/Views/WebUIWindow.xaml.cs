using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AnimeLocalTracker.Views
{
    public partial class WebUIWindow : Window
    {
        public WebUIWindow()
        {
            InitializeComponent();
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            try
            {
                // Especificar explícitamente una carpeta de datos de usuario con permisos de escritura
                string userDataFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeLocalTracker", "WebView2Data");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                
                await ReactWebView.EnsureCoreWebView2Async(env);
                
                // Suscribirse al evento para recibir mensajes de React
                ReactWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                
                // Navegar al servidor de desarrollo de React (Vite)
                ReactWebView.Source = new Uri("http://localhost:5173");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al inicializar WebView2: {ex.Message}");
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Leer el mensaje que nos envía React
            string mensajeDeReact = e.TryGetWebMessageAsString();
            
            // Responder de vuelta a React
            ReactWebView.CoreWebView2.PostWebMessageAsString($"Recibido en C# a las {DateTime.Now:HH:mm:ss}. Tu mensaje fue: {mensajeDeReact}");
        }
    }
}
