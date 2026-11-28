using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services.TikTok;

internal enum PublishAccountCreationState { Accepted, Retryable, Pending }

internal sealed record PublishAccountCreationResult(PublishAccountCreationState State, string Message);

internal sealed class TikTokPublishAccountBatchRunner(MainViewModel viewModel)
{
    private readonly HashSet<string> _activeAccounts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<PublishAccountCreationResult> CreateAsync(
        TikTokAccountProfile account, PublishAccountMaterial material, CancellationToken ct, IProgress<string>? progress = null)
    {
        lock (_activeAccounts)
        {
            if (!_activeAccounts.Add(account.Id))
                return new(PublishAccountCreationState.Retryable, "该主账号已有一项创建申请正在提交。");
        }

        TikTokPublishAccountMaterials.MaterialReservation? reservation = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            reservation = await Task.Run(() => TikTokPublishAccountMaterials.Reserve(material, account.Id), ct);
            var viewAccount = viewModel.Accounts.FirstOrDefault(item => ReferenceEquals(item.Model, account))
                ?? throw new InvalidOperationException("主账号已移除");

            var response = await TikTokApiUploadService.CreatePublishingAccountAsync(
                account, material, saved => viewModel.HandleApiLoginSaved(viewAccount, saved),
                reservation.BeginSubmission, progress, ct);
            if (response.State == PublishAccountCreationState.Accepted)
                reservation.Complete(accepted: true, response.ReceiptId);
            else if (response.State == PublishAccountCreationState.Retryable)
                reservation.Complete(accepted: false, "");
            return new(response.State, response.Message);
        }
        catch (OperationCanceledException)
        {
            return reservation?.SubmissionStarted == true
                ? new(PublishAccountCreationState.Pending, "已开始提交，结果待核对")
                : new(PublishAccountCreationState.Retryable, ct.IsCancellationRequested ? "已停止，未提交" : "接口请求超时，未提交");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                   HttpRequestException or TimeoutException or JsonException or ArgumentException)
        {
            if (reservation?.SubmissionStarted == true)
                return new(PublishAccountCreationState.Pending, "提交结果或使用标记待核对，请查看平台记录");
            var message = ex is InvalidOperationException ? ex.Message : "接口暂不可用或素材无法读取，请检查后重试。";
            return new(PublishAccountCreationState.Retryable, message);
        }
        finally
        {
            reservation?.Dispose();
            lock (_activeAccounts)
                _activeAccounts.Remove(account.Id);
        }
    }
}
