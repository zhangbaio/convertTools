using Avalonia.Controls;
using Avalonia.Interactivity;
using PlatformPublisher.Desktop.Services;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Ui.Views;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouVideoDedupView : UserControl
{
    private string? _accountId;
    private string _accountName = "";
    private VideoDedupPreset? _saved;
    private string _initialFingerprint = "";
    private bool _confirmingChanges;

    public KuaishouVideoDedupView()
    {
        InitializeComponent();
        PresetEditor.PreserveEpisodeBoundaries();
        LoadAccount(null, null, null);
    }

    internal void LoadAccount(string? accountId, string? accountName, string? legacyTemplateId)
    {
        _accountId = accountId;
        _accountName = accountName ?? "";
        AccountSummary.Text = accountId is null ? "请先选择快手账号" : $"当前账号 · {_accountName}。去重保留原集数和集序。";
        FeedbackText.Text = "";
        _saved = null;
        try
        {
            if (accountId is not null)
                _saved = KuaishouVideoDedup.LoadPreset(accountId, legacyTemplateId);
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"读取失败：{ex.Message}";
        }
        PresetEditor.IsEnabled = SaveButton.IsEnabled = _saved is not null;
        RestoreEditor();
    }

    internal async Task<bool> TryLeaveAsync(Window owner)
    {
        if (_confirmingChanges)
            return false;
        if (_saved is null || PresetEditor.CaptureDraft().Fingerprint() == _initialFingerprint)
            return true;
        var accountId = _accountId;
        _confirmingChanges = true;
        try
        {
            var save = await ConfirmDialog.ShowSaveChangesAsync(owner, "保存快手去重配置",
                $"「{_accountName}」的去重方案有未保存的修改。是否保存？");
            if (save is null || accountId != _accountId)
                return false;
            if (save == true)
                return SavePreset();
            RestoreEditor();
            return true;
        }
        finally
        {
            _confirmingChanges = false;
        }
    }

    private void RestoreEditor()
    {
        PresetEditor.Load(_saved);
        _initialFingerprint = PresetEditor.CaptureDraft().Fingerprint();
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => SavePreset();

    private bool SavePreset()
    {
        if (_accountId is null || _saved is null)
            return false;
        try
        {
            var preset = PresetEditor.Read();
            KuaishouVideoDedup.SavePreset(_accountId, preset);
            _saved = preset;
            RestoreEditor();
            FeedbackText.Text = $"已保存「{_accountName}」的去重方案。";
            return true;
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"保存失败：{ex.Message}";
            return false;
        }
    }
}
