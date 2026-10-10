using System;
using System.IO;
using AnimeLocalTracker.Services.Native;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class RustNativeTests
{
    [Fact]
    public void NativeMethods_VerificarDisponibilidad_DeberiaEstarDisponibleSiExisteDll()
    {
        // Si animetracker_core.dll está copiado en el directorio de salida
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "animetracker_core.dll")))
        {
            NativeMethods.IsAvailable.Should().BeTrue();
            string? version = NativeMethods.ObtenerVersion();
            version.Should().NotBeNullOrWhiteSpace();
            version.Should().Contain("Rust Core");
        }
    }

    [Fact]
    public void NativeMethods_ParseFilename_DeberiaExtraerCamposFansubCompletos()
    {
        if (!NativeMethods.IsAvailable) return;

        string filename = "[SubsPlease] Sousou no Frieren - 01 (1080p) [ABCD1234].mkv";
        var result = NativeMethods.ParseFilename(filename);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.AnimeTitle.Should().Be("Sousou no Frieren");
        result.EpisodeNumber.Should().Be("01");
        result.ReleaseGroup.Should().Be("SubsPlease");
        result.VideoResolution.Should().Be("1080p");
        result.Checksum.Should().Be("ABCD1234");
        result.FileExtension.Should().Be("mkv");
    }

    [Fact]
    public void NativeMethods_ParseFilename_ConTemporadaYFormatoWestern_DeberiaExtraerEpisodio()
    {
        if (!NativeMethods.IsAvailable) return;

        string filename = "[Erai-raws] Jujutsu Kaisen 2nd Season - 14 [1080p][Multiple Subtitle].mkv";
        var result = NativeMethods.ParseFilename(filename);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.AnimeTitle.Should().Contain("Jujutsu Kaisen");
        result.EpisodeNumber.Should().Be("14");
    }

    [Fact]
    public void NativeMethods_ComputeFingerprint_GeneraHashConsistenteParaElMismoArchivo()
    {
        if (!NativeMethods.IsAvailable) return;

        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, new byte[1024 * 512]); // 512 KB

            var fp1 = NativeMethods.ComputeFingerprint(tempFile);
            var fp2 = NativeMethods.ComputeFingerprint(tempFile);

            fp1.Should().NotBeNull();
            fp1!.Success.Should().BeTrue();
            fp1.Fingerprint.Should().NotBeNullOrWhiteSpace();
            fp1.FileSize.Should().Be(1024 * 512);

            fp2.Should().NotBeNull();
            fp2!.Fingerprint.Should().Be(fp1.Fingerprint);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
