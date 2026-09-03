using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using Microsoft.Win32;

namespace ModbusSnifferViewer;

public partial class MainWindow : System.Windows.Window
{
    private readonly ObservableCollection<LogEntry> entries = [];
    private readonly CollectionViewSource viewSource = new();

    public MainWindow()
    {
        InitializeComponent();
        viewSource.Source = entries;
        viewSource.Filter += FilterEntries;
        LogGrid.ItemsSource = viewSource.View;
    }

    private void OpenLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Modbus log files (*.log)|*.log|All files (*.*)|*.*",
            Title = "Open Modbus log",
            InitialDirectory = GetLogDirectory()
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        LoadLog(dialog.FileName);
    }

    private static string? GetLogDirectory()
    {
        string logDirectory = Path.Combine(AppContext.BaseDirectory, "log");
        return Directory.Exists(logDirectory) ? logDirectory : null;
    }

    private void LoadLog(string filePath)
    {
        entries.Clear();
        foreach (string line in File.ReadLines(filePath))
        {
            LogEntry? entry = LogEntry.Parse(line);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        FileText.Text = Path.GetFileName(filePath);
        viewSource.View.Refresh();
        CountText.Text = $"{entries.Count:N0} records";
    }

    private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => viewSource.View.Refresh();

    private void FilterChanged(object sender, System.Windows.RoutedEventArgs e) => viewSource.View.Refresh();

    private void FilterEntries(object sender, FilterEventArgs e)
    {
        if (e.Item is not LogEntry entry)
        {
            e.Accepted = false;
            return;
        }

        string filter = FilterTextBox?.Text.Trim() ?? string.Empty;
        e.Accepted = (!ErrorsOnlyCheckBox.IsChecked.GetValueOrDefault() || entry.IsError) &&
            (filter.Length == 0 || entry.SearchText.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void LogGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        DetailsTextBox.Text = (LogGrid.SelectedItem as LogEntry)?.Details ?? string.Empty;
    }

    private sealed class LogEntry
    {
        public DateTimeOffset Timestamp { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string Function { get; init; } = string.Empty;
        public int Length { get; init; }
        public string UsbTransmissions { get; init; } = string.Empty;
        public double? ResponseTimeMilliseconds { get; init; }
        public double MaximumGapMilliseconds { get; init; }
        public bool IsError { get; init; }
        public string Details { get; init; } = string.Empty;
        public string SearchText => $"{Label} {Address} {Function} {Details}";

        public static LogEntry? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string label = root.GetProperty("Label").GetString() ?? string.Empty;
                long first = root.TryGetProperty("FirstUsbTransmission", out JsonElement firstElement) ? firstElement.GetInt64() : 0;
                long last = root.TryGetProperty("LastUsbTransmission", out JsonElement lastElement) ? lastElement.GetInt64() : 0;
                int count = root.TryGetProperty("UsbTransmissionCount", out JsonElement countElement) ? countElement.GetInt32() : 0;
                return new LogEntry
                {
                    Timestamp = root.GetProperty("Timestamp").GetDateTimeOffset(),
                    Label = label,
                    Address = FormatByte(root, "Address"),
                    Function = FormatByte(root, "Function"),
                    Length = root.GetProperty("Length").GetInt32(),
                    UsbTransmissions = count == 0 ? string.Empty : $"#{first}-#{last} ({count})",
                    ResponseTimeMilliseconds = GetNullableDouble(root, "ResponseTimeMilliseconds"),
                    MaximumGapMilliseconds = root.GetProperty("MaximumGapMilliseconds").GetDouble(),
                    IsError = IsErrorLabel(label),
                    Details = root.TryGetProperty("Hex", out JsonElement hex) ? hex.GetString() ?? string.Empty : line
                };
            }
            catch (JsonException)
            {
                int separator = line.IndexOf(' ');
                string timestampText = separator > 0 ? line[..separator] : string.Empty;
                string label = separator > 0 ? line[(separator + 1)..] : line;
                return DateTimeOffset.TryParse(timestampText, out DateTimeOffset timestamp)
                    ? new LogEntry { Timestamp = timestamp, Label = label, IsError = true, Details = line }
                    : null;
            }
        }

        private static string FormatByte(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? $"0x{value.GetByte():X2}"
                : string.Empty;

        private static double? GetNullableDouble(JsonElement root, string propertyName) =>
            root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null
                ? value.GetDouble()
                : null;

        private static bool IsErrorLabel(string label) =>
            label.StartsWith("INCOMPLETE", StringComparison.Ordinal) ||
            label.StartsWith("TRUNCATED", StringComparison.Ordinal) ||
            label.StartsWith("NO_RESPONSE", StringComparison.Ordinal) ||
            label.StartsWith("RESPONSE_MISMATCH", StringComparison.Ordinal) ||
            label.StartsWith("RESPONSE_WITHOUT_REQUEST", StringComparison.Ordinal) ||
            label.StartsWith("MATCHED_EXCEPTION", StringComparison.Ordinal);
    }
}
