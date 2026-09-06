using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace BLESerialTerminal;

public partial class SessionCaptureWindow : Window
{
    private const int MaxVisibleReplayEvents = 20000;
    private const int MaxVisibleDecodedPackets = 10000;

    private readonly Func<SessionMetadata> _metadataProvider;
    private readonly SessionRecorder _recorder;
    private readonly ObservableCollection<SessionEventRecord> _visibleEvents = new();
    private readonly ObservableCollection<ReplayDecodedPacket> _decodedPackets = new();
    private readonly SessionReplayProcessor _replayProcessor = new();

    private SessionDocument? _loadedDocument;
    private CancellationTokenSource? _playCts;
    private int _replayIndex;
    private DateTime? _lastReplayTimestamp;
    private bool _isPlaying;
    private bool _closed;

    internal SessionCaptureWindow(LogStore store, Func<SessionMetadata> metadataProvider)
    {
        _metadataProvider = metadataProvider;
        _recorder = new SessionRecorder(store);
        InitializeComponent();

        EventGrid.ItemsSource = _visibleEvents;
        DecodedGrid.ItemsSource = _decodedPackets;
        _recorder.EventCountChanged += Recorder_EventCountChanged;
        _recorder.RecordingStateChanged += Recorder_RecordingStateChanged;
        Closed += SessionCaptureWindow_Closed;

        MetadataTextBox.Text = FormatMetadata(_metadataProvider());
        UpdateUiState();
    }

    private void SessionCaptureWindow_Closed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        CancelReplay();
        _recorder.EventCountChanged -= Recorder_EventCountChanged;
        _recorder.RecordingStateChanged -= Recorder_RecordingStateChanged;
        _recorder.Dispose();
    }

    private void Recorder_EventCountChanged(int count)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CaptureCountTextBlock.Text = count.ToString("N0", CultureInfo.InvariantCulture);
            SaveSessionButton.IsEnabled = count > 0;
        });
    }

    private void Recorder_RecordingStateChanged(bool recording)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CaptureStateTextBlock.Text = recording ? "RECORDING" : "STOPPED";
            UpdateUiState();
        });
    }

    private void StartCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            MessageBox.Show(this, "Pause offline replay before starting a live session capture.", "Session Capture", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SessionMetadata metadata = _metadataProvider();
            _recorder.Start(metadata);
            MetadataTextBox.Text = FormatMetadata(_recorder.Snapshot().Metadata);
            CaptureCountTextBlock.Text = "0";
            ReplayDetailTextBox.Clear();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Start capture failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateUiState();
    }

    private void StopCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        _recorder.Stop();
        MetadataTextBox.Text = FormatMetadata(_recorder.Snapshot().Metadata);
        UpdateUiState();
    }

    private void SaveSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.EventCount == 0)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save BLE Serial Terminal session",
            Filter = "BLE Serial session (*.blsession.json)|*.blsession.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"BLESerialSession_{DateTime.Now:yyyyMMdd_HHmmss}.blsession.json"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SessionDocument document = _recorder.Snapshot();
            SessionSerializer.Save(dialog.FileName, document);
            ReplayStateTextBlock.Text = $"Saved {document.Events.Count:N0} events";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save session failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            MessageBox.Show(this, "Stop the live capture before loading an offline replay file.", "Session Replay", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Load BLE Serial Terminal session",
            Filter = "BLE Serial session (*.blsession.json)|*.blsession.json|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            CancelReplay();
            SessionDocument document = SessionSerializer.Load(dialog.FileName);
            _loadedDocument = document;
            ResetReplayState(clearLoadedDocument: false);
            MetadataTextBox.Text = FormatMetadata(document.Metadata);
            ReplayStateTextBlock.Text = $"Loaded v{document.Version}: {document.Events.Count:N0} events";
        }
        catch (Exception ex)
        {
            _loadedDocument = null;
            ResetReplayState(clearLoadedDocument: false);
            MessageBox.Show(this, ex.Message, "Load session failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateUiState();
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedDocument == null || _isPlaying)
            return;
        if (_recorder.IsRecording)
        {
            MessageBox.Show(this, "Stop live capture before starting offline replay.", "Session Replay", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_replayIndex >= _loadedDocument.Events.Count)
            ResetReplayState(clearLoadedDocument: false);

        _isPlaying = true;
        _playCts = new CancellationTokenSource();
        UpdateUiState();
        ReplayStateTextBlock.Text = "OFFLINE REPLAY PLAYING - BLE TX DISABLED";

        try
        {
            CancellationToken token = _playCts.Token;
            while (_loadedDocument != null && _replayIndex < _loadedDocument.Events.Count)
            {
                SessionEventRecord record = _loadedDocument.Events[_replayIndex];
                if (_lastReplayTimestamp.HasValue)
                {
                    double speed = SelectedReplaySpeed();
                    TimeSpan delta = record.Timestamp - _lastReplayTimestamp.Value;
                    if (delta > TimeSpan.Zero)
                    {
                        TimeSpan scaled = TimeSpan.FromTicks((long)(delta.Ticks / speed));
                        await Task.Delay(scaled, token);
                    }
                }

                token.ThrowIfCancellationRequested();
                ProcessReplayEvent(record);
                _lastReplayTimestamp = record.Timestamp;
                _replayIndex++;
                UpdateReplayProgress();
            }
        }
        catch (OperationCanceledException)
        {
            // Pause preserves current replay index/state.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Replay failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isPlaying = false;
            _playCts?.Dispose();
            _playCts = null;
            ReplayStateTextBlock.Text = _loadedDocument == null
                ? "No session loaded"
                : _replayIndex >= _loadedDocument.Events.Count
                    ? "OFFLINE REPLAY COMPLETE - BLE TX DISABLED"
                    : "OFFLINE REPLAY PAUSED - BLE TX DISABLED";
            UpdateUiState();
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => CancelReplay();

    private void StepButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedDocument == null || _recorder.IsRecording)
            return;
        CancelReplay();
        if (_replayIndex >= _loadedDocument.Events.Count)
            return;

        SessionEventRecord record = _loadedDocument.Events[_replayIndex];
        ProcessReplayEvent(record);
        _lastReplayTimestamp = record.Timestamp;
        _replayIndex++;
        ReplayStateTextBlock.Text = "OFFLINE REPLAY STEPPED - BLE TX DISABLED";
        UpdateReplayProgress();
        UpdateUiState();
    }

    private void ResetReplayButton_Click(object sender, RoutedEventArgs e)
    {
        CancelReplay();
        ResetReplayState(clearLoadedDocument: false);
        if (_loadedDocument != null)
            ReplayStateTextBlock.Text = $"Reset: {_loadedDocument.Events.Count:N0} events ready";
        UpdateUiState();
    }

    private void ProcessReplayEvent(SessionEventRecord record)
    {
        _visibleEvents.Add(record);
        while (_visibleEvents.Count > MaxVisibleReplayEvents)
            _visibleEvents.RemoveAt(0);

        foreach (ReplayDecodedPacket packet in _replayProcessor.Process(record))
        {
            _decodedPackets.Add(packet);
            while (_decodedPackets.Count > MaxVisibleDecodedPackets)
                _decodedPackets.RemoveAt(0);
        }

        if (_visibleEvents.Count > 0)
            EventGrid.ScrollIntoView(_visibleEvents[^1]);
        if (_decodedPackets.Count > 0)
            DecodedGrid.ScrollIntoView(_decodedPackets[^1]);
        UpdateReplayCounters();
    }

    private void DecodedGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ReplayDetailTextBox.Text = DecodedGrid.SelectedItem is ReplayDecodedPacket packet
            ? FormatReplayPacket(packet)
            : string.Empty;
    }

    private void CancelReplay()
    {
        try { _playCts?.Cancel(); } catch { }
    }

    private void ResetReplayState(bool clearLoadedDocument)
    {
        if (clearLoadedDocument)
            _loadedDocument = null;
        _replayIndex = 0;
        _lastReplayTimestamp = null;
        _replayProcessor.Reset();
        _visibleEvents.Clear();
        _decodedPackets.Clear();
        ReplayDetailTextBox.Clear();
        UpdateReplayProgress();
        UpdateReplayCounters();
    }

    private void UpdateReplayProgress()
    {
        int total = _loadedDocument?.Events.Count ?? 0;
        ReplayProgressTextBlock.Text = $"Replay {_replayIndex:N0} / {total:N0}";
    }

    private void UpdateReplayCounters()
    {
        ReplayCountersTextBlock.Text = $"RX {_replayProcessor.RawRxEvents:N0} events / {_replayProcessor.RawRxBytes:N0} bytes   KISS {_replayProcessor.KissFrames:N0}";
    }

    private void UpdateUiState()
    {
        bool recording = _recorder.IsRecording;
        bool loaded = _loadedDocument != null;
        bool hasRemaining = loaded && _replayIndex < _loadedDocument!.Events.Count;

        StartCaptureButton.IsEnabled = !recording && !_isPlaying;
        StopCaptureButton.IsEnabled = recording;
        SaveSessionButton.IsEnabled = _recorder.EventCount > 0;
        LoadSessionButton.IsEnabled = !recording && !_isPlaying;
        PlayButton.IsEnabled = loaded && !_isPlaying && !recording;
        PauseButton.IsEnabled = _isPlaying;
        StepButton.IsEnabled = hasRemaining && !_isPlaying && !recording;
        ResetReplayButton.IsEnabled = loaded && !_isPlaying;
        ReplaySpeedComboBox.IsEnabled = !_isPlaying;
    }

    private double SelectedReplaySpeed()
    {
        if (ReplaySpeedComboBox.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double speed) && speed > 0)
            return speed;
        return 1.0;
    }

    private static string FormatMetadata(SessionMetadata metadata)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Application: {metadata.ApplicationVersion}");
        sb.AppendLine($"Capture started UTC: {(metadata.CaptureStartedUtc == default ? "-" : metadata.CaptureStartedUtc.ToString("O"))}");
        sb.AppendLine($"Capture ended UTC: {(metadata.CaptureEndedUtc.HasValue ? metadata.CaptureEndedUtc.Value.ToString("O") : "-")}");
        sb.AppendLine($"Device: {metadata.Device}");
        sb.AppendLine($"Address: {metadata.Address}");
        sb.AppendLine($"Connection: {metadata.ConnectionState}");
        sb.AppendLine($"Profile: {metadata.Profile}");
        sb.AppendLine($"Service: {metadata.ServiceUuid}");
        sb.AppendLine($"Write: {metadata.WriteUuid}");
        sb.AppendLine($"Notify: {metadata.NotifyUuid}");
        sb.AppendLine($"Same characteristic: {metadata.SameCharacteristic}");
        if (metadata.RelevantGatt.Count > 0)
        {
            sb.AppendLine("Relevant GATT:");
            foreach (SessionGattCharacteristic ch in metadata.RelevantGatt)
                sb.AppendLine($"  {ch.Role}: service={ch.ServiceUuid} char={ch.CharacteristicUuid} properties={ch.Properties}");
        }
        return sb.ToString();
    }

    private static string FormatReplayPacket(ReplayDecodedPacket packet)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OFFLINE REPLAY - NO BLE TRANSMISSION");
        sb.AppendLine($"Time: {packet.Timestamp:O}");
        sb.AppendLine($"Characteristic: {packet.Characteristic}");
        sb.AppendLine($"KISS port/cmd: {packet.KissText}");
        sb.AppendLine($"KISS raw HEX: {BitConverter.ToString(packet.Kiss.Raw).Replace('-', ' ')}");
        sb.AppendLine($"KISS payload HEX: {BitConverter.ToString(packet.Kiss.Payload).Replace('-', ' ')}");
        foreach (string warning in packet.Kiss.Warnings)
            sb.AppendLine($"KISS warning: {warning}");

        if (packet.Ax25 != null)
        {
            sb.AppendLine();
            sb.AppendLine($"AX.25: {packet.Ax25.Source.Display}>{packet.Ax25.Destination.Display} path={packet.Ax25.Path}");
            sb.AppendLine($"Frame: {packet.Ax25.FrameType} control=0x{packet.Ax25.Control:X2} pid={(packet.Ax25.Pid.HasValue ? $"0x{packet.Ax25.Pid.Value:X2}" : "-")}");
            sb.AppendLine($"Info: {packet.Ax25.InformationText}");
        }
        else if (!string.IsNullOrWhiteSpace(packet.DecodeError))
        {
            sb.AppendLine($"AX.25 decode: {packet.DecodeError}");
        }

        if (packet.Aprs != null)
        {
            sb.AppendLine();
            sb.AppendLine($"APRS: {packet.Aprs.Category} - {packet.Aprs.Summary}");
            sb.AppendLine($"Raw information: {packet.Aprs.RawInformation}");
            foreach (KeyValuePair<string, string> field in packet.Aprs.Fields)
                sb.AppendLine($"{field.Key}: {field.Value}");
            foreach (string warning in packet.Aprs.Warnings)
                sb.AppendLine($"APRS warning: {warning}");
        }
        return sb.ToString();
    }
}
