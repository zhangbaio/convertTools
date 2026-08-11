using DocumentFormat.OpenXml.Packaging;
using FluentAssertions;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokQueueDocumentStepTests
{
    [Fact]
    public void Document_steps_are_registered_in_expected_order()
    {
        var keys = QueueStepRegistry.All.Select(step => step.Key).ToArray();

        Array.IndexOf(keys, QueueStepKeys.GenerateEpisodeScript)
            .Should().BeLessThan(Array.IndexOf(keys, QueueStepKeys.GenerateProofMaterial));
        Array.IndexOf(keys, QueueStepKeys.GenerateTimestampCertificate)
            .Should().BeGreaterThan(Array.IndexOf(keys, QueueStepKeys.GenerateProofMaterial));
        QueueStepRegistry.UserSelectable.Select(step => step.Key)
            .Should().Contain([QueueStepKeys.GenerateEpisodeScript, QueueStepKeys.GenerateTimestampCertificate]);
    }

    [Fact]
    public void Normalize_step_states_adds_document_steps_to_existing_projects()
    {
        var item = new QueueProjectItem();

        item.NormalizeStepStates();

        item.StepStates[QueueStepKeys.GenerateEpisodeScript].Should().Be(QueueStepStatus.Pending);
        item.StepStates[QueueStepKeys.GenerateTimestampCertificate].Should().Be(QueueStepStatus.Pending);
    }

    [Fact]
    public void Document_writer_creates_readable_docx()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tiktok-document-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "artifact.docx");
            TikTokQueueDocumentWriter.WriteDocument(
                path,
                "测试剧本",
                "审核材料",
                [("第1集", "00:00:01 人物进入场景。")]);

            using var document = WordprocessingDocument.Open(path, false);
            document.MainDocumentPart!.Document.InnerText
                .Should().Contain("测试剧本").And.Contain("第1集");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
