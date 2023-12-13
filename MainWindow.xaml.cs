﻿using Microsoft.Win32;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using TEAMS2HA.API;

namespace TEAMS2HA
{
    public class AppSettings
    {
        #region Public Properties

        public string EncryptedMqttPassword { get; set; }
        public string HomeAssistantToken { get; set; }
        public string HomeAssistantURL { get; set; }
        public string MqttAddress { get; set; }
        public string MqttUsername { get; set; }
        public bool RunAtWindowsBoot { get; set; }
        public bool RunMinimized { get; set; }
        public string TeamsToken { get; set; }
        public string Theme { get; set; }

        #endregion Public Properties
    }

    public partial class MainWindow : Window
    {
        #region Private Fields

        private MeetingUpdate _latestMeetingUpdate;

        private AppSettings _settings;

        private string _settingsFilePath;

        private string _teamsApiKey;

        private API.WebSocketClient _teamsClient;

        private string deviceid;

        private bool isDarkTheme = false;

        private MqttClientWrapper mqttClientWrapper;

        private System.Timers.Timer mqttKeepAliveTimer;

        private System.Timers.Timer mqttPublishTimer;

        private string Mqtttopic;

        private List<string> sensorNames = new List<string>
        {
            "IsMuted", "IsVideoOn", "IsHandRaised", "IsInMeeting", "IsRecordingOn", "IsBackgroundBlurred", "IsSharing", "HasUnreadMessages"
        };

        #endregion Private Fields

        #region Public Constructors

        public MainWindow()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataFolder = Path.Combine(localAppData, "TEAMS2HA");
            Directory.CreateDirectory(appDataFolder); // Ensure the directory exists
            _settingsFilePath = Path.Combine(appDataFolder, "settings.json");
            _settings = LoadSettings();
            deviceid = System.Environment.MachineName;
            this.InitializeComponent();
            //ApplyTheme(Properties.Settings.Default.Theme);
            var Mqtttopic = deviceid;
            this.Loaded += MainPage_Loaded;
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Square150x150Logo.scale-200.ico");
            MyNotifyIcon.Icon = new System.Drawing.Icon(iconPath);
            mqttClientWrapper = new MqttClientWrapper(
                "TEAMS2HA",
                _settings.MqttAddress,
                _settings.MqttUsername,
                _settings.EncryptedMqttPassword
            );
            InitializeConnections();
            mqttKeepAliveTimer = new System.Timers.Timer(60000); // Set interval to 60 seconds (60000 ms)
            mqttKeepAliveTimer.Elapsed += OnTimedEvent;
            mqttKeepAliveTimer.AutoReset = true;
            mqttKeepAliveTimer.Enabled = true;
            InitializeMqttPublishTimer();
            mqttClientWrapper.MessageReceived += async (e) =>
            {
                HandleIncomingCommand(this, e);
            };

        }

        #endregion Public Constructors

        #region Public Methods

        public async Task InitializeConnections()
        {
            // Other initialization code...
            await initializeteamsconnection();
            await InitializeMQTTConnection();
            // Other initialization code...
        }

        public async Task InitializeMQTTConnection()
        {
            if (mqttClientWrapper == null)
            {
                Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Client Not Initialized");
                return;
            }

            int retryCount = 0;
            const int maxRetries = 5;

            while (retryCount < maxRetries && !mqttClientWrapper.IsConnected)
            {
                try
                {
                    await mqttClientWrapper.ConnectAsync();
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                    await mqttClientWrapper.SubscribeAsync("homeassistant/switch/+/set", MqttQualityOfServiceLevel.AtLeastOnce);
                    return; // Exit the method if connected
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = $"MQTT Status: Disconnected (Retry {retryCount + 1})");
                    Debug.WriteLine($"Retry {retryCount + 1}: {ex.Message}");
                    retryCount++;
                    await Task.Delay(2000); // Wait for 2 seconds before retrying
                }
            }

            Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Disconnected (Failed to connect)");
        }

        #endregion Public Methods

        #region Protected Methods

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_teamsClient != null)
            {
                _teamsClient.TeamsUpdateReceived -= TeamsClient_TeamsUpdateReceived;
            }
            if (mqttClientWrapper != null)
            {
                mqttClientWrapper.Dispose();
            }
            MyNotifyIcon.Dispose();
            base.OnClosing(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Only hide the window and show the NotifyIcon when minimized
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                // Ensure the NotifyIcon is hidden when the window is not minimized
                MyNotifyIcon.Visibility = Visibility.Collapsed;
            }

            base.OnStateChanged(e);
        }

        #endregion Protected Methods

        #region Private Methods
        private void HandleIncomingCommand(object sender, MqttApplicationMessageReceivedEventArgs e)
        {
            string topic = e.ApplicationMessage.Topic;
            // Check if it's a command topic and handle accordingly
            if (topic.StartsWith("homeassistant/switch/") && topic.EndsWith("/set"))
            {
                string command = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                // Parse and handle the command
                HandleSwitchCommand(topic, command);
            }
        }
        private async void HandleSwitchCommand(string topic, string command)
        {
            // Determine which switch is being controlled based on the topic
            string switchName = topic.Split('/')[2]; // Assuming topic format is "homeassistant/switch/{switchName}/set"
            int underscoreIndex = switchName.IndexOf('_');
            if (underscoreIndex != -1 && underscoreIndex < switchName.Length - 1)
            {
                switchName = switchName.Substring(underscoreIndex + 1);
            }
            string jsonMessage = "";
            switch (switchName)
            {
                case "ismuted":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"toggle-mute\",\"action\":\"toggle-mute\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;
                case "isvideoon":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"toggle-video\",\"action\":\"toggle-video\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;
                case "isbackgroundblurred":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"background-blur\",\"action\":\"toggle-background-blur\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;
                case "ishandraised":
                    jsonMessage = $"{{\"apiVersion\":\"1.0.0\",\"service\":\"raise-hand\",\"action\":\"toggle-hand\",\"manufacturer\":\"Jimmy White\",\"device\":\"THFHA\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"requestId\":1}}";
                    break;

                    // Add other cases as needed
            }

            if (!string.IsNullOrEmpty(jsonMessage))
            {
                // Send the message to Teams
                await _teamsClient.SendMessageAsync(jsonMessage);
            }
        }

        private void ApplyTheme(string theme)
        {
            isDarkTheme = theme == "Dark";
            Uri themeUri;
            if (theme == "Dark")
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");
                isDarkTheme = true;
            }
            else
            {
                themeUri = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml");
                isDarkTheme = false;
            }

            // Update the theme
            var existingTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == themeUri);
            if (existingTheme == null)
            {
                existingTheme = new ResourceDictionary() { Source = themeUri };
                Application.Current.Resources.MergedDictionaries.Add(existingTheme);
            }

            // Remove the other theme
            var otherThemeUri = isDarkTheme
                ? new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Light.xaml")
                : new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesignTheme.Dark.xaml");

            var currentTheme = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source == otherThemeUri);
            if (currentTheme != null)
            {
                Application.Current.Resources.MergedDictionaries.Remove(currentTheme);
            }
        }

        private async void CheckMqttConnection()
        {
            if (mqttClientWrapper != null && !mqttClientWrapper.IsConnected)
            {
                try
                {
                    await mqttClientWrapper.ConnectAsync();
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Connected");
                }
                catch
                {
                    Dispatcher.Invoke(() => MQTTConnectionStatus.Text = "MQTT Status: Disconnected");
                }
            }
        }

        private string DetermineDeviceClass(string sensor)
        {
            switch (sensor)
            {
                case "IsMuted":
                case "IsVideoOn":
                case "IsHandRaised":
                case "IsBackgroundBlurred":
                    return "switch"; // These are ON/OFF switches
                case "IsInMeeting":
                case "HasUnreadMessages":
                case "IsRecordingOn":
                case "IsSharing":
                    return "sensor"; // These are true/false sensors
                default:
                    return null; // Or a default device class if appropriate
            }
        }

        // This method determines the appropriate icon based on the sensor and meeting state
        private string DetermineIcon(string sensor, MeetingState state)
        {
            return sensor switch
            {
                // If the sensor is "IsMuted", return "mdi:microphone-off" if state.IsMuted is true, otherwise return "mdi:microphone"
                "IsMuted" => state.IsMuted ? "mdi:microphone-off" : "mdi:microphone",

                // If the sensor is "IsVideoOn", return "mdi:camera" if state.IsVideoOn is true, otherwise return "mdi:camera-off"
                "IsVideoOn" => state.IsVideoOn ? "mdi:camera" : "mdi:camera-off",

                // If the sensor is "IsHandRaised", return "mdi:hand-back-left" if state.IsHandRaised is true, otherwise return "mdi:hand-back-left-off"
                "IsHandRaised" => state.IsHandRaised ? "mdi:hand-back-left" : "mdi:hand-back-left-off",

                // If the sensor is "IsInMeeting", return "mdi:account-group" if state.IsInMeeting is true, otherwise return "mdi:account-off"
                "IsInMeeting" => state.IsInMeeting ? "mdi:account-group" : "mdi:account-off",

                // If the sensor is "IsRecordingOn", return "mdi:record-rec" if state.IsRecordingOn is true, otherwise return "mdi:record"
                "IsRecordingOn" => state.IsRecordingOn ? "mdi:record-rec" : "mdi:record",

                // If the sensor is "IsBackgroundBlurred", return "mdi:blur" if state.IsBackgroundBlurred is true, otherwise return "mdi:blur-off"
                "IsBackgroundBlurred" => state.IsBackgroundBlurred ? "mdi:blur" : "mdi:blur-off",

                // If the sensor is "IsSharing", return "mdi:monitor-share" if state.IsSharing is true, otherwise return "mdi:monitor-off"
                "IsSharing" => state.IsSharing ? "mdi:monitor-share" : "mdi:monitor-off",

                // If the sensor is "HasUnreadMessages", return "mdi:message-alert" if state.HasUnreadMessages is true, otherwise return "mdi:message-outline"
                "HasUnreadMessages" => state.HasUnreadMessages ? "mdi:message-alert" : "mdi:message-outline",

                // If the sensor does not match any of the above cases, return "mdi:eye"
                _ => "mdi:eye"
            };
        }

        private string GetStateValue(string sensor, MeetingUpdate meetingUpdate)
        {
            switch (sensor)
            {
                case "IsMuted":
                case "IsVideoOn":
                case "IsBackgroundBlurred":
                case "IsHandRaised":
                    // Cast to bool and then check the value
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "ON" : "OFF";

                case "IsInMeeting":
                case "HasUnreadMessages":
                case "IsRecordingOn":
                case "IsSharing":
                    // Similar casting for these properties
                    return (bool)meetingUpdate.MeetingState.GetType().GetProperty(sensor).GetValue(meetingUpdate.MeetingState, null) ? "True" : "False";

                default:
                    return "unknown";
            }
        }

        private void InitializeMqttPublishTimer()
        {
            mqttPublishTimer = new System.Timers.Timer(60000); // Set the interval to 60 seconds
            mqttPublishTimer.Elapsed += OnMqttPublishTimerElapsed;
            mqttPublishTimer.AutoReset = true; // Reset the timer after it elapses
            mqttPublishTimer.Enabled = true; // Enable the timer
        }

        private async Task initializeteamsconnection()
        {
            if (_teamsClient != null && _teamsClient.IsConnected)
            {
                return; // Already connected, no need to reinitialize
            }
            string teamsToken = _settings.TeamsToken;
            if (string.IsNullOrEmpty(teamsToken))
            {
                // If the Teams token is not set, then we can't connect to Teams
                return;
            }
            // Initialize the Teams WebSocket connection
            //var uri = new Uri("ws://localhost:8124?protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");
            var state = new API.State();  // You would initialize this as necessary
            _teamsClient = new API.WebSocketClient(uri, state, _settingsFilePath, token => this.Dispatcher.Invoke(() => TeamsApiKeyBox.Text = token));
            _teamsClient.TeamsUpdateReceived += TeamsClient_TeamsUpdateReceived;
            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
        }

        private AppSettings LoadSettings()
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                return JsonConvert.DeserializeObject<AppSettings>(json);
            }
            else
            {
                return new AppSettings(); // Defaults if file does not exist
            }
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            //LoadSettings();

            RunAtWindowsBootCheckBox.IsChecked = _settings.RunAtWindowsBoot;
            RunMinimisedCheckBox.IsChecked = _settings.RunMinimized;
            MqttUserNameBox.Text = _settings.MqttUsername;
            MQTTPasswordBox.Text = _settings.EncryptedMqttPassword;
            MqttAddress.Text = _settings.MqttAddress;
            TeamsApiKeyBox.Text = _settings.TeamsToken;
            ApplyTheme(_settings.Theme);
            if (RunMinimisedCheckBox.IsChecked == true)
            {// Start the window minimized and hide it
                this.WindowState = WindowState.Minimized;
                this.Hide();
                MyNotifyIcon.Visibility = Visibility.Visible; // Show the NotifyIcon in the system tray
            }
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe when the page is unloaded
            _teamsClient.ConnectionStatusChanged -= TeamsConnectionStatusChanged;
        }

        private void MyNotifyIcon_Click(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                // Restore the window if it's minimized
                this.Show();
                this.WindowState = WindowState.Normal;
            }
            else
            {
                // Minimize the window if it's currently normal or maximized
                this.WindowState = WindowState.Minimized;
            }
        }

        private void OnMqttPublishTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                // Example: Publish a keep-alive message
                string keepAliveTopic = "TEAMS2HA/keepalive";
                string keepAliveMessage = "alive";
                _ = mqttClientWrapper.PublishAsync(keepAliveTopic, keepAliveMessage);
            }
        }

        private void OnTimedEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            // Check the MQTT connection
            CheckMqttConnection();

        }

        private async Task PublishConfigurations(MeetingUpdate meetingUpdate, AppSettings settings)
        {
            foreach (var sensor in sensorNames)
            {
                var device = new Device()
                {
                    Identifiers = deviceid,
                    Name = deviceid,
                    SwVersion = "1.0.0",
                    Model = "Teams2HA",
                    Manufacturer = "JimmyWhite",
                };

                string sensorName = $"{deviceid}_{sensor}".ToLower().Replace(" ", "_");
                string deviceClass = DetermineDeviceClass(sensor);
                string icon = DetermineIcon(sensor, meetingUpdate.MeetingState);
                string stateValue = GetStateValue(sensor, meetingUpdate);

                if (deviceClass == "switch")
                {
                    string stateTopic = $"homeassistant/switch/{sensorName}/state";
                    string commandTopic = $"homeassistant/switch/{sensorName}/set";
                    var switchConfig = new
                    {
                        name = sensorName,
                        unique_id = sensorName,
                        state_topic = stateTopic,
                        command_topic = commandTopic,
                        payload_on = "ON",
                        payload_off = "OFF",
                        icon = icon
                    };
                    string configTopic = $"homeassistant/switch/{sensorName}/config";
                    await mqttClientWrapper.PublishAsync(configTopic, JsonConvert.SerializeObject(switchConfig));
                    await mqttClientWrapper.PublishAsync(stateTopic, stateValue);
                }
                else if (deviceClass == "sensor") // Use else-if for binary_sensor
                {
                    string stateTopic = $"homeassistant/sensor/{sensorName}/state"; // Corrected state topic
                    var binarySensorConfig = new
                    {
                        name = sensorName,
                        unique_id = sensorName,
                        state_topic = stateTopic,

                        icon = icon,
                        Device = device,
                    };
                    string configTopic = $"homeassistant/sensor/{sensorName}/config";
                    await mqttClientWrapper.PublishAsync(configTopic, JsonConvert.SerializeObject(binarySensorConfig), true);
                    await mqttClientWrapper.PublishAsync(stateTopic, stateValue); // Publish the state
                }
            }
        }

        private async Task PublishDiscoveryMessages()
        {
            var muteSwitchConfig = new
            {
                name = "Teams Mute",
                unique_id = "TEAMS2HA_mute",
                state_topic = "TEAMS2HA/TEAMS/mute",
                command_topic = "TEAMS2HA/TEAMS/mute/set",
                payload_on = "true",
                payload_off = "false",
                device = new { identifiers = new[] { "TEAMS2HA" }, name = "Teams Integration", manufacturer = "Your Company" }
            };

            string muteConfigTopic = "homeassistant/switch/TEAMS2HA/mute/config";
            await mqttClientWrapper.PublishAsync(muteConfigTopic, JsonConvert.SerializeObject(muteSwitchConfig));

            // Repeat for other entities like video
        }



        private bool SaveSettings()
        {
            var oldMqttAddress = _settings.MqttAddress;
            var oldMqttUsername = _settings.MqttUsername;
            var oldMqttPassword = _settings.EncryptedMqttPassword;
            _settings.RunAtWindowsBoot = RunAtWindowsBootCheckBox.IsChecked ?? false;
            _settings.RunMinimized = RunMinimisedCheckBox.IsChecked ?? false;
            _settings.MqttAddress = MqttAddress.Text;
            _settings.MqttUsername = MqttUserNameBox.Text;
            _settings.EncryptedMqttPassword = MQTTPasswordBox.Text;
            _settings.EncryptedMqttPassword = MQTTPasswordBox.Text;

            _settings.TeamsToken = TeamsApiKeyBox.Text;
            _settings.Theme = isDarkTheme ? "Dark" : "Light";
          
            string json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            File.WriteAllText(_settingsFilePath, json);
            return oldMqttAddress != _settings.MqttAddress ||
         oldMqttUsername != _settings.MqttUsername ||
         oldMqttPassword != _settings.EncryptedMqttPassword;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            bool mqttSettingsChanged = SaveSettings();
            if (mqttSettingsChanged)
            {
                // Retry MQTT connection with new settings
                mqttClientWrapper = new MqttClientWrapper(
                    "TEAMS2HA",
                    _settings.MqttAddress,
                    _settings.MqttUsername,
                    _settings.EncryptedMqttPassword
                );
                InitializeMQTTConnection();
            }
        }

        private async Task SetStartupAsync(bool startWithWindows)
        {
            await Task.Run(() =>
            {
                const string appName = "TEAMS2HA"; // Your application's name
                string exePath = System.Windows.Forms.Application.ExecutablePath;

                // Open the registry key for the current user's startup programs
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (startWithWindows)
                    {
                        // Set the application to start with Windows startup by adding a registry value
                        key.SetValue(appName, exePath);
                    }
                    else
                    {
                        // Remove the registry value to prevent the application from starting with
                        // Windows startup
                        key.DeleteValue(appName, false);
                    }
                }
            });
        }

        private async void TeamsClient_TeamsUpdateReceived(object sender, WebSocketClient.TeamsUpdateEventArgs e)
        {
            if (mqttClientWrapper != null && mqttClientWrapper.IsConnected)
            {
                // Store the latest update
                _latestMeetingUpdate = e.MeetingUpdate;

                // Update sensor configurations
                await PublishConfigurations(_latestMeetingUpdate, _settings);

                // If you need to publish state messages, add that logic here as well
            }
        }

        private void TeamsConnectionStatusChanged(bool isConnected)
        {
            // The UI needs to be updated on the main thread.
            Dispatcher.Invoke(() =>
            {
                TeamsConnectionStatus.Text = isConnected ? "Teams: Connected" : "Teams: Disconnected";
            });
        }

        private async void TestMQTTConnection_Click(object sender, RoutedEventArgs e)
        {
            // Get the Homeassistant token from the HomeassistantTokenBox _homeassistantToken = HomeassistantTokenBox.Text;

            // If the token is empty or null, return and do nothing
        }

        private void TestTeamsConnection_Click(object sender, RoutedEventArgs e)
        {
            string teamsToken = _settings.TeamsToken; // Get the Teams token from the settings

            // Create a URI with the necessary parameters for the WebSocket connection
            var uri = new Uri($"ws://localhost:8124?token={teamsToken}&protocol-version=2.0.0&manufacturer=JimmyWhite&device=PC&app=THFHA&app-version=2.0.26");

            var state = new API.State();  // You would initialize this as necessary

            // Create a new WebSocketClient with the URI, state, and settings file path
            _teamsClient = new API.WebSocketClient(uri, state, _settingsFilePath, token => this.Dispatcher.Invoke(() => TeamsApiKeyBox.Text = token));

            // Subscribe to the ConnectionStatusChanged event of the WebSocketClient
            _teamsClient.ConnectionStatusChanged += TeamsConnectionStatusChanged;
        }

        private void ToggleThemeButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle the theme
            isDarkTheme = !isDarkTheme;
            _settings.Theme = isDarkTheme ? "Dark" : "Light";
            ApplyTheme(_settings.Theme);

            // Save settings after changing the theme
            SaveSettings();
        }

        #endregion Private Methods
    }
}