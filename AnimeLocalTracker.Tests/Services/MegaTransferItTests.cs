using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Mega (archivos cifrados: AES-CTR con la clave del enlace) y TransferIt (el mismo servicio de MEGA, pero el archivo se
/// sirve en claro). Los vectores de cifrado se generaron con PyCryptodome, una implementación independiente de la nuestra.
/// </summary>
public class MegaTransferItTests : IDisposable
{
    private const string EnlaceFile = "https://mega.nz/file/vvpnGa5K#nwPBwVSws3oTGESA3rDz-JvMVil1txGQFSzs4nPO2e8";
    private const string ClaveB64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA"; // bytes 1..32

    private const string PlanoHex = "030a11181f262d343b424950575e656c737a81888f969da4abb2b9c0c7ced5dce3eaf1f8ff060d141b222930373e454c535a61686f767d848b9299a0a7aeb5bcc3cad1d8dfe6edf4fb020910171e252c333a41484f565d646b727980878e959ca3aab1b8";
    private const string Cifrado0Hex = "f6a99d7e919b772980e175baed472926e34b395b140fdeb48b18e71d98dfb148b0516dabd8fb90a94eb4a414096353cd5fa2b6589b0a562a6135f73dd895b223f3fc16711e2d1dd514b07fe2fce250197367a348b5d978775038bf5ca771293d9a31887b";
    private const string Cifrado32Hex = "50b18d4b38dbb0896ed4c474690373ed7f8256b87beab60a4115d75db8f5d243d3dc36513ecdfd35f4905fc2dcc230791307836895f95897b0d85f7c8751091dfa51e81b668dddbe4ce81f9051fb68c1528197e95197d0e1cf34fc28a562d63a2815138f";

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"mega_{Guid.NewGuid():N}");

    public MegaTransferItTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    private static byte[] Clave() => Convert.FromBase64String(ClaveB64.Replace('-', '+').Replace('_', '/') + "=");

    // === Enlaces ===

    [Fact]
    public void ParsearEnlaceMega_ConEnlaceDeArchivo_DevuelveHandleYClaveDe32Bytes()
    {
        var enlace = MegaTransferIt.ParsearEnlaceMega(EnlaceFile);

        enlace.Should().NotBeNull();
        enlace!.Value.Handle.Should().Be("vvpnGa5K");
        enlace.Value.Clave.Should().HaveCount(32);
    }

    [Fact]
    public void ParsearEnlaceMega_ConEnlaceEmbed_TambienLoEntiende()
    {
        var enlace = MegaTransferIt.ParsearEnlaceMega("https://mega.nz/embed/ntxzURRJ#7CCEoGhFf0QplFo-W-toyUq8a6B5JR3NQu-m9EJtBzE");

        enlace.Should().NotBeNull();
        enlace!.Value.Handle.Should().Be("ntxzURRJ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/file/vvpnGa5K#nwPBwVSws3oTGESA3rDz-JvMVil1txGQFSzs4nPO2e8")] // otro sitio
    [InlineData("http://mega.nz/file/vvpnGa5K#nwPBwVSws3oTGESA3rDz-JvMVil1txGQFSzs4nPO2e8")]       // sin https
    [InlineData("https://mega.nz/file/vvpnGa5K")]                                                  // sin clave
    [InlineData("https://mega.nz/file/vvpnGa5K#clave-corta")]                                      // clave que no es de 32 bytes
    [InlineData("https://mega.nz/file/corto#nwPBwVSws3oTGESA3rDz-JvMVil1txGQFSzs4nPO2e8")]         // handle mal formado
    [InlineData("https://mega.nz/folder/vvpnGa5K#nwPBwVSws3oTGESA3rDz-JvMVil1txGQFSzs4nPO2e8")]    // carpeta: no es un archivo
    public void ParsearEnlaceMega_ConEnlaceNoValido_DevuelveNull(string? url) =>
        MegaTransferIt.ParsearEnlaceMega(url).Should().BeNull();

    [Fact]
    public void ExtraerHandleTransferIt_ConEnlaceDeTransferencia_DevuelveElHandle() =>
        MegaTransferIt.ExtraerHandleTransferIt("https://transfer.it/t/LWBgKbxnyZgK").Should().Be("LWBgKbxnyZgK");

    [Theory]
    [InlineData(null)]
    [InlineData("https://transfer.it/t/corto")]
    [InlineData("https://otro.com/t/LWBgKbxnyZgK")]
    [InlineData("http://transfer.it/t/LWBgKbxnyZgK")]
    [InlineData("https://transfer.it/x/LWBgKbxnyZgK")]
    public void ExtraerHandleTransferIt_ConEnlaceNoValido_DevuelveNull(string? url) =>
        MegaTransferIt.ExtraerHandleTransferIt(url).Should().BeNull();

    [Fact]
    public void LaClaveViajaEnElFragmentoDeLaUrlYSeRecupera()
    {
        var url = MegaTransferIt.UrlConClave("https://gfs1.userstorage.mega.co.nz/dl/token", Clave());

        url.Should().StartWith("https://gfs1.userstorage.mega.co.nz/dl/token#");
        MegaTransferIt.ClaveDeUrl(url).Should().Equal(Clave());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://gfs1.userstorage.mega.co.nz/dl/token")]
    [InlineData("https://gfs1.userstorage.mega.co.nz/dl/token#otra=cosa")]
    [InlineData("https://gfs1.userstorage.mega.co.nz/dl/token#mega=corta")]
    public void ClaveDeUrl_SinClaveValida_DevuelveNull(string? url) =>
        MegaTransferIt.ClaveDeUrl(url).Should().BeNull();

    // === Descifrado (AES-CTR) ===

    [Fact]
    public void Descifrar_DesdeElInicio_CoincideConElVectorDePyCryptodome()
    {
        var datos = Convert.FromHexString(Cifrado0Hex);

        MegaTransferIt.Descifrar(Clave(), 0, datos);

        Convert.ToHexString(datos).ToLowerInvariant().Should().Be(PlanoHex);
    }

    [Fact]
    public void Descifrar_DesdeUnOffset_UsaElContadorQueLeCorresponde()
    {
        var datos = Convert.FromHexString(Cifrado32Hex);

        MegaTransferIt.Descifrar(Clave(), 32, datos);

        Convert.ToHexString(datos).ToLowerInvariant().Should().Be(PlanoHex, "reanudar a mitad de archivo exige el contador del bloque 2");
    }

    [Fact]
    public void Descifrar_ConOffsetNoMultiploDe16_Lanza() =>
        FluentActions.Invoking(() => MegaTransferIt.Descifrar(Clave(), 10, new byte[16]))
            .Should().Throw<ArgumentException>();

    [Fact]
    public async Task DescifrarArchivoAsync_ConUnArchivoMayorQueElBufer_DevuelveElOriginal()
    {
        var original = new byte[3 * 1024 * 1024 + 777]; // no múltiplo de 16 ni del búfer
        new Random(42).NextBytes(original);
        var cifrado = (byte[])original.Clone();
        MegaTransferIt.Descifrar(Clave(), 0, cifrado); // CTR es simétrico: cifrar y descifrar es lo mismo
        string origen = Path.Combine(_carpeta, "cifrado.bin"), destino = Path.Combine(_carpeta, "claro.bin");
        await File.WriteAllBytesAsync(origen, cifrado);

        await MegaTransferIt.DescifrarArchivoAsync(origen, destino, Clave(), CancellationToken.None);

        (await File.ReadAllBytesAsync(destino)).Should().Equal(original);
    }

    // === ¿Ya está en claro? (hace idempotente el descifrado tras un corte) ===

    [Theory]
    [InlineData("0000002066747970")] // mp4: tamaño de caja + "ftyp"
    [InlineData("1a45dfa3")]         // mkv / webm (EBML)
    [InlineData("52494646")]         // avi (RIFF)
    [InlineData("4f676753")]         // ogg
    [InlineData("464c5601")]         // flv
    public void PareceContenedorDeVideo_ConCabeceraDeContenedor_EsVerdadero(string hex) =>
        MegaTransferIt.PareceContenedorDeVideo(Convert.FromHexString(hex.PadRight(24, '0'))).Should().BeTrue();

    [Fact]
    public void PareceContenedorDeVideo_ConBytesCifrados_EsFalso() =>
        MegaTransferIt.PareceContenedorDeVideo(Convert.FromHexString(Cifrado0Hex)).Should().BeFalse();

    [Fact]
    public void PareceContenedorDeVideo_ConPocosBytes_EsFalso() =>
        MegaTransferIt.PareceContenedorDeVideo(new byte[] { 0x1a, 0x45 }).Should().BeFalse();

    // === Respuestas de la API ===

    [Fact]
    public void LeerRespuestaG_ConUrlHttps_DevuelveUrlYTamano()
    {
        var r = MegaTransferIt.LeerRespuestaG("""[{"s":206358939,"at":"x","msd":1,"g":"https://gfs206n146.userstorage.mega.co.nz/dl/Af5Gzmj4rx02AwIi","fh":"y"}]""");

        r.Should().NotBeNull();
        r!.Url.Should().Be("https://gfs206n146.userstorage.mega.co.nz/dl/Af5Gzmj4rx02AwIi");
        r.Tamano.Should().Be(206358939);
    }

    [Theory]
    [InlineData("""[{"s":10,"g":"http://gfs1.userstorage.mega.co.nz/dl/x"}]""")] // sin https
    [InlineData("""[{"s":10}]""")]                                                 // sin enlace
    [InlineData("[-9]")]                                                           // archivo no encontrado
    [InlineData("esto no es json")]
    [InlineData("")]
    public void LeerRespuestaG_ConRespuestaInutilizable_DevuelveNull(string json) =>
        MegaTransferIt.LeerRespuestaG(json).Should().BeNull();

    [Theory]
    [InlineData("[-9]", -9)]
    [InlineData("[-17]", -17)]
    [InlineData("""[{"s":1,"g":"https://x"}]""", null)]
    [InlineData("basura", null)]
    public void CodigoErrorApi_LeeElCodigoNegativoSiLoHay(string json, int? esperado) =>
        MegaTransferIt.CodigoErrorApi(json).Should().Be(esperado);

    [Fact]
    public void LeerArchivoDeTransferencia_IgnoraLaCarpetaYDevuelveElArchivo()
    {
        const string json = """
            [{"f":[{"h":"bTpn2ZQC","p":"","t":1,"a":"xx","k":"yy","ts":1790944241},
                   {"h":"XHp1FJ5S","p":"bTpn2ZQC","t":0,"a":"zz","k":"ww","s":206358939,"ts":1790944283}]}]
            """;

        var archivo = MegaTransferIt.LeerArchivoDeTransferencia(json);

        archivo.Should().NotBeNull();
        archivo!.Value.Handle.Should().Be("XHp1FJ5S");
        archivo.Value.Tamano.Should().Be(206358939);
    }

    [Theory]
    [InlineData("""[{"f":[{"h":"bTpn2ZQC","p":"","t":1}]}]""")] // solo carpeta
    [InlineData("[-9]")]
    [InlineData("basura")]
    public void LeerArchivoDeTransferencia_SinArchivos_DevuelveNull(string json) =>
        MegaTransferIt.LeerArchivoDeTransferencia(json).Should().BeNull();
}
