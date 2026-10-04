using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Una pestaña = una fila de la tabla: NavigationService la abre sin conocerla, solo por su tipo de vista.</summary>
[Collection("NavigationServiceTests")]
public class NavegacionPorPestanaTests : IDisposable
{
    private sealed class VistaDePrueba : ObservableObject, IAlEntrarEnPestana
    {
        public int Entradas { get; private set; }
        public bool FallarAlEntrar { get; set; }

        public Task AlEntrarAsync()
        {
            Entradas++;
            if (FallarAlEntrar) throw new InvalidOperationException("fallo de prueba al entrar");
            return Task.CompletedTask;
        }
    }

    private static readonly Pestana PestanaDePrueba = new("Prueba", "Cog", "Nav_Galeria", false, [typeof(VistaDePrueba)]);

    private readonly VistaDePrueba _vista = new();
    private readonly NavigationService _sut;

    public NavegacionPorPestanaTests()
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(_vista);
        _sut = new NavigationService(servicios.BuildServiceProvider());
    }

    // NavigationService se registra en el mensajero estático: sin esto seguiría recibiendo mensajes de otras pruebas.
    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(_sut);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Receive_MuestraLaVistaDeLaPestanaYLeAvisaDeQueSeEntro()
    {
        _sut.Receive(new NavegarMensaje_Pestana(PestanaDePrueba));

        _sut.VistaActual.Should().BeSameAs(_vista);
        _vista.Entradas.Should().Be(1);
        _sut.EstaActiva(PestanaDePrueba).Should().BeTrue();
    }

    [Fact]
    public void Receive_SiLaVistaFallaAlEntrar_LaPestanaSeMuestraIgual()
    {
        _vista.FallarAlEntrar = true;

        Action navegar = () => _sut.Receive(new NavegarMensaje_Pestana(PestanaDePrueba));

        navegar.Should().NotThrow();
        _sut.VistaActual.Should().BeSameAs(_vista);
    }

    [Fact]
    public void Receive_PestanaSinVistaRegistrada_NoLanza()
    {
        var huerfana = new Pestana("Huerfana", "Cog", "Nav_Galeria", false, [typeof(string)]);

        Action navegar = () => _sut.Receive(new NavegarMensaje_Pestana(huerfana));

        navegar.Should().NotThrow();
    }

    [Fact]
    public void Abrir_EnviaElMensajeDeSuPestana()
    {
        PestanaDePrueba.Abrir();

        _sut.VistaActual.Should().BeSameAs(_vista, "NavigationService escucha NavegarMensaje_Pestana");
    }

    [Fact]
    public void ObtenerVista_DevuelveLaVistaPrincipalDeLaPestana()
        => _sut.ObtenerVista(PestanaDePrueba).Should().BeSameAs(_vista);
}
