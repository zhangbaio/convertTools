using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Core.Tests;

[Collection("Account AI data root")]
public sealed class VideoProcessingConcurrencySettingsTests
{
    [Fact]
    public async Task Manual_input_displays_the_saved_limit_and_saves_typed_and_spun_values()
    {
        if (VideoProcessingConcurrency.ManualMaximum < 2)
            return;

        var dataRoot = AppPaths.DataRootOverride;
        var initial = VideoProcessingConcurrency.LoadSettings();
        var temporary = Directory.CreateTempSubdirectory("typed-video-processing-limit-");
        AppPaths.DataRootOverride = temporary.FullName;
        try
        {
            await VideoProcessingConcurrency.SaveSettingsAsync(new(false, 1));
            var model = new ProjectConcurrencySettingsViewModel(null, null, showsTikTok: false);
            model.Load();
            var textBox = new TextBox();
            var input = new TestNumericUpDown
            {
                Minimum = 1,
                Maximum = VideoProcessingConcurrency.ManualMaximum,
                FormatString = "0",
                Template = new FuncControlTemplate<NumericUpDown>((_, scope) =>
                {
                    scope.Register("PART_TextBox", textBox);
                    return textBox;
                }),
            };
            using var binding = input.Bind(NumericUpDown.ValueProperty, new Binding(nameof(model.VideoManualLimit))
            {
                Source = model,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });

            input.InitializeControl();
            input.ApplyTemplate();

            Assert.True(input.IsInitialized);
            Assert.Equal("1", textBox.Text);
            Assert.DoesNotContain("未保存", model.VideoProcessingStatus);

            textBox.Text = "2";
            Assert.Contains("上限修改未保存", model.VideoProcessingStatus);
            Assert.Contains("当前生效 1 路", model.VideoProcessingStatus);
            await model.SaveCommand.ExecuteAsync(null);

            Assert.Equal(new VideoProcessingConcurrencySettings(false, 2), VideoProcessingConcurrency.LoadSettings());
            Assert.Equal(2, VideoDedupEncodeGate.ReadUsage().Limit);
            Assert.Contains("当前生效 2 路", model.VideoProcessingStatus);
            Assert.DoesNotContain("未保存", model.VideoProcessingStatus);

            input.Spin(SpinDirection.Decrease);
            Assert.Equal("1", textBox.Text);
            Assert.Contains("上限修改未保存", model.VideoProcessingStatus);
            await model.SaveCommand.ExecuteAsync(null);
            Assert.Equal(new VideoProcessingConcurrencySettings(false, 1), VideoProcessingConcurrency.LoadSettings());
        }
        finally
        {
            try { await VideoProcessingConcurrency.SaveSettingsAsync(initial); }
            finally
            {
                AppPaths.DataRootOverride = dataRoot;
                temporary.Delete(recursive: true);
            }
        }
    }

    private sealed class TestNumericUpDown : NumericUpDown
    {
        public void InitializeControl() => InitializeIfNeeded();

        public void Spin(SpinDirection direction) => OnSpin(new SpinEventArgs(direction));
    }

    [Fact]
    public async Task Saving_either_mode_updates_the_shared_limit_and_preserves_the_manual_value()
    {
        var dataRoot = AppPaths.DataRootOverride;
        var initial = VideoProcessingConcurrency.LoadSettings();
        var temporary = Directory.CreateTempSubdirectory("video-processing-settings-");
        AppPaths.DataRootOverride = temporary.FullName;
        try
        {
            var manual = new VideoProcessingConcurrencySettings(false, VideoProcessingConcurrency.ManualMaximum);
            Assert.True(VideoProcessingConcurrency.LoadSettings().Automatic);
            await VideoProcessingConcurrency.SaveSettingsAsync(manual);
            Assert.Equal(manual, VideoProcessingConcurrency.LoadSettings());
            Assert.Equal(new VideoProcessingConcurrency.EffectiveLimit(manual.ManualLimit, false),
                await VideoProcessingConcurrency.EnsureAppliedAsync(CancellationToken.None));

            var automatic = manual with { Automatic = true };
            await VideoProcessingConcurrency.SaveSettingsAsync(automatic);
            Assert.Equal(automatic, VideoProcessingConcurrency.LoadSettings());
            Assert.Equal(new VideoProcessingConcurrency.EffectiveLimit(await VideoProcessingConcurrency.GetLimitAsync(), true),
                await VideoProcessingConcurrency.EnsureAppliedAsync(CancellationToken.None));
        }
        finally
        {
            try { await VideoProcessingConcurrency.SaveSettingsAsync(initial); }
            finally
            {
                AppPaths.DataRootOverride = dataRoot;
                temporary.Delete(recursive: true);
            }
        }
    }
}
