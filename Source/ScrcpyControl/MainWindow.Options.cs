using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ScrcpyControl.Infrastructure;
using ScrcpyControl.Localization;
using ScrcpyControl.Models;
using ScrcpyControl.Services;

namespace ScrcpyControl;

public partial class MainWindow
{
    private sealed record CategoryChoice(ScrcpyOptionCategory? Category, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ChoiceItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private async Task LoadOptionsAsync(IReadOnlyDictionary<string, SavedOptionState> savedOptions)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                _paths.ScrcpyExe,
                ["--help"],
                _paths.BaseDirectory,
                _appCancellation.Token);
            var help = result.CombinedOutput;
            if (string.IsNullOrWhiteSpace(help))
            {
                throw new InvalidOperationException("scrcpy --help 没有返回参数文本");
            }

            _optionDefinitions = ScrcpyOptionCatalog.ParseHelp(help);
            if (_optionDefinitions.Count == 0)
            {
                throw new InvalidOperationException("无法解析 scrcpy 参数");
            }

            _optionValues = _optionDefinitions
                .Select(definition => new ScrcpyOptionValue { Definition = definition })
                .ToList();

            foreach (var option in _optionValues)
            {
                if (savedOptions.TryGetValue(option.Definition.CliName, out var saved))
                {
                    option.IsEnabled = saved.Enabled;
                    option.Value = saved.Value ?? string.Empty;
                }
            }

            if (!savedOptions.ContainsKey("--stay-awake") && _appSettings.AutoStayAwake)
            {
                var stayAwake = _optionValues.FirstOrDefault(
                    option => option.Definition.CliName == "--stay-awake");
                if (stayAwake is not null)
                {
                    stayAwake.IsEnabled = true;
                }
            }

            BuildCategoryChoices();
            RenderOptionRows();
            UpdateCommandPreview();

            var expectedText = _optionDefinitions.Count == 109
                ? "已加载 109 个 scrcpy 参数"
                : $"当前 scrcpy 帮助中解析到 {_optionDefinitions.Count} 个参数，预期 109 个";
            AppendLog(expectedText);
        }
        catch (Exception ex)
        {
            AppendLog($"加载 scrcpy 参数失败：{ex.Message}");
            ParameterPanel.Children.Clear();
            ParameterPanel.Children.Add(new TextBlock
            {
                Text = "无法读取 scrcpy 参数。请确认 scrcpy.exe 与 GUI 位于同一目录。",
                Foreground = (Brush)FindResource("DangerBrush"),
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
    }

    private void BuildCategoryChoices()
    {
        var choices = new List<CategoryChoice>
        {
            new(null, "全部参数 / All options")
        };
        choices.AddRange(
            Enum.GetValues<ScrcpyOptionCategory>()
                .Where(category => _optionValues.Any(
                    value => value.Definition.Category == category &&
                             !value.Definition.ActionOnly))
                .Select(category => new CategoryChoice(
                    category,
                    $"{BilingualScrcpyText.CategoryChinese(category)} / " +
                    $"{BilingualScrcpyText.CategoryEnglish(category)}")));

        CategoryComboBox.ItemsSource = choices;
        CategoryComboBox.SelectedIndex = 0;
    }

    private void RenderOptionRows()
    {
        ParameterPanel.Children.Clear();
        if (_optionValues.Count == 0)
        {
            return;
        }

        var query = OptionSearchBox.Text.Trim();
        var modifiedOnly = ModifiedOnlyCheckBox.IsChecked == true;
        var category = (CategoryComboBox.SelectedItem as CategoryChoice)?.Category;

        var filtered = _optionValues
            .Where(option => !option.Definition.ActionOnly)
            .Where(option => category is null || option.Definition.Category == category)
            .Where(option => !modifiedOnly || option.IsModified)
            .Where(option =>
                string.IsNullOrWhiteSpace(query) ||
                option.Definition.CliName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                option.Definition.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                option.Definition.ChineseDescription.Contains(
                    query,
                    StringComparison.CurrentCultureIgnoreCase) ||
                option.Definition.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            ParameterPanel.Children.Add(new TextBlock
            {
                Text = "没有匹配的参数",
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(4, 16, 0, 0)
            });
            return;
        }

        foreach (var group in filtered
                     .OrderBy(option => option.Definition.Category)
                     .ThenBy(option => option.Definition.CliName, StringComparer.Ordinal)
                     .GroupBy(option => option.Definition.Category))
        {
            var heading = new TextBlock
            {
                Text = $"{BilingualScrcpyText.CategoryChinese(group.Key)} / " +
                       $"{BilingualScrcpyText.CategoryEnglish(group.Key)}",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(2, 16, 0, 4)
            };
            ParameterPanel.Children.Add(heading);

            foreach (var option in group)
            {
                ParameterPanel.Children.Add(CreateOptionRow(option));
            }
        }
    }

    private Border CreateOptionRow(ScrcpyOptionValue option)
    {
        var definition = option.Definition;
        var container = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        CheckBox? enableBox = null;
        if (definition.Kind == ScrcpyOptionKind.Boolean)
        {
            enableBox = new CheckBox
            {
                Content = definition.Label,
                IsChecked = option.IsEnabled,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            enableBox.Checked += (_, _) =>
            {
                option.IsEnabled = true;
                UpdateCommandPreview();
            };
            enableBox.Unchecked += (_, _) =>
            {
                option.IsEnabled = false;
                UpdateCommandPreview();
            };
            Grid.SetColumn(enableBox, 1);
            header.Children.Add(enableBox);
        }
        else
        {
            enableBox = new CheckBox
            {
                IsChecked = option.IsEnabled,
                ToolTip = "启用此参数覆盖项",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(enableBox, 0);
            header.Children.Add(enableBox);

            var label = new TextBlock
            {
                Text = definition.Label,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            header.Children.Add(label);
        }

        var cliText = new TextBlock
        {
            Text = $"EN  {definition.CliName}" +
                   (definition.OptionalValue ? "  [可选值]" : string.Empty),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(cliText, 2);
        header.Children.Add(cliText);
        container.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(definition.ChineseDescription))
        {
            container.Children.Add(new TextBlock
            {
                Text = definition.ChineseDescription,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(
                    definition.Kind == ScrcpyOptionKind.Boolean ? 22 : 30,
                    4,
                    0,
                    0)
            });
        }

        var descriptionText = definition.Description;
        if (!string.IsNullOrWhiteSpace(definition.DefaultHint))
        {
            descriptionText += $"  Default: {definition.DefaultHint}";
        }
        if (string.IsNullOrWhiteSpace(descriptionText))
        {
            descriptionText = "Original scrcpy option text is unavailable.";
        }

        container.Children.Add(new TextBlock
        {
            Text = $"EN  {descriptionText}",
            Style = (Style)FindResource("MutedText"),
            Margin = new Thickness(
                definition.Kind == ScrcpyOptionKind.Boolean ? 22 : 30,
                2,
                0,
                0)
        });

        FrameworkElement? editor = null;
        if (definition.Kind != ScrcpyOptionKind.Boolean)
        {
            editor = CreateOptionEditor(option);
            editor.Margin = new Thickness(
                definition.Kind == ScrcpyOptionKind.Boolean ? 22 : 30,
                8,
                0,
                0);
            editor.IsEnabled = option.IsEnabled;
            container.Children.Add(editor);

            if (enableBox is not null)
            {
                var capturedEditor = editor;
                enableBox.Checked += (_, _) =>
                {
                    option.IsEnabled = true;
                    capturedEditor.IsEnabled = true;
                    if (definition.Kind == ScrcpyOptionKind.Enum &&
                        !definition.OptionalValue &&
                        string.IsNullOrWhiteSpace(option.Value) &&
                        definition.Choices.Count > 0)
                    {
                        option.Value = definition.Choices[0];
                        RefreshEditorValue(capturedEditor, option.Value);
                    }
                    UpdateCommandPreview();
                };
                enableBox.Unchecked += (_, _) =>
                {
                    option.IsEnabled = false;
                    capturedEditor.IsEnabled = false;
                    UpdateCommandPreview();
                };
            }
        }

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(43, 49, 57)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(2, 9, 2, 10),
            ToolTip = string.Join(
                Environment.NewLine,
                new[]
                {
                    definition.ChineseDescription,
                    string.IsNullOrWhiteSpace(definition.Description)
                        ? string.Empty
                        : $"EN  {definition.Description}"
                }.Where(text => !string.IsNullOrWhiteSpace(text)))
        };
        border.Child = container;
        return border;
    }

    private FrameworkElement CreateOptionEditor(ScrcpyOptionValue option)
    {
        var definition = option.Definition;
        switch (definition.Kind)
        {
            case ScrcpyOptionKind.Enum:
            {
                var choiceItems = definition.Choices
                    .Select(value => new ChoiceItem(
                        value,
                        BilingualScrcpyText.ChoiceLabel(value)))
                    .ToList();
                var combo = new ComboBox
                {
                    ItemsSource = choiceItems,
                    SelectedItem = choiceItems.FirstOrDefault(item =>
                                       item.Value == option.Value)
                                   ?? choiceItems.FirstOrDefault(),
                    MinWidth = 230,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ChoiceItem choice)
                    {
                        option.Value = choice.Value;
                        UpdateCommandPreview();
                    }
                };
                return combo;
            }
            case ScrcpyOptionKind.File:
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var textBox = new TextBox { Text = option.Value };
                textBox.TextChanged += (_, _) =>
                {
                    option.Value = textBox.Text;
                    UpdateCommandPreview();
                };
                var browse = new Button
                {
                    Content = "浏览",
                    Margin = new Thickness(8, 0, 0, 0)
                };
                browse.Click += (_, _) =>
                {
                    var dialog = new SaveFileDialog
                    {
                        Filter = "MP4 视频 (*.mp4)|*.mp4|Matroska 视频 (*.mkv)|*.mkv|所有文件 (*.*)|*.*",
                        FileName = "scrcpy-record.mp4"
                    };
                    if (dialog.ShowDialog(this) == true)
                    {
                        textBox.Text = dialog.FileName;
                    }
                };
                Grid.SetColumn(browse, 1);
                grid.Children.Add(textBox);
                grid.Children.Add(browse);
                return grid;
            }
            case ScrcpyOptionKind.Color:
            {
                var textBox = new TextBox
                {
                    Text = option.Value,
                    Width = 180,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "支持 #RGB 或 #RRGGBB"
                };
                textBox.TextChanged += (_, _) =>
                {
                    option.Value = textBox.Text;
                    UpdateCommandPreview();
                };
                return textBox;
            }
            default:
            {
                var textBox = new TextBox
                {
                    Text = option.Value,
                    FontFamily = definition.Kind == ScrcpyOptionKind.KeyValue
                        ? new FontFamily("Cascadia Mono")
                        : new FontFamily("Microsoft YaHei UI"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MinWidth = definition.Kind == ScrcpyOptionKind.KeyValue ? 420 : 300
                };
                textBox.TextChanged += (_, _) =>
                {
                    option.Value = textBox.Text;
                    UpdateCommandPreview();
                };
                return textBox;
            }
        }
    }

    private static void RefreshEditorValue(FrameworkElement editor, string value)
    {
        switch (editor)
        {
            case TextBox textBox:
                textBox.Text = value;
                break;
            case ComboBox comboBox:
                comboBox.SelectedItem = comboBox.Items
                    .OfType<ChoiceItem>()
                    .FirstOrDefault(item => item.Value == value);
                break;
        }
    }

    private void OptionSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoaded)
        {
            RenderOptionRows();
        }
    }

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoaded)
        {
            RenderOptionRows();
        }
    }

    private void ModifiedOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            RenderOptionRows();
        }
    }

    private IReadOnlyList<string> BuildScrcpyArguments()
    {
        var arguments = new List<string>();
        if (_selectedDevice is not null)
        {
            arguments.Add($"--serial={_selectedDevice.Serial}");
        }

        foreach (var option in _optionValues
                     .Where(option => option.IsEnabled && !option.Definition.ActionOnly)
                     .Where(option => option.Definition.CliName != "--serial")
                     .OrderBy(option => option.Definition.CliName, StringComparer.Ordinal))
        {
            arguments.Add(option.ToArgument());
        }

        arguments.AddRange(CommandLineParser.Split(ExtraArgumentsBox.Text));
        return arguments;
    }

    private IReadOnlyList<string> ValidateOptionConflicts(IReadOnlyList<string> arguments)
    {
        var errors = new List<string>();
        var enabled = _optionValues
            .Where(option => option.IsEnabled && !option.Definition.ActionOnly)
            .ToDictionary(option => option.Definition.CliName, option => option.Value, StringComparer.Ordinal);

        foreach (var option in _optionValues.Where(option =>
                     option.IsEnabled &&
                     option.Definition.Kind != ScrcpyOptionKind.Boolean &&
                     !option.Definition.OptionalValue &&
                     string.IsNullOrWhiteSpace(option.Value)))
        {
            errors.Add($"{option.Definition.Label} 已启用但尚未填写值。");
        }

        if (enabled.ContainsKey("--select-usb") && enabled.ContainsKey("--select-tcpip"))
        {
            errors.Add("不能同时选择 USB 和 TCP/IP 设备。");
        }

        if (enabled.ContainsKey("--otg") &&
            (enabled.ContainsKey("--record") || enabled.ContainsKey("--new-display")))
        {
            errors.Add("OTG 模式不支持录制或虚拟显示。");
        }

        if (enabled.TryGetValue("--video-source", out var source) &&
            source == "camera" &&
            (enabled.ContainsKey("--new-display") || enabled.ContainsKey("--display-id")))
        {
            errors.Add("摄像头视频源不能与虚拟显示参数组合。");
        }

        if (enabled.ContainsKey("--no-video") &&
            (enabled.ContainsKey("--record") || enabled.ContainsKey("--video-source")))
        {
            errors.Add("禁用视频时不能同时录制视频或选择视频源。");
        }

        if (enabled.ContainsKey("--no-audio") &&
            (enabled.ContainsKey("--audio-source") ||
             enabled.ContainsKey("--audio-codec") ||
             enabled.ContainsKey("--audio-encoder")))
        {
            errors.Add("禁用音频时不能同时设置音频来源或编码器。");
        }

        foreach (var numeric in _optionValues.Where(option =>
                     option.IsEnabled &&
                     option.Definition.Kind == ScrcpyOptionKind.Number &&
                     !string.IsNullOrWhiteSpace(option.Value)))
        {
            if (!double.TryParse(numeric.Value.TrimEnd('K', 'M', 'k', 'm'), out _))
            {
                errors.Add($"{numeric.Definition.Label} 的数值格式无效。");
            }
        }

        return errors;
    }

    private void UpdateCommandPreview()
    {
        if (!_isLoaded || _optionValues.Count == 0)
        {
            return;
        }

        var arguments = BuildScrcpyArguments();
        CommandPreviewBox.Text =
            _paths.ScrcpyExe + " " + CommandLineParser.FormatForDisplay(arguments);
        var modified = _optionValues.Count(option => option.IsModified);
        ModifiedCountText.Text = $"{modified} 项 / items";
    }

    private Dictionary<string, SavedOptionState> CaptureOptionStates()
    {
        return _optionValues
            .Where(option => !option.Definition.ActionOnly)
            .Where(option => option.IsEnabled || !string.IsNullOrWhiteSpace(option.Value))
            .ToDictionary(
                option => option.Definition.CliName,
                option => new SavedOptionState
                {
                    Enabled = option.IsEnabled,
                    Value = option.Value
                },
                StringComparer.Ordinal);
    }

    private bool IsOptionEnabled(string cliName) =>
        _optionValues.Any(option => option.Definition.CliName == cliName && option.IsEnabled);

    private string? GetOptionValue(string cliName) =>
        _optionValues.FirstOrDefault(option => option.Definition.CliName == cliName)?.Value;

    private void ResetOptions_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "恢复所有参数为 scrcpy 默认值？",
                "恢复默认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var option in _optionValues)
        {
            option.IsEnabled = false;
            option.Value = string.Empty;
        }

        ApplyAutoStayAwakeToOptions();
        RenderOptionRows();
        UpdateCommandPreview();
    }

    private void ApplyOptionStates(IReadOnlyDictionary<string, SavedOptionState> states)
    {
        foreach (var option in _optionValues)
        {
            if (states.TryGetValue(option.Definition.CliName, out var state))
            {
                option.IsEnabled = state.Enabled;
                option.Value = state.Value ?? string.Empty;
            }
            else
            {
                option.IsEnabled = false;
                option.Value = string.Empty;
            }
        }

        RenderOptionRows();
        UpdateCommandPreview();
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Scrcpy 配置 (*.json)|*.json",
            InitialDirectory = _settings.ProfilesDirectory,
            FileName = $"scrcpy-profile-{DateTime.Now:yyyyMMdd-HHmm}.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await _settings.SaveProfileAsync(
                dialog.FileName,
                new ScrcpyProfile
                {
                    Name = Path.GetFileNameWithoutExtension(dialog.FileName),
                    Options = CaptureOptionStates(),
                    ExtraArguments = ExtraArgumentsBox.Text
                },
                _appCancellation.Token);
            BottomStatusText.Text = "参数配置已保存";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Scrcpy 配置 (*.json)|*.json",
            InitialDirectory = _settings.ProfilesDirectory
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var profile = await _settings.LoadProfileAsync(
                dialog.FileName,
                _appCancellation.Token);
            if (profile is null)
            {
                throw new InvalidOperationException("配置内容为空或格式错误");
            }

            ApplyOptionStates(profile.Options);
            ExtraArgumentsBox.Text = profile.ExtraArguments;
            BottomStatusText.Text = $"已加载配置：{profile.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "加载配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunAction_Click(object sender, RoutedEventArgs e)
    {
        var actions = _optionDefinitions.Where(option => option.ActionOnly).ToList();
        if (actions.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var action in actions)
        {
            var item = new MenuItem
            {
                Header = $"{action.Label} / {action.CliName}"
            };
            item.Click += async (_, _) => await RunScrcpyActionAsync(action);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = RunActionButton;
        menu.IsOpen = true;
    }

    private async Task RunScrcpyActionAsync(ScrcpyOptionDefinition action)
    {
        try
        {
            BottomStatusText.Text = $"正在执行 {action.CliName}…";
            var serial = action.CliName is "--help" or "--version"
                ? null
                : _selectedDevice?.Serial;
            var result = await _scrcpy.RunActionAsync(
                [action.CliName],
                serial,
                _appCancellation.Token);
            AppendLog($"{action.CliName} 退出代码：{result.ExitCode}");
            BottomStatusText.Text = $"已执行 {action.CliName}";
            LogsNav.IsChecked = true;
        }
        catch (Exception ex)
        {
            AppendLog($"执行 {action.CliName} 失败：{ex.Message}");
        }
    }
}
