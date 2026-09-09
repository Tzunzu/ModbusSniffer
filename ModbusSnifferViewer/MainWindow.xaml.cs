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
    private string lastSearch = string.Empty;
    private int nextSearchIndex;

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
        lastSearch = string.Empty;
        nextSearchIndex = 0;
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
        UpdateCounts();
        UpdateStatistics(filePath);
    }

    private void FindNext_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        FindNext();
    }

    private void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
    }

    private void FindNext()
    {
        string search = SearchTextBox.Text.Trim();
        if (search.Length == 0)
        {
            return;
        }

        if (!string.Equals(search, lastSearch, StringComparison.OrdinalIgnoreCase))
        {
            lastSearch = search;
            nextSearchIndex = 0;
        }

        List<LogEntry> visibleEntries = viewSource.View.Cast<LogEntry>().ToList();
        if (visibleEntries.Count == 0)
        {
            return;
        }

        for (int offset = 0; offset < visibleEntries.Count; offset++)
        {
            int index = (nextSearchIndex + offset) % visibleEntries.Count;
            if (!visibleEntries[index].SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LogGrid.SelectedItem = visibleEntries[index];
            LogGrid.ScrollIntoView(visibleEntries[index]);
            nextSearchIndex = (index + 1) % visibleEntries.Count;
            return;
        }

        nextSearchIndex = 0;
    }

    private void FilterChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        viewSource.View.Refresh();
        UpdateCounts();
        nextSearchIndex = 0;
    }

    private void UpdateCounts()
    {
        int visibleCount = viewSource.View.Cast<LogEntry>().Count();
        CountText.Text = visibleCount == entries.Count
            ? $"{entries.Count:N0} records"
            : $"{visibleCount:N0} of {entries.Count:N0} records";
    }

    private void UpdateStatistics(string filePath)
    {
        long fileSize = new FileInfo(filePath).Length;
        if (entries.Count == 0)
        {
            OverviewText.Text = "No records";
            TrafficText.Text = string.Empty;
            ErrorsText.Text = string.Empty;
            TimingText.Text = string.Empty;
            return;
        }

        DateTimeOffset firstTimestamp = entries.Min(entry => entry.Timestamp);
        DateTimeOffset lastTimestamp = entries.Max(entry => entry.Timestamp);
        TimeSpan span = lastTimestamp - firstTimestamp;
        long frameBytes = entries.Sum(entry => (long)entry.Length);

        OverviewText.Text = string.Join('\n',
            $"{entries.Count:N0} records",
            $"{frameBytes:N0} frame bytes",
            $"{fileSize:N0} bytes on disk",
            $"span {FormatDuration(span)}",
            span.TotalSeconds > 0 ? $"{entries.Count / span.TotalSeconds:N1} records/s" : "rate n/a",
            $"first {firstTimestamp:yyyy-MM-dd HH:mm:ss.fff}",
            $"last  {lastTimestamp:yyyy-MM-dd HH:mm:ss.fff}");

        List<IGrouping<string, LogEntry>> byType = entries
            .GroupBy(entry => entry.Type)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        var traffic = new System.Text.StringBuilder();
        foreach (IGrouping<string, LogEntry> group in byType)
        {
            traffic.AppendLine($"{group.Key,-26} {group.Count(),6} {100d * group.Count() / entries.Count,5:F1}%");
        }

        List<IGrouping<string, LogEntry>> byFunction = entries
            .Where(entry => entry.FunctionName.Length > 0)
            .GroupBy(entry => entry.FunctionName)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        if (byFunction.Count > 0)
        {
            traffic.AppendLine();
            foreach (IGrouping<string, LogEntry> group in byFunction)
            {
                traffic.AppendLine($"{group.Key,-34} {group.Count(),6}");
            }
        }

        var byAddress = entries
            .Where(entry => entry.Address.Length > 0)
            .GroupBy(entry => entry.Address)
            .Select(group => new
            {
                Address = group.Key,
                Total = group.Count(),
                Errors = group.Count(entry => entry.IsError)
            })
            .OrderByDescending(item => item.Errors)
            .ThenBy(item => item.Address, StringComparer.Ordinal)
            .ToList();
        if (byAddress.Count > 0)
        {
            traffic.AppendLine();
            traffic.AppendLine($"{"Address",-10} {"Records",7} {"Errors",7}");
            foreach (var address in byAddress)
            {
                traffic.AppendLine($"{address.Address,-10} {address.Total,7} {address.Errors,7}");
            }
        }

        TrafficText.Text = traffic.ToString().TrimEnd();

        int errorCount = entries.Count(entry => entry.IsError);
        if (errorCount == 0)
        {
            ErrorsText.Text = "No errors";
        }
        else
        {
            var errors = new System.Text.StringBuilder();
            errors.AppendLine($"{errorCount:N0} errors  {100d * errorCount / entries.Count:F2}% of records");
            errors.AppendLine();
            foreach (IGrouping<string, LogEntry> group in entries
                .Where(entry => entry.IsError)
                .GroupBy(entry => entry.Type)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                errors.AppendLine($"{group.Key,-26} {group.Count(),6}");
            }

            ErrorsText.Text = errors.ToString().TrimEnd();
        }

        List<double> responseTimes = entries
            .Where(entry => entry.ResponseTimeMilliseconds.HasValue)
            .Select(entry => entry.ResponseTimeMilliseconds!.Value)
            .OrderBy(value => value)
            .ToList();
        int requests = entries.Count(entry => entry.Type == "REQUEST");
        int matchedResponses = entries.Count(entry =>
            entry.Type is "MATCHED_RESPONSE" or "MATCHED_EXCEPTION_RESPONSE");
        int noResponse = entries.Count(entry => entry.Type == "NO_RESPONSE");
        List<double> masterDelays = entries
            .Where(entry => entry.MasterDelayMilliseconds.HasValue)
            .Select(entry => entry.MasterDelayMilliseconds!.Value)
            .ToList();
        double maxUsbGap = entries.Max(entry => entry.MaximumGapMilliseconds);
        int splitFrames = entries.Count(entry => entry.UsbTransmissionCount > 1);

        var timing = new List<string>
        {
            $"{requests:N0} requests / {matchedResponses:N0} matched",
            noResponse > 0 ? $"{noResponse:N0} requests unanswered" : "all requests answered",
            responseTimes.Count > 0
                ? $"response ms  avg {responseTimes.Average():F1}  p50 {Percentile(responseTimes, 50):F1}  p95 {Percentile(responseTimes, 95):F1}  max {responseTimes[^1]:F1}"
                : "response latency n/a",
            $"max USB intra-frame gap {maxUsbGap:F3} ms",
            $"{splitFrames:N0} frames split across USB reads"
        };
        if (masterDelays.Count > 0)
        {
            timing.Add($"{masterDelays.Count:N0} master delays  max {masterDelays.Max():F0} ms");
        }

        TimingText.Text = string.Join('\n', timing);
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}m {duration.Seconds}s"
                : $"{duration.TotalSeconds:F1}s";

    private static double Percentile(List<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        double rank = percentile / 100d * (sortedValues.Count - 1);
        int low = (int)Math.Floor(rank);
        int high = (int)Math.Ceiling(rank);
        return low == high
            ? sortedValues[low]
            : sortedValues[low] + ((rank - low) * (sortedValues[high] - sortedValues[low]));
    }

    private void FilterEntries(object sender, FilterEventArgs e)
    {
        if (e.Item is not LogEntry entry)
        {
            e.Accepted = false;
            return;
        }

        e.Accepted = !ErrorsOnlyCheckBox.IsChecked.GetValueOrDefault() || entry.IsError;
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
        public string FunctionName { get; init; } = string.Empty;
        public int Length { get; init; }
        public string UsbTransmissions { get; init; } = string.Empty;
        public int UsbTransmissionCount { get; init; }
        public double? ResponseTimeMilliseconds { get; init; }
        public double? MasterDelayMilliseconds { get; init; }
        public double MaximumGapMilliseconds { get; init; }
        public bool IsError { get; init; }
        public string Details { get; init; } = string.Empty;
        public string SearchText => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Label} {Address} {Function} {FunctionName} {Length} {UsbTransmissions} {ResponseTimeMilliseconds} {MaximumGapMilliseconds} {Details}";

        public string Type =>
            Label.StartsWith("MATCHED_EXCEPTION", StringComparison.Ordinal) ? "MATCHED_EXCEPTION_RESPONSE" :
            Label.StartsWith("MATCHED_RESPONSE", StringComparison.Ordinal) ? "MATCHED_RESPONSE" :
            Label.StartsWith("MASTER_DELAY_AFTER_RESPONSE", StringComparison.Ordinal) ? "MASTER_DELAY_AFTER_RESPONSE" :
            Label.StartsWith("NO_RESPONSE", StringComparison.Ordinal) ? "NO_RESPONSE" :
            Label.StartsWith("RESPONSE_MISMATCH", StringComparison.Ordinal) ? "RESPONSE_MISMATCH" :
            Label.StartsWith("RESPONSE_WITHOUT_REQUEST", StringComparison.Ordinal) ? "RESPONSE_WITHOUT_REQUEST" :
            Label.StartsWith("TRUNCATED", StringComparison.Ordinal) ? "TRUNCATED_BY_REQUEST" :
            Label.StartsWith("INCOMPLETE", StringComparison.Ordinal) ? "INCOMPLETE" :
            Label.IndexOf(' ') is int space and > 0 ? Label[..space] : Label;

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
                    FunctionName = root.TryGetProperty("FunctionName", out JsonElement functionName) && functionName.ValueKind == JsonValueKind.String
                        ? functionName.GetString() ?? string.Empty
                        : string.Empty,
                    Length = root.GetProperty("Length").GetInt32(),
                    UsbTransmissions = count == 0 ? string.Empty : $"#{first}-#{last} ({count})",
                    UsbTransmissionCount = count,
                    ResponseTimeMilliseconds = GetNullableDouble(root, "ResponseTimeMilliseconds"),
                    MasterDelayMilliseconds = GetNullableDouble(root, "MasterDelayMilliseconds"),
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
