using System.Security.Cryptography;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokProofMaterialTemplateProviderTests
{
    private const string ExpectedTemplateSha256 =
        "6104B21635CF61BE9F7D06361AE1BC5C2CC6EFA6A1B4068C82180861482F7680";

    [Fact]
    public void Embedded_template_is_released_to_data_directory_and_restored_when_corrupted()
    {
        using var temp = new TemporaryDirectory();

        var path = TikTokProofMaterialTemplateProvider.EnsureBuiltInTemplate(temp.Path);

        path.Should().Be(Path.Combine(
            temp.Path,
            "templates",
            "proof-material",
            TikTokProofMaterialTemplateProvider.BuiltInTemplateFileName));
        File.Exists(path).Should().BeTrue();
        ComputeSha256(path).Should().Be(ExpectedTemplateSha256);

        File.WriteAllText(path, "corrupted");
        TikTokProofMaterialTemplateProvider.EnsureBuiltInTemplate(temp.Path).Should().Be(path);
        ComputeSha256(path).Should().Be(ExpectedTemplateSha256);
        Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Existing_custom_template_has_priority_and_invalid_path_falls_back_to_embedded_template()
    {
        using var temp = new TemporaryDirectory();
        var customPath = Path.Combine(temp.Path, "custom.docx");
        File.WriteAllText(customPath, "custom-template");

        TikTokProofMaterialTemplateProvider.ResolveTemplatePath(customPath, temp.Path)
            .Should().Be(Path.GetFullPath(customPath));

        var fallback = TikTokProofMaterialTemplateProvider.ResolveTemplatePath(
            Path.Combine(temp.Path, "missing.docx"),
            temp.Path);
        fallback.Should().NotBe(customPath);
        ComputeSha256(fallback).Should().Be(ExpectedTemplateSha256);
    }

    [Fact]
    public void Empty_builtin_marker_and_legacy_desktop_default_all_resolve_to_embedded_template()
    {
        using var temp = new TemporaryDirectory();

        var empty = TikTokProofMaterialTemplateProvider.ResolveTemplatePath("", temp.Path);
        var marker = TikTokProofMaterialTemplateProvider.ResolveTemplatePath("内置", temp.Path);
        var legacy = TikTokProofMaterialTemplateProvider.ResolveTemplatePath(
            TikTokProofMaterialTemplateProvider.LegacyDefaultTemplatePath,
            temp.Path);

        marker.Should().Be(empty);
        legacy.Should().Be(empty);
        legacy.Should().NotBe(TikTokProofMaterialTemplateProvider.LegacyDefaultTemplatePath);
        ComputeSha256(empty).Should().Be(ExpectedTemplateSha256);
    }

    [Fact]
    public void Client_settings_store_migrates_legacy_hard_coded_template_path_to_builtin_semantics()
    {
        using var temp = new TemporaryDirectory();
        var databasePath = Path.Combine(temp.Path, "settings.db");
        ClientSettingsStore.Save(new ClientSettings
        {
            TiktokProofTemplateDocxPath = TikTokProofMaterialTemplateProvider.LegacyDefaultTemplatePath,
        }, databasePath);

        var loaded = ClientSettingsStore.Load(databasePath);

        loaded.TiktokProofTemplateDocxPath.Should().BeEmpty();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"proof-template-provider-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
