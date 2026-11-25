using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ChannelsPublisher.Clip;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Views;

internal sealed class MaterialClipModeEditor : Border
{
    public static IReadOnlyList<string> Modes { get; } = ["highlight", "mashup", "commentary", "slice"];

    public static string Label(string mode, bool story) => mode switch
    {
        "highlight" => story ? "剧情高光" : "原声高光",
        "mashup" => "节奏混剪",
        "commentary" => "AI 解说",
        "slice" => "连续切片",
        _ => mode,
    };

    private static string Description(string mode, bool story) => mode switch
    {
        "highlight" => story ? "保留原声和对白切点，可从多集串起一段剧情。" : "保留原声，按精彩程度挑选高光片段。",
        "mashup" => "把多个精彩片段组合成节奏更快的短片。",
        "commentary" => "用解说文案和配音串起剧情，需要配置 AI 和配音服务。",
        "slice" => "每条成片从同一集截取一段连续画面。",
        _ => "",
    };

    public MaterialClipModeEditor(MaterialClipTaskConfiguration config, string mode, Action changed, Action rebuild)
    {
        Classes.Add("panelCard");
        BorderBrush = Brush.Parse("#CBD9ED");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10);
        var body = new StackPanel { Spacing = 8 };
        Child = body;

        var heading = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        var choices = new (string Mode, bool Story)[]
        {
            ("highlight", true), ("highlight", false), ("mashup", false), ("commentary", false), ("slice", false),
        }.Where(choice => choice.Mode == mode || !config.EnabledModes.Contains(choice.Mode)).ToArray();
        var picker = new ComboBox
        {
            ItemsSource = choices.Select(choice => Label(choice.Mode, choice.Story)).ToArray(),
            SelectedIndex = Array.FindIndex(choices, choice => choice.Mode == mode &&
                (mode != "highlight" || choice.Story == config.StoryHighlights)),
        };
        AutomationProperties.SetName(picker, "剪辑模式");
        heading.Children.Add(picker);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0)
                return;
            var choice = choices[picker.SelectedIndex];
            config.SetModeEnabled(mode, false);
            config.SetModeEnabled(choice.Mode, true);
            if (choice.Mode == "highlight")
                config.StoryHighlights = choice.Story;
            changed();
            rebuild();
        };
        if (config.EnabledModes.Count > 1)
        {
            var remove = new Button { Content = "移除此模式" };
            remove.Click += (_, _) =>
            {
                config.SetModeEnabled(mode, false);
                changed();
                rebuild();
            };
            heading.Children.Add(remove);
        }
        body.Children.Add(heading);
        body.Children.Add(new TextBlock
        {
            Text = Description(mode, config.StoryHighlights), Classes = { "pageSummary" }, TextWrapping = TextWrapping.Wrap,
        });

        var existing = config.GetRanges(mode);
        var count = Number(existing.Count, 10, "成片条数");
        var min = Number(existing[0].MinSeconds, 1800, "最短秒数");
        var max = Number(existing[0].MaxSeconds, 1800, "最长秒数");
        var rangeSummary = new TextBlock { Classes = { "pageSummary" }, TextWrapping = TextWrapping.Wrap };
        var quick = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        quick.Children.Add(Field("成片条数", count));
        quick.Children.Add(Field("最短秒数", min));
        quick.Children.Add(Field("最长秒数", max));
        var unify = new Button { Content = "统一时长", VerticalAlignment = VerticalAlignment.Bottom };
        quick.Children.Add(unify);
        body.Children.Add(quick);
        body.Children.Add(rangeSummary);

        var rows = new StackPanel { Spacing = 6 };
        var detail = new Expander { Header = "逐条调整时长", Content = rows };
        body.Children.Add(detail);
        var perClip = Enumerable.Range(0, 10).Select(index =>
        {
            var range = index < existing.Count ? existing[index] : existing[^1];
            var lower = Number(range.MinSeconds, 1800, $"第 {index + 1} 条最短秒数");
            var upper = Number(range.MaxSeconds, 1800, $"第 {index + 1} 条最长秒数");
            var row = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
            row.Children.Add(new TextBlock { Text = $"第 {index + 1} 条", VerticalAlignment = VerticalAlignment.Center, MinWidth = 58 });
            row.Children.Add(lower);
            row.Children.Add(new TextBlock { Text = "至", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(upper);
            rows.Children.Add(row);
            return (Lower: lower, Upper: upper, Row: row);
        }).ToArray();

        var syncing = false;
        int Count() => (int)(count.Value ?? 1);
        bool IsMixed() => perClip.Take(Count()).Select(row => (row.Lower.Value, row.Upper.Value)).Distinct().Skip(1).Any();
        void Refresh()
        {
            var mixed = IsMixed();
            unify.IsVisible = rangeSummary.IsVisible = mixed;
            rangeSummary.Text = "各条时长不同。修改上方秒数会统一应用到各条成片。";
            for (var i = 0; i < perClip.Length; i++)
                perClip[i].Row.IsVisible = i < Count();
        }
        void SaveRanges()
        {
            config.SetRanges(mode, perClip.Take(Count()).Select(row =>
                new ClipTimeRange((int)(row.Lower.Value ?? 0), (int)(row.Upper.Value ?? 0))).ToArray());
            Refresh();
            changed();
        }
        void ApplyUniform()
        {
            syncing = true;
            foreach (var row in perClip)
            {
                row.Lower.Value = min.Value;
                row.Upper.Value = max.Value;
            }
            syncing = false;
            SaveRanges();
        }
        void OnIndividualChanged()
        {
            if (syncing)
                return;
            if (!IsMixed())
            {
                syncing = true;
                min.Value = perClip[0].Lower.Value;
                max.Value = perClip[0].Upper.Value;
                syncing = false;
            }
            SaveRanges();
        }
        count.ValueChanged += (_, _) =>
        {
            var current = config.GetRanges(mode);
            syncing = true;
            for (var index = current.Count; index < Count(); index++)
            {
                perClip[index].Lower.Value = current[^1].MinSeconds;
                perClip[index].Upper.Value = current[^1].MaxSeconds;
            }
            syncing = false;
            SaveRanges();
        };
        min.ValueChanged += (_, _) => { if (!syncing) ApplyUniform(); };
        max.ValueChanged += (_, _) => { if (!syncing) ApplyUniform(); };
        unify.Click += (_, _) => ApplyUniform();
        foreach (var row in perClip)
        {
            row.Lower.ValueChanged += (_, _) => OnIndividualChanged();
            row.Upper.ValueChanged += (_, _) => OnIndividualChanged();
        }
        Refresh();

        if (mode == "highlight" && config.StoryHighlights)
        {
            var advanced = new StackPanel { Spacing = 6 };
            var expander = new Expander { Content = advanced };
            advanced.Children.Add(Flag("本地 OCR 补充硬字幕", config.HardSubtitles, value => config.HardSubtitles = value));
            advanced.Children.Add(Flag("复评画面以发现少对白或无对白高光", config.VisualReview, value => config.VisualReview = value));
            void UpdateAdvancedTitle()
            {
                var flags = new List<string>();
                if (config.HardSubtitles) flags.Add("硬字幕");
                if (config.VisualReview) flags.Add("画面复评");
                expander.Header = flags.Count == 0 ? "高级设置" : $"高级设置 · {string.Join("、", flags)}";
            }
            UpdateAdvancedTitle();
            body.Children.Add(expander);

            CheckBox Flag(string label, bool initial, Action<bool> set)
            {
                var box = new CheckBox { Content = label, IsChecked = initial };
                box.IsCheckedChanged += (_, _) =>
                {
                    set(box.IsChecked == true);
                    UpdateAdvancedTitle();
                    changed();
                };
                return box;
            }
        }
    }

    private static StackPanel Field(string label, Control input)
    {
        var field = new StackPanel { Spacing = 4 };
        field.Children.Add(new TextBlock { Text = label });
        field.Children.Add(input);
        return field;
    }

    private static NumericUpDown Number(int value, int max, string label)
    {
        var input = new NumericUpDown
        {
            Minimum = 1, Maximum = max, Increment = 1, Value = Math.Clamp(value, 1, max), FormatString = "0",
            MinWidth = 117,
        };
        AutomationProperties.SetName(input, label);
        return input;
    }
}
