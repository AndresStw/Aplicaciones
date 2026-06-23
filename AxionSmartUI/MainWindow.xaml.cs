using AudioSwitcher.AudioApi.CoreAudio;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Drawing;
 

////////////////////////////falta optimizar con AI a ver que sugiere.///////////////////////////////////////
namespace AxionSmartUI
{
    public partial class MainWindow : Window
    {
        // AUDIO 
        private readonly CoreAudioController audioController = new();

        // TIMERS 
        private readonly DispatcherTimer timer = new();
        private readonly DispatcherTimer volumeDebounceTimer = new();
        private readonly DispatcherTimer brightnessDebounceTimer = new();

        //  SYSTEM METRICS 
        private PerformanceCounter? cpuCounter;
        private PerformanceCounter? ramCounter;
        
        // Cache para WMI Briiiiillo 
        private ManagementObject? brightnessObject;

        // CONFIG & STATE 
        private SmartConfig config = new();
        private string configFileName = "AxionSettings.json";
        private string configPath;
        private string currentMode = "";
        private bool isApplyingProfile = false;
        private bool suppressProfileEvent = false;
        private string lastAppliedSmartMode = "";
        private System.Windows.Forms.NotifyIcon trayIcon;
        private string pendingSmartMode = "";
        private int debounceCounter = 0;
        private const int DebounceLimit = 6;
        private bool isDiscordRunning = false;
        private bool isCustomAppRunning = false;
        private bool isBlueLightFilterActive = false; // Para el futuro filtro de luz azul

        //WIN32 API 
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowRect(IntPtr hwnd, out RECT rc);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        //  HELPERS 
        private string GetActiveWindowTitle()
        {
            IntPtr hwnd = GetForegroundWindow();
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            if (GetWindowText(hwnd, sb, 256) > 0)
            {
                return sb.ToString();
            }
            return "";
        }

        private void ShowExistingWindow()
        {
            this.WindowState = WindowState.Normal;
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
            SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }


        public MainWindow()
        {

            configPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AxionSmartUI",
        configFileName
    );
            InitializeComponent();

            Dispatcher.BeginInvoke(new Action(() => {
                InitializeAppAsync();
            }), DispatcherPriority.Background);

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "Axion Smart UI"
            };
            trayIcon.DoubleClick += TrayIcon_DoubleClick;
        }

        private void InitializeAppAsync()
        {
            LoadConfig();
            
            Task.Run(() => InitCounters());

            ApplyConfigToUI();
            RefreshProfiles();

            RefreshCustomAppsList(); // Cargar la lista de apps personalizadas , esta tiene error
            volumeDebounceTimer.Interval = TimeSpan.FromMilliseconds(150);
            volumeDebounceTimer.Tick += (s, e) =>
            {
                volumeDebounceTimer.Stop();
                audioController.DefaultPlaybackDevice.Volume = VolumeSlider.Value;
            };

            brightnessDebounceTimer.Interval = TimeSpan.FromMilliseconds(150);
            brightnessDebounceTimer.Tick += (s, e) =>
            {
                brightnessDebounceTimer.Stop();
                SetBrightness((int)BrightnessSlider.Value);
            };

            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += Timer_Tick;
            timer.Start();

            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            BrightnessSlider.ValueChanged += BrightnessSlider_ValueChanged;
        }

        private void InitCounters()
        {
            try 
            {
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
                
                // Iniciar objeto de brillo una sola vez
                var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject obj in searcher.Get()) {
                    brightnessObject = obj;
                    break;
                }
            } catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveConfig();
            base.OnClosing(e);
        }

        private bool IsGamingFullscreen()
        {
            if (!IsForegroundFullScreen()) return false;

            //filtrar navegadores
            string title = GetActiveWindowTitle();

            // Lista de navegadores que conosco, falta agregar +
            string[] browserKeywords = { "Google Chrome", "Mozilla Firefox", "Microsoft Edge", "Opera", "Brave", "Vivaldi" };

            
            foreach (string keyword in browserKeywords)
            {
                if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true; // Es pantalla completa y no es un navegador
        }

        // TIMER & DASHBOARD
        private void Timer_Tick(object? sender, EventArgs e)
        {
            var allProcesses = Process.GetProcesses();
            
            isDiscordRunning = allProcesses.Any(p => p.ProcessName.Equals("Discord", StringComparison.OrdinalIgnoreCase));
            
            //si alguna app de la lista negra está en ejecución
            isCustomAppRunning = config.CustomApps.Any(appName => 
                allProcesses.Any(p => p.ProcessName.Equals(appName, StringComparison.OrdinalIgnoreCase)));

            // opcional

            UpdateDashboard();
            ApplySmartMode();
        }

        private void UpdateDashboard()
        {
            if (cpuCounter != null && ramCounter != null)
            {
                CpuText.Text = $"{(int)cpuCounter.NextValue()}%";
                RamText.Text = $"{(int)ramCounter.NextValue()}%";
                // FpsText.Text = "N/A" por ahora
            }
        }

        //NAVIGATION 
        private void Dashboard_Click(object sender, RoutedEventArgs e)
        {
            DashboardPanel.Visibility = Visibility.Visible;
            ProfilesPanel.Visibility = Visibility.Collapsed;
            AutomationPanel.Visibility = Visibility.Collapsed;
        }

        private void Profiles_Click(object sender, RoutedEventArgs e)
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            ProfilesPanel.Visibility = Visibility.Visible;
            AutomationPanel.Visibility = Visibility.Collapsed;
        }

        private void Automation_Click(object sender, RoutedEventArgs e)
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            ProfilesPanel.Visibility = Visibility.Collapsed;
            AutomationPanel.Visibility = Visibility.Visible;
        }

        private void RefreshCustomAppsList()
        {
            CustomAppsListBox.Items.Clear();
            foreach (var app in config.CustomApps)
            {
                CustomAppsListBox.Items.Add(app);
            }
        }


        // PROFILE SYSTEM 
        private void RefreshProfiles()
        {
            var selected = ProfileSelector.SelectedItem?.ToString();
            ProfileSelector.Items.Clear();

            foreach (var p in config.Profiles)
                ProfileSelector.Items.Add(p.Name);

            if (selected != null && config.Profiles.Any(p => p.Name == selected))
                ProfileSelector.SelectedItem = selected;
        }

        private void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressProfileEvent || ProfileSelector.SelectedItem == null) return;

            string name = ProfileSelector.SelectedItem.ToString() ?? "";
            var profile = config.Profiles.FirstOrDefault(p => p.Name == name);

            if (profile != null) ApplyProfile(profile);
        }

        private void ApplyProfile(Profile profile)
        {
            suppressProfileEvent = true;
            VolumeSlider.Value = profile.Volume;
            BrightnessSlider.Value = profile.Brightness;
            currentMode = profile.Name;
            ModeText.Text = profile.Name;

            audioController.DefaultPlaybackDevice.Volume = profile.Volume;
            SetBrightness(profile.Brightness);
            suppressProfileEvent = false;
        }

        //CRUD PERFILES
        private void AddProfile_Click(object sender, RoutedEventArgs e)
        {
            config.Profiles.Add(new Profile { Name = $"Profile {config.Profiles.Count + 1}", Volume = 50, Brightness = 50 });
            RefreshProfiles();
            SaveConfig();
        }

        private void EditProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileSelector.SelectedItem == null) return;
            var profile = config.Profiles.FirstOrDefault(p => p.Name == ProfileSelector.SelectedItem.ToString());
            if (profile != null)
            {
                profile.Volume = VolumeSlider.Value;
                profile.Brightness = (byte)BrightnessSlider.Value;
                SaveConfig();
                System.Windows.MessageBox.Show($"Perfil '{profile.Name}' actualizado con los valores actuales.", "Perfil Guardado");
            }
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileSelector.SelectedItem == null) return;
            var profile = config.Profiles.FirstOrDefault(p => p.Name == ProfileSelector.SelectedItem.ToString());
            if (profile != null)
            {
                config.Profiles.Remove(profile);
                RefreshProfiles();
                SaveConfig();
            }
        }

        private void RenameProfile_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileSelector.SelectedItem == null) return;
            string oldName = ProfileSelector.SelectedItem.ToString() ?? "";
            var profile = config.Profiles.FirstOrDefault(p => p.Name == oldName);
            if (profile == null) return;

            string newName = Microsoft.VisualBasic.Interaction.InputBox("Nuevo nombre del perfil:", "Renombrar", oldName);
            if (string.IsNullOrWhiteSpace(newName)) return;

            profile.Name = newName;
            RefreshProfiles();
            SaveConfig();
            ProfileSelector.SelectedItem = newName;
        }

        //CRUD CUSTOM APPS
        private void AddCustomApp_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Seleccionar aplicación personalizada"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string appName = System.IO.Path.GetFileNameWithoutExtension(openFileDialog.FileName);
                if (!config.CustomApps.Contains(appName, StringComparer.OrdinalIgnoreCase))
                {
                    config.CustomApps.Add(appName);
                    RefreshCustomAppsList();
                    SaveConfig();
                    System.Windows.MessageBox.Show($"Aplicación '{appName}' agregada a la lista.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show($"La aplicación '{appName}' ya está en la lista.", "Advertencia", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void RemoveCustomApp_Click(object sender, RoutedEventArgs e)
        {
            if (CustomAppsListBox.SelectedItem is string selectedApp)
            {
                config.CustomApps.Remove(selectedApp);
                RefreshCustomAppsList();
                SaveConfig();
            }
        }

        //SMART MODE LOGIC
        private bool IsForegroundFullScreen()
        {
            IntPtr desktopHandle = GetDesktopWindow();
            IntPtr shellHandle = GetShellWindow();
            IntPtr hWnd = GetForegroundWindow();

            if (hWnd.Equals(IntPtr.Zero) || hWnd.Equals(desktopHandle) || hWnd.Equals(shellHandle)) return false;

            GetWindowRect(hWnd, out RECT appBounds);
            int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            int screenHeight = (int)SystemParameters.PrimaryScreenHeight;

            return (appBounds.right - appBounds.left) == screenWidth &&
                   (appBounds.bottom - appBounds.top) == screenHeight;
        }
         //falta mejorar esta parte 
        private bool IsNightTime()
        {
            int hour = DateTime.Now.Hour;
            return hour >= 20 || hour < 6;
        }

        private void ApplySmartMode()
        {
            if (!config.AutoMode) return;

            string targetMode = "Normal";
            double targetVol = config.NormalVolume;
            byte targetBright = config.NormalBrightness;

            bool isFullscreenChecked = FullscreenModeCheck.IsChecked == true;
            bool isNightChecked = NightModeCheck.IsChecked == true;
            bool isDiscordChecked = DiscordModeCheck.IsChecked == true; // Agregamos la variable
            bool isBlueLightFilterChecked = BlueLightFilterCheck.IsChecked == true; // Nuevo check para filtro de luz azul

            //LÓGICA CON PRIORIDAD
            if (isFullscreenChecked && (IsGamingFullscreen() || isCustomAppRunning))
            {
                targetMode = "Gaming / Fullscreen";
                targetVol = config.FullscreenVolume;
                targetBright = config.FullscreenBrightness;
            }
            else if (isDiscordChecked && isDiscordRunning)
            {
                targetMode = "Discord Mode";//podria agregar otra app, aunque no uso discord mucho
                targetVol = 60; 
                targetBright = 70;
            }
            else if (isNightChecked && IsNightTime())
            {
                targetMode = "Night Mode";
                targetVol = config.NightVolume;
                targetBright = config.NightBrightness;
                //filtro de luz azul ,para implementar, ya que lo uso mucho tengo que agregar el tema de horario y compatibilidad con la app de autoDarkMode que uso 
                // isBlueLightFilterActive = true; 
            }
            else if (isBlueLightFilterChecked) 
            {
                //activar solo el filtro de luz azul sin cambiar volumen/brillo
            }

            // SISTEMA DE TOLERANCIA
            //aun no le hago cambios peroooo deberia ser mas flexible
            if (targetMode != lastAppliedSmartMode)
            {
                if (pendingSmartMode == targetMode)
                {
                    debounceCounter++;
                    if (debounceCounter >= DebounceLimit)
                    {
                        ActivateMode(targetMode, targetVol, targetBright);
                        lastAppliedSmartMode = targetMode;
                        debounceCounter = 0;
                    }
                }
                else
                {
                    pendingSmartMode = targetMode;
                    debounceCounter = 1;
                }
            }
            else
            {
                debounceCounter = 0;
                pendingSmartMode = "";
            }
        }

        private void ActivateMode(string name, double volume, double brightness)
        {
            if (isApplyingProfile) return;
            isApplyingProfile = true;

            ModeText.Text = name;
            VolumeSlider.Value = volume;
            BrightnessSlider.Value = brightness;

            audioController.DefaultPlaybackDevice.Volume = volume;
            SetBrightness((int)brightness);

            // Lógica para activar o desactivar filtro de luz azul
            // if (isBlueLightFilterActive) { Activar filtro }
            // else { Desactivar filtro } 


            isApplyingProfile = false;
        }

        //SLIDERS VOLUMEN, BRILLO 
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isApplyingProfile || suppressProfileEvent) return;

           
            if (lastAppliedSmartMode == "Normal") config.NormalVolume = VolumeSlider.Value;
            volumeDebounceTimer.Stop();
            volumeDebounceTimer.Start();
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isApplyingProfile || suppressProfileEvent) return;

            if (lastAppliedSmartMode == "Normal") config.NormalBrightness = (byte)BrightnessSlider.Value;
            brightnessDebounceTimer.Stop();
            brightnessDebounceTimer.Start();
        }

        private void SetBrightness(int brightness)
        {
            if (brightnessObject == null) return;
            try
            {
                brightnessObject.InvokeMethod("WmiSetBrightness", new object[] { UInt32.MaxValue, brightness });
            }
            catch { }
        }

        //SAVE, LOAD, STARTUP 

        private void LoadConfig()
        {
            // Aseguramos que la carpeta exista actualmente estoy usando %appdata% por temas de si elimino la carpeta no perder la confi
            string directory = System.IO.Path.GetDirectoryName(configPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    config = JsonSerializer.Deserialize<SmartConfig>(json) ?? new SmartConfig();
                    
                    // Si no hay perfiles en el primer uso creamos los básicos falta agregar maas o dejarlo asi ,
                    if (config.Profiles.Count == 0)
                    {
                        config.Profiles.Add(new Profile { Name = "Gaming", Volume = 90, Brightness = 100 });
                        config.Profiles.Add(new Profile { Name = "Night", Volume = 20, Brightness = 25 });
                        config.Profiles.Add(new Profile { Name = "Work", Volume = 50, Brightness = 70 });
                        SaveConfig();
                    }
                }
                catch { config = new SmartConfig(); }
            }
            else
            {
                config = new SmartConfig();
                SaveConfig();
            }
        }

        private void ApplyConfigToUI()
        {
            VolumeSlider.Value = config.NormalVolume;
            BrightnessSlider.Value = config.NormalBrightness;
            StartupCheck.IsChecked = config.StartWithWindows;
            TrayCheck.IsChecked = config.MinimizeToTray;

            FullscreenModeCheck.IsChecked = config.EnableFullscreenMode;
            NightModeCheck.IsChecked = config.EnableNightMode;
            DiscordModeCheck.IsChecked = config.EnableDiscordMode;
            // BlueLightFilterCheck.IsChecked = config.EnableBlueLightFilter; // Cargar estado del filtro
        }

        private void SaveConfig()
        {
           
            config.StartWithWindows = StartupCheck.IsChecked == true;
            config.MinimizeToTray = TrayCheck.IsChecked == true;
            config.EnableFullscreenMode = FullscreenModeCheck.IsChecked == true;
            config.EnableNightMode = NightModeCheck.IsChecked == true;
            config.EnableDiscordMode = DiscordModeCheck.IsChecked == true;
            // config.EnableBlueLightFilter = BlueLightFilterCheck.IsChecked == true; // Guardar estado del filtro

            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void SetStartup(bool enable)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                if (enable) key.SetValue("AxionSmartUI", Environment.ProcessPath ?? "");
                else key.DeleteValue("AxionSmartUI", false);
            }
            catch { }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Guardar configuración",
                FileName = "config.json"//AxionSmartConfig.json? o SmartConfig.json
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                configPath = saveFileDialog.FileName;
                SaveConfig();
                SetStartup(config.StartWithWindows);
                System.Windows.MessageBox.Show("Configuración guardada exitosamente.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        //SYSTEM SHUTDOWN
        private void ScheduleShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(ShutdownMinutesInput.Text, out int minutes) && minutes > 0)
            {
                try
                {
                    Process.Start("shutdown", $"-s -t {minutes * 60}");// por seguridad prefiero mostrar mensaje, note que al momentod e establecer el tiempo tiene un leve retraso , optiomizar con AI , sin dañar la estructura
                    System.Windows.MessageBox.Show($"El equipo se apagará en {minutes} minutos.", "Apagado Programado", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (Exception ex) { System.Windows.MessageBox.Show($"Error: {ex.Message}"); }
            }
            else
            {
                System.Windows.MessageBox.Show("Ingresa un número válido de minutos.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelShutdown_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start("shutdown", "-a");
                ShutdownMinutesInput.Text = string.Empty;
                System.Windows.MessageBox.Show("Apagado automático cancelado.", "Cancelado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }
        //SYSTEM TRAY LOGIC 
        protected override void OnStateChanged(EventArgs e)
        {
            // minimizar en bandeja 
            if (WindowState == WindowState.Minimized && config.MinimizeToTray)
            {
                this.Hide();
            }
            base.OnStateChanged(e);
        }

        private void TrayIcon_DoubleClick(object? sender, EventArgs e)
        {
            //doble clic en el ícono al lado del reloj, restauramos la app, cambiar
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        protected override void OnClosed(EventArgs e)
        {
            //destruir el ícono al cerrar para que no se quede fantasma en la barra
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            base.OnClosed(e);
            //falta revisar matar el proceso bien, y verificar el consumo , actualemente es minimo , pero lo puedo mejorar
        }
    }

}