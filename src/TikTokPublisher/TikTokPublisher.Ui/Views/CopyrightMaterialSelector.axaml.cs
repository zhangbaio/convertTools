using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Views;

public partial class CopyrightMaterialSelector : UserControl
{
    private bool _isLoading;

    public CopyrightMaterialSelector()
    {
        InitializeComponent();
        UpdateMinimumStatus();
    }

    public bool UploadAiScriptOutlineWithScreenshots
    {
        get => AiScreenshotsMaterialBox.IsChecked == true &&
               UploadAiScriptOutlineWithScreenshotsBox.IsChecked == true;
        set => UploadAiScriptOutlineWithScreenshotsBox.IsChecked = value;
    }

    public bool UploadSourceInfoRoleSceneScreenshot
    {
        get => SourceInfoMaterialBox.IsChecked == true &&
               UploadSourceInfoRoleSceneScreenshotBox.IsChecked == true;
        set => UploadSourceInfoRoleSceneScreenshotBox.IsChecked = value;
    }

    public void Load(
        IEnumerable<string>? values,
        bool uploadAiScriptOutlineWithScreenshots,
        bool uploadSourceInfoRoleSceneScreenshot)
    {
        _isLoading = true;
        try
        {
            var selected = new HashSet<string>(
                TikTokPublishConstants.NormalizeCopyrightMaterialTypes(values),
                StringComparer.Ordinal);
            ProductionAgreementMaterialBox.IsChecked = selected.Contains("production_agreement");
            WorkRegistrationMaterialBox.IsChecked = selected.Contains("work_registration_certificate");
            FilingLicenseMaterialBox.IsChecked = selected.Contains("filing_or_distribution_license");
            RightsNoticeMaterialBox.IsChecked = selected.Contains("opening_ending_rights_notice");
            AiScreenshotsMaterialBox.IsChecked = selected.Contains("ai_generation_screenshots");
            EditingProjectMaterialBox.IsChecked = selected.Contains("editing_project_files");
            SourceInfoMaterialBox.IsChecked = selected.Contains("source_file_information");
            UploadAiScriptOutlineWithScreenshotsBox.IsChecked =
                AiScreenshotsMaterialBox.IsChecked == true && uploadAiScriptOutlineWithScreenshots;
            UploadSourceInfoRoleSceneScreenshotBox.IsChecked =
                SourceInfoMaterialBox.IsChecked == true && uploadSourceInfoRoleSceneScreenshot;
        }
        finally
        {
            _isLoading = false;
            UpdateMinimumStatus();
        }
    }

    public void Clear()
    {
        _isLoading = true;
        try
        {
            ProductionAgreementMaterialBox.IsChecked = false;
            WorkRegistrationMaterialBox.IsChecked = false;
            FilingLicenseMaterialBox.IsChecked = false;
            RightsNoticeMaterialBox.IsChecked = false;
            AiScreenshotsMaterialBox.IsChecked = false;
            EditingProjectMaterialBox.IsChecked = false;
            SourceInfoMaterialBox.IsChecked = false;
            UploadAiScriptOutlineWithScreenshotsBox.IsChecked = false;
            UploadSourceInfoRoleSceneScreenshotBox.IsChecked = false;
        }
        finally
        {
            _isLoading = false;
            UpdateMinimumStatus();
        }
    }

    public List<string> GetSelectedMaterialTypes()
    {
        var result = new List<string>();
        if (ProductionAgreementMaterialBox.IsChecked == true) result.Add("production_agreement");
        if (WorkRegistrationMaterialBox.IsChecked == true) result.Add("work_registration_certificate");
        if (FilingLicenseMaterialBox.IsChecked == true) result.Add("filing_or_distribution_license");
        if (RightsNoticeMaterialBox.IsChecked == true) result.Add("opening_ending_rights_notice");
        if (AiScreenshotsMaterialBox.IsChecked == true) result.Add("ai_generation_screenshots");
        if (EditingProjectMaterialBox.IsChecked == true) result.Add("editing_project_files");
        if (SourceInfoMaterialBox.IsChecked == true) result.Add("source_file_information");
        return result;
    }

    private void OnSelectMinimumStandardClick(object? sender, RoutedEventArgs e)
    {
        _isLoading = true;
        try
        {
            ProductionAgreementMaterialBox.IsChecked = true;
            WorkRegistrationMaterialBox.IsChecked = false;
            FilingLicenseMaterialBox.IsChecked = false;
            RightsNoticeMaterialBox.IsChecked = false;
            AiScreenshotsMaterialBox.IsChecked = false;
            EditingProjectMaterialBox.IsChecked = false;
            SourceInfoMaterialBox.IsChecked = false;
            UploadAiScriptOutlineWithScreenshotsBox.IsChecked = false;
            UploadSourceInfoRoleSceneScreenshotBox.IsChecked = false;
        }
        finally
        {
            _isLoading = false;
            UpdateMinimumStatus();
        }
    }

    private void OnMaterialSelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        if (AiScreenshotsMaterialBox.IsChecked != true)
            UploadAiScriptOutlineWithScreenshotsBox.IsChecked = false;
        if (SourceInfoMaterialBox.IsChecked != true)
            UploadSourceInfoRoleSceneScreenshotBox.IsChecked = false;
        UpdateMinimumStatus();
    }

    private void UpdateMinimumStatus()
    {
        var selected = GetSelectedMaterialTypes();
        var coreCount = selected.Count(TikTokPublishConstants.CoreCopyrightMaterialTypes.Contains);
        var auxiliaryCount = selected.Count(TikTokPublishConstants.AuxiliaryCopyrightMaterialTypes.Contains);

        if (coreCount > 0)
        {
            MinimumStatusText.Text = $"✓ 已满足最低标准：已选择 {coreCount} 项核心材料";
            MinimumStatusText.Foreground = Brush.Parse("#4BD69A");
            return;
        }

        if (auxiliaryCount >= 2)
        {
            MinimumStatusText.Text = $"✓ 已满足最低标准：已选择 {auxiliaryCount} 项辅助材料";
            MinimumStatusText.Foreground = Brush.Parse("#4BD69A");
            return;
        }

        MinimumStatusText.Text = auxiliaryCount == 1
            ? "尚未满足最低标准：还需选择 1 项辅助材料"
            : "尚未满足最低标准：请选择 1 项核心材料或 2 项辅助材料";
        MinimumStatusText.Foreground = Brush.Parse("#F5C66B");
    }
}
