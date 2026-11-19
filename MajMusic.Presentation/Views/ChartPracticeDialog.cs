#nullable enable

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MajMusic.Core.Models;
using MajMusic.Presentation.ViewModels;
using ArcButton = TKCode.Arc.Button;
using ArcSlider = TKCode.Arc.Slider;
using GlyphPath = Avalonia.Controls.Shapes.Path;

namespace MajMusic.Presentation.Views
{
    /// <summary>Interactive contents of the settings popover. Edits remain a draft until saved.</summary>
    internal sealed class ChartPracticeDialog : Grid
    {
        public ChartPracticeDialog(MainViewModel owner, Action close)
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto");
            var title = new TextBlock { Text = "谱面设置", FontWeight = FontWeight.SemiBold, FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center };
            var gear = new GlyphPath { Data = TKCode.Arc.ArcIcons.Settings.Clone(), Width = 18, Height = 18,
                Stretch = Stretch.Uniform, StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center };
            gear[!GlyphPath.StrokeProperty] = new DynamicResourceExtension("MusicAccentBrush");
            var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Child = gear };
            tile[!Border.BackgroundProperty] = new DynamicResourceExtension("MusicSelectionBrush");
            Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9,
                Margin = new Thickness(0, 0, 0, 8), Children = { tile, title } });

            var draft = owner.ChartOptions;
            var rows = new StackPanel();
            AddSection(rows, "谱面显示");
            var note = AddSlider(rows, "音符流速", draft.NoteSpeed, 0.1, 20, 0.1, "0.0");
            var touch = AddSlider(rows, "触摸流速", draft.TouchSpeed, 0.1, 20, 0.1, "0.0");
            var shade = AddSlider(rows, "背景遮罩", draft.Shade * 100, 0, 100, 10, "0'%'");
            AddSection(rows, "播放与音效");
            var speed = AddSlider(rows, "播放速度", draft.Speed, 0.25, 2, 0.05, "0.00'×'");
            var offset = AddSlider(rows, "打击音偏移", draft.OffsetMs, -500, 500, 5, "0' ms'");
            var effects = AddSlider(rows, "打击音量", draft.Effects * 100, 0, 100, 1, "0'%'");
            var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            SetRow(scroll, 1);
            Children.Add(scroll);

            var cancel = new ArcButton { Content = "取消", Variant = TKCode.Arc.ButtonVariant.Ghost, MinHeight = 36 };
            var save = new ArcButton { Content = "保存", MinHeight = 36 };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancel, save } };
            var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = actions };
            footer[!Border.BorderBrushProperty] = new DynamicResourceExtension("MusicLineBrush");
            SetRow(footer, 2);
            Children.Add(footer);
            cancel.Click += (_, _) => close();
            save.Click += async (_, _) =>
            {
                save.IsEnabled = false;
                try
                {
                    await owner.SaveChartOptionsAsync(new ChartPracticeOptions(note.Value, touch.Value, shade.Value / 100, offset.Value, effects.Value / 100, speed.Value));
                    close();
                }
                catch (Exception exception)
                {
                    owner.ReportError(exception);
                }
                finally
                {
                    save.IsEnabled = true;
                }
            };
        }

        private static void AddSection(Panel rows, string name)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 9, Margin = new Thickness(0, 5, 0, 2) };
            var label = new TextBlock { Text = name, FontSize = 11, FontWeight = FontWeight.SemiBold };
            label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("MusicMutedBrush");
            var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
            rule[!Border.BackgroundProperty] = new DynamicResourceExtension("MusicLineBrush");
            SetColumn(rule, 1);
            row.Children.Add(label);
            row.Children.Add(rule);
            rows.Children.Add(row);
        }

        private static ArcSlider AddSlider(Panel rows, string name, double value, double min, double max, double step, string format)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("72,*,56"), ColumnSpacing = 6, MinHeight = 38 };
            row.Children.Add(new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var slider = new ArcSlider { Minimum = min, Maximum = max, SmallChange = step, IsValueVisible = false,
                ThumbDiameter = 14, TrackThickness = 3, MinHeight = 36, Value = value };
            AutomationProperties.SetName(slider, name);
            SetColumn(slider, 1);
            row.Children.Add(slider);
            var label = new TextBlock { Text = value.ToString(format), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            var chip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(3, 2), VerticalAlignment = VerticalAlignment.Center, Child = label };
            chip[!Border.BackgroundProperty] = new DynamicResourceExtension("MusicPanelBrush");
            SetColumn(chip, 2);
            row.Children.Add(chip);
            slider.ValueChanged += (_, args) => label.Text = args.NewValue.ToString(format);
            rows.Children.Add(row);
            return slider;
        }
    }
}
