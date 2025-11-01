using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.IO;
using System.Text.Json;

namespace TuioPatientUI
{
    public partial class MainWindow : Window
    {
        private const string TCP_HOST = "127.0.0.1";
        private const int TCP_PORT = 8765;

        // Enhanced medication list
        private readonly List<string> MedNames = new List<string>
        {
            "Paracetamol", "Amoxicillin", "Aspirin", "Metformin",
            "Lisinopril", "Atorvastatin", "Omeprazole", "Salbutamol",
            "Ibuprofen", "Vitamin D"
        };

        private readonly List<Button> _sectorButtons = new List<Button>();

        // Enhanced state machine
        private enum Mode { Idle, SelectingMedication, ConfirmingTaken }
        private Mode _mode = Mode.Idle;
        private int _hoveredSector = -1;
        private int _selectedMedIndex = -1;
        private string _selectedMedication = "";

        // Medication tracking
        private List<MedicationRecord> _medicationHistory = new List<MedicationRecord>();
        private const string HISTORY_FILE = "medication_history.json";
        private const int HOURS_BETWEEN_DOSES = 12;

        // Networking
        private TcpClient _client;
        private NetworkStream _stream;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            SetupWheel();
            UpdateInstruction();
            LoadMedicationHistory();
        }

        // Medication record class
        public class MedicationRecord
        {
            public string MedicationName { get; set; }
            public DateTime TimeTaken { get; set; }
            public DateTime NextDoseTime { get; set; }
            public bool Taken { get; set; }
        }

        private void Log(string s)
        {
            Dispatcher.Invoke(() =>
            {
                LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {s}");
                if (LogList.Items.Count > 200) LogList.Items.RemoveAt(LogList.Items.Count - 1);
            });
        }

        private void SetMode(Mode m)
        {
            _mode = m;
            Dispatcher.Invoke(() =>
            {
                ModeText.Text = $"Mode: {m}";
                UpdateInstruction();
            });
        }

        private void UpdateInstruction()
        {
            Dispatcher.Invoke(() =>
            {
                switch (_mode)
                {
                    case Mode.Idle:
                        InstructionText.Text = "Place ROTATE marker to select medication";
                        break;
                    case Mode.SelectingMedication:
                        InstructionText.Text = "Rotate marker to choose medication, then place SELECT marker nearby";
                        break;
                    case Mode.ConfirmingTaken:
                        InstructionText.Text = "Rotate marker: LEFT side = YES, RIGHT side = NO";
                        break;
                }
            });
        }

        #region Medication History Management

        private void LoadMedicationHistory()
        {
            try
            {
                if (File.Exists(HISTORY_FILE))
                {
                    string json = File.ReadAllText(HISTORY_FILE);
                    _medicationHistory = JsonSerializer.Deserialize<List<MedicationRecord>>(json) ?? new List<MedicationRecord>();
                    Log($"Loaded {_medicationHistory.Count} medication records");
                    UpdateHistoryDisplay();
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading history: {ex.Message}");
                _medicationHistory = new List<MedicationRecord>();
            }
        }

        private void SaveMedicationHistory()
        {
            try
            {
                string json = JsonSerializer.Serialize(_medicationHistory, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HISTORY_FILE, json);
            }
            catch (Exception ex)
            {
                Log($"Error saving history: {ex.Message}");
            }
        }

        private void UpdateHistoryDisplay()
        {
            Dispatcher.Invoke(() =>
            {
                HistoryList.Items.Clear();

                // Show latest 10 records
                var recentRecords = _medicationHistory
                    .OrderByDescending(r => r.TimeTaken)
                    .Take(10);

                foreach (var record in recentRecords)
                {
                    string status = record.Taken ? "TAKEN" : "NOT TAKEN";
                    string nextDoseInfo = record.Taken ?
                        $"Next: {record.NextDoseTime:HH:mm}" :
                        "Not taken";

                    HistoryList.Items.Add($"{record.MedicationName} - {record.TimeTaken:HH:mm} - {status} - {nextDoseInfo}");
                }

                // Update next dose information
                UpdateNextDoseInfo();
            });
        }

        private void UpdateNextDoseInfo()
        {
            var upcomingDoses = _medicationHistory
                .Where(r => r.Taken && r.NextDoseTime > DateTime.Now)
                .OrderBy(r => r.NextDoseTime)
                .ToList();

            if (upcomingDoses.Any())
            {
                var nextDose = upcomingDoses.First();
                TimeSpan timeUntilNext = nextDose.NextDoseTime - DateTime.Now;
                string timeString = timeUntilNext.TotalHours >= 1 ?
                    $"{timeUntilNext.TotalHours:F1} hours" :
                    $"{timeUntilNext.TotalMinutes:F0} minutes";

                NextDoseText.Text = $"Next dose: {nextDose.MedicationName} in {timeString}";
                NextDoseText.Foreground = timeUntilNext.TotalHours < 1 ? Brushes.Red : Brushes.Green;
            }
            else
            {
                NextDoseText.Text = "No upcoming doses";
                NextDoseText.Foreground = Brushes.Gray;
            }
        }

        private void AddMedicationRecord(string medication, bool taken)
        {
            var record = new MedicationRecord
            {
                MedicationName = medication,
                TimeTaken = DateTime.Now,
                NextDoseTime = DateTime.Now.AddHours(HOURS_BETWEEN_DOSES),
                Taken = taken
            };

            _medicationHistory.Add(record);
            SaveMedicationHistory();
            UpdateHistoryDisplay();

            if (taken)
            {
                Log($"Recorded: {medication} taken at {DateTime.Now:HH:mm}. Next dose at {record.NextDoseTime:HH:mm}");
            }
            else
            {
                Log($"Recorded: {medication} not taken at {DateTime.Now:HH:mm}");
            }
        }

        #endregion

        #region Wheel UI

        private void SetupWheel()
        {
            double cx = WheelCanvas.Width / 2;
            double cy = WheelCanvas.Height / 2;
            double radius = 150;
            int n = MedNames.Count;

            WheelCanvas.Children.Clear();
            _sectorButtons.Clear();

            for (int i = 0; i < n; i++)
            {
                double angle = (2 * Math.PI * i) / n - Math.PI / 2;
                double bx = cx + Math.Cos(angle) * radius;
                double by = cy + Math.Sin(angle) * radius;

                var b = new Button()
                {
                    Width = 120,
                    Height = 40,
                    Content = MedNames[i],
                    Tag = i,
                    FontSize = 10
                };
                Canvas.SetLeft(b, bx - b.Width / 2);
                Canvas.SetTop(b, by - b.Height / 2);
                b.IsHitTestVisible = false;
                WheelCanvas.Children.Add(b);
                _sectorButtons.Add(b);
            }

            // Add center circle
            var centerEllipse = new Ellipse()
            {
                Width = 80,
                Height = 80,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 2
            };
            Canvas.SetLeft(centerEllipse, cx - 40);
            Canvas.SetTop(centerEllipse, cy - 40);
            WheelCanvas.Children.Add(centerEllipse);
        }

        private void ShowWheel(bool show)
        {
            Dispatcher.Invoke(() =>
            {
                WheelCanvas.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show)
                {
                    Popup.Visibility = Visibility.Collapsed;
                    // Don't clear selected medication text when hiding wheel
                }
            });
        }

        private void HighlightSector(int sectorIndex)
        {
            Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < _sectorButtons.Count; i++)
                {
                    var b = _sectorButtons[i];
                    if (_mode == Mode.ConfirmingTaken)
                    {
                        // In confirmation mode, show simple left/right highlighting
                        if (sectorIndex < MedNames.Count / 2)
                        {
                            // Left side - YES
                            b.Background = (i == 0) ? Brushes.LightGreen : SystemColors.ControlBrush;
                            b.BorderBrush = (i == 0) ? Brushes.Green : Brushes.Transparent;
                        }
                        else
                        {
                            // Right side - NO  
                            b.Background = (i == 1) ? Brushes.LightCoral : SystemColors.ControlBrush;
                            b.BorderBrush = (i == 1) ? Brushes.Red : Brushes.Transparent;
                        }
                    }
                    else
                    {
                        // Normal wheel highlighting
                        b.Background = (i == sectorIndex) ? Brushes.LightSkyBlue : SystemColors.ControlBrush;
                        b.BorderBrush = (i == sectorIndex) ? Brushes.DodgerBlue : Brushes.Transparent;
                    }
                }
            });
            _hoveredSector = sectorIndex;
        }

        #endregion

        #region Confirmation Handlers
        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            MarkConfirmation(true);
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            MarkConfirmation(false);
        }

        private void ShowPopup(string medName)
        {
            Dispatcher.Invoke(() =>
            {
                PopupText.Text = $"Did you take {medName}?";
                Popup.Visibility = Visibility.Visible;
                // Update confirmation instruction
                InstructionText.Text = "Rotate marker: LEFT side = YES, RIGHT side = NO";
            });
        }

        private void HidePopup()
        {
            Dispatcher.Invoke(() => Popup.Visibility = Visibility.Collapsed);
        }
        #endregion

        #region Networking
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Log("Starting TCP client...");
            Task.Run(async () => await StartTcpLoop());
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _stream?.Close();
                _client?.Close();
            }
            catch { }
        }

        private async Task StartTcpLoop()
        {
            while (true)
            {
                try
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync(TCP_HOST, TCP_PORT);
                    _stream = _client.GetStream();
                    SetStatus($"Connected to {TCP_HOST}:{TCP_PORT}");
                    Log("Connected to TUIO broadcaster");
                    await ReadLoop(_stream);
                }
                catch (Exception ex)
                {
                    SetStatus($"Disconnected - retrying in 2s ({ex.Message})");
                    Log("Connection failed: " + ex.Message);
                    await Task.Delay(2000);
                }
            }
        }

        private void SetStatus(string s)
        {
            Dispatcher.Invoke(() => StatusText.Text = s);
        }

        private async Task ReadLoop(NetworkStream ns)
        {
            var buffer = new byte[4096];
            var sb = new StringBuilder();

            while (_client?.Connected == true)
            {
                int read = 0;
                try
                {
                    read = await ns.ReadAsync(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (read == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));

                string s = sb.ToString();
                int idx;
                while ((idx = s.IndexOf('\n')) >= 0)
                {
                    string line = s.Substring(0, idx).Trim();
                    if (!string.IsNullOrEmpty(line))
                    {
                        try
                        {
                            ProcessJsonLine(line);
                        }
                        catch (Exception ex)
                        {
                            Log("JSON parse error: " + ex.Message);
                        }
                    }
                    s = s.Substring(idx + 1);
                }
                sb.Clear();
                sb.Append(s);
            }

            Log("Disconnected from broadcaster");
            SetStatus("Disconnected");
            _stream?.Close();
            _client?.Close();
        }
        #endregion

        #region Message Processing & State Machine
        private void ProcessJsonLine(string line)
        {
            var j = JObject.Parse(line);
            var type = (string)j["type"];
            Log($"recv: {type}");

            switch (type)
            {
                case "wheel_open":
                    SetMode(Mode.SelectingMedication);
                    ShowWheel(true);
                    Log("Wheel opened - start selecting medication");
                    break;

                case "wheel_hover":
                    int sector = j["sector"]?.Value<int>() ?? -1;
                    string medication = j["medication"]?.Value<string>();
                    if (sector >= 0)
                    {
                        HighlightSector(sector);
                        // Show which medication is being hovered
                        Dispatcher.Invoke(() => SelectedMedText.Text = $"Hovering: {medication}");
                    }
                    break;

                case "wheel_select_confirm":
                    int chosenSector = j["sector"]?.Value<int>() ?? -1;
                    string chosenMed = j["medication"]?.Value<string>();
                    Dispatcher.Invoke(() => HandleWheelSelect(chosenSector, chosenMed));
                    break;

                case "medication_selected":
                    // Direct medication marker detection
                    string directMed = j["medication"]?.Value<string>();
                    int symbolId = j["symbol_id"]?.Value<int>() ?? -1;
                    Log($"Direct medication selection: {directMed} (symbol {symbolId})");
                    Dispatcher.Invoke(() => HandleDirectMedicationSelect(directMed));
                    break;

                case "back_pressed":
                    Log("Back pressed - canceling current operation");
                    SetMode(Mode.Idle);
                    ShowWheel(false);
                    HidePopup();
                    Dispatcher.Invoke(() => SelectedMedText.Text = "Selection canceled");
                    break;

                default:
                    break;
            }
        }

        private void HandleWheelSelect(int sector, string medication)
        {
            Log($"Wheel selection confirmed: sector={sector}, medication={medication}");

            if (_mode == Mode.SelectingMedication)
            {
                _selectedMedication = medication;
                _selectedMedIndex = sector;
                SelectedMedText.Text = $"Selected: {medication}";
                Log($"Medication selected: {medication}");

                // Show confirmation popup
                ShowPopup(medication);
                SetMode(Mode.ConfirmingTaken);
            }
            else if (_mode == Mode.ConfirmingTaken)
            {
                // In confirmation mode, use wheel position for yes/no
                bool yes = (sector < MedNames.Count / 2); // Left side = yes
                MarkConfirmation(yes);
            }
        }

        private void HandleDirectMedicationSelect(string medication)
        {
            if (_mode == Mode.Idle || _mode == Mode.SelectingMedication)
            {
                _selectedMedication = medication;
                SelectedMedText.Text = $"Selected: {medication}";
                Log($"Direct medication selection: {medication}");

                // Show confirmation popup
                ShowPopup(medication);
                SetMode(Mode.ConfirmingTaken);
                ShowWheel(false); // Hide wheel if it was open
            }
        }

        private void MarkConfirmation(bool taken)
        {
            string result = taken ? "TAKEN" : "NOT TAKEN";
            Log($"Medication confirmation: {_selectedMedication} => {result}");

            // Add to medication history
            AddMedicationRecord(_selectedMedication, taken);

            Dispatcher.Invoke(() =>
            {
                if (taken)
                {
                    DateTime nextDose = DateTime.Now.AddHours(HOURS_BETWEEN_DOSES);
                    SelectedMedText.Text = $"{_selectedMedication} taken at {DateTime.Now:HH:mm}. Next dose at {nextDose:HH:mm}";

                    // Show success message with next dose time
                    MessageBox.Show(
                        $"{_selectedMedication} recorded as taken at {DateTime.Now:HH:mm}\n\n" +
                        $"Next dose: {nextDose:HH:mm} (in {HOURS_BETWEEN_DOSES} hours)",
                        "Medication Recorded",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    SelectedMedText.Text = $"{_selectedMedication} → {result}";
                }

                HidePopup();
                ShowWheel(false);
                SetMode(Mode.Idle);
            });

            // Reset selection
            _selectedMedication = "";
            _selectedMedIndex = -1;
        }

        #endregion

        #region UI Event Handlers
        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear all medication history?",
                "Clear History",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _medicationHistory.Clear();
                SaveMedicationHistory();
                UpdateHistoryDisplay();
                Log("Medication history cleared");
            }
        }

        private void ExportHistory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string filename = $"medication_export_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string json = JsonSerializer.Serialize(_medicationHistory, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filename, json);
                Log($"History exported to {filename}");
                MessageBox.Show($"History exported to {filename}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Export failed: {ex.Message}");
                MessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion
    }
}