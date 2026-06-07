using UnityEngine;
using TMPro; 
using UnityEngine.UI;
using System;
using System.Net.Sockets;

public class AppManager : MonoBehaviour
{
    public static AppManager Instance { get; private set; } 

    [Header("UI References")]
    public TMP_Dropdown protocolDropdown; 
    public TMP_InputField ipInputField;
    public TMP_InputField portInputField;
    public TMP_Dropdown handDropdown;     
    public GameObject menuPanel;          
    
    [Header("Network Status References")]
    public TextMeshProUGUI networkStatusText; 
    public Button btnStart;                

    [Header("Build Info")]
    public TextMeshProUGUI versionText;   

    [Header("Visual Settings")]
    public Toggle visualizationToggle; 
    public bool ShowLandmarks => visualizationToggle != null && visualizationToggle.isOn;

    [Header("Debug and Experimental")]
    public Toggle debugInfoToggle;
    public bool ShowDebugInfo => debugInfoToggle != null && debugInfoToggle.isOn;

    public Toggle videoStreamToggle;
    public bool ShowVideoStream => videoStreamToggle != null && videoStreamToggle.isOn;

    public Toggle headPoseToggle;
    public bool TrackHeadPose => headPoseToggle != null && headPoseToggle.isOn;

    [Header("Interaction Settings")]
    public GameObject[] rayInteractors;

    [Header("Hand Visuals")]
    public GameObject syntheticHandLeft;
    public GameObject syntheticHandRight;

    [Header("Logging Settings")]
    public string targetLogSource = "Left"; 

    [Header("Status")]
    public bool isStreaming = false;
    private string _connectionErrorMessage = ""; 
    private Color _statusColor = Color.green; // Persistent color cache

    public string ServerIP { get; private set; }
    public int ServerPort { get; private set; }
    public int SelectedProtocol { get; private set; } 
    public int SelectedHandMode { get; private set; } 

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        else Instance = this;
    }

    private void Start()
    {
        // Automatically pulls from Project Settings > Player > Version
        string version = Application.version; 
        
        if (versionText != null) 
        {
            versionText.text = $"v{version}";
        }

        if (protocolDropdown != null)
        {
            protocolDropdown.onValueChanged.AddListener(OnProtocolChanged);
            // OnProtocolChanged(protocolDropdown.value);
        }

        ipInputField.onValueChanged.AddListener(delegate { ClearError(); });
        portInputField.onValueChanged.AddListener(delegate { ClearError(); });
        // Load saved config (if any)
        LoadConfig();
        ApplyVideoCanvasVisibility();
    }

    private void SaveConfig()
    {
        // Save the current UI values to disk
        PlayerPrefs.SetInt("SavedProtocol", protocolDropdown.value);
        PlayerPrefs.SetString("SavedIP", ipInputField.text);
        PlayerPrefs.SetString("SavedPort", portInputField.text);
        PlayerPrefs.SetInt("SavedHandMode", handDropdown.value);
        
        // Force write to disk immediately
        PlayerPrefs.Save(); 
        Debug.Log("[AppManager] Config Saved.");
    }

    private void LoadConfig()
    {
        // 1. Load Protocol (Default to 2: TCP Wireless if not found)
        if (PlayerPrefs.HasKey("SavedProtocol"))
        {
            int savedProto = PlayerPrefs.GetInt("SavedProtocol");
            // This triggers OnProtocolChanged, which sets default IPs
            protocolDropdown.value = savedProto; 
        }

        // 2. Load IP (Overwrite the default set by the dropdown)
        if (PlayerPrefs.HasKey("SavedIP"))
        {
            ipInputField.text = PlayerPrefs.GetString("SavedIP");
        }

        // 3. Load Port
        if (PlayerPrefs.HasKey("SavedPort"))
        {
            portInputField.text = PlayerPrefs.GetString("SavedPort");
        }

        // 4. Load Hand Mode
        if (PlayerPrefs.HasKey("SavedHandMode"))
        {
            handDropdown.value = PlayerPrefs.GetInt("SavedHandMode");
        }
    }

    private void Update()
    {
        // If streaming, check if the user clicks the Menu Button on the Right controller
        if (isStreaming && OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.RTouch))
        {
            StopStreaming(); 
        }

        if (!isStreaming)
        {
            ValidateNetwork();
        }
    }
    
    private void OnDestroy()
    {
        if (protocolDropdown != null)
        {
            protocolDropdown.onValueChanged.RemoveListener(OnProtocolChanged);
        }
    }

private void OnProtocolChanged(int index)
    {
        ClearError();
        if (index == 0) // UDP
        {
            if (ipInputField != null) ipInputField.text = "255.255.255.255";
            if (portInputField != null) portInputField.text = "9000";
            UpdateStatusUI("UDP Ready", Color.green, true);
        }
        else if (index == 1) // TCP (Wired / ADB)
        {
            if (ipInputField != null) ipInputField.text = "127.0.0.1";
            if (portInputField != null) portInputField.text = "8000"; 
            StartCoroutine(QuickTCPCheck());
        }
        else if (index == 2) // TCP (Wireless) - NEW
        {
            // Set a placeholder or the last known IP. 
            if (ipInputField != null) ipInputField.text = "192.168.1.1"; // Placeholder for the PC's Wi-Fi IP
            if (portInputField != null) portInputField.text = "8000"; 
            StartCoroutine(QuickTCPCheck());
        }
    }

    public void ClearError()
    {
        _connectionErrorMessage = "";
        _statusColor = Color.green;
    }

    private void ValidateNetwork()
    {
        if (Application.internetReachability == NetworkReachability.NotReachable)
        {
            UpdateStatusUI("Error: No Active Network Connection", Color.red, false);
            return;
        }

        if (!string.IsNullOrEmpty(_connectionErrorMessage))
        {
            // Uses the persistent color (Red or Yellow) set by the connection logic
            UpdateStatusUI(_connectionErrorMessage, _statusColor, true);
            return;
        }

        UpdateStatusUI("System Ready", Color.green, true);
    }

    private void UpdateStatusUI(string message, Color color, bool canStart)
    {
        _statusColor = color; // Cache the color for the Update loop
        if (networkStatusText != null)
        {
            networkStatusText.text = message;
            networkStatusText.color = color;
        }

        if (btnStart != null)
        {
            btnStart.interactable = canStart;
            var breather = btnStart.GetComponent<UIButtonBreather>();
            if (breather != null) breather.enabled = canStart;
        }
    }

    public async void OnStartStreaming()
    {
        ClearError();
        ServerIP = ipInputField.text;
        
        int parsedPort;
        if (!int.TryParse(portInputField.text, out parsedPort))
        {
            UpdateStatusUI("Error: Port number is invalid", Color.red, false);
            return;
        }
        ServerPort = parsedPort;

        SelectedProtocol = protocolDropdown.value;
        SelectedHandMode = handDropdown.value;

        // --- UPDATED TCP CHECK BLOCK ---
        if (SelectedProtocol == 1 || SelectedProtocol == 2) // TCP wireless and wired
        {
            try 
            {
                // Optional: visual feedback that we are trying
                UpdateStatusUI($"Connecting to {ServerIP}...", Color.yellow, false);

                using (TcpClient testClient = new TcpClient())
                {
                    IAsyncResult result = testClient.BeginConnect(ServerIP, ServerPort, null, null);
                    
                    // Wait for 1 second max
                    bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    
                    if (!success)
                    {
                        // Clean up manually if we timed out
                        try { testClient.Close(); } catch {}
                        throw new Exception("Connection Timed Out");
                    }

                    // CRITICAL: EndConnect throws the specific SocketException (Refused/Unreachable)
                    testClient.EndConnect(result);
                }
            }
            catch (Exception ex)
            {
                // 1. Get the real error message (e.g. "No connection could be made...", "Timed Out")
                string realError = ex.Message;
                
                // 2. Format a shorter message for the small UI text
                _connectionErrorMessage = $"TCP Connection Error: {realError}";
                
                // 3. LOG EVERYTHING to the HUD/Console
                // This will show up on your shared screen log
                string detailedLog = $"TCP Connection Target: {ServerIP}:{ServerPort}\nException: {realError}";
                SendLog(detailedLog);
                Debug.LogError(detailedLog); // Also print to ADB Logcat

                UpdateStatusUI(_connectionErrorMessage, Color.red, true);
                return; 
            }
        }
        // -------------------------------

        UpdateStatusUI("Streaming Active", Color.green, true);

        // Save the current config for next time
        SaveConfig();
        
        string protocolName = protocolDropdown.options[SelectedProtocol].text;
        string handName = handDropdown.options[SelectedHandMode].text;
        string statusMsg = $"Stream started! \nIP: {ServerIP} \nPort: {ServerPort} \nProtocol: {protocolName} \nHands: {handName}";
        SendLog(statusMsg);
        
        if(menuPanel != null) menuPanel.SetActive(false);

        ToggleRays(false);
        UpdateHandVisuals(SelectedHandMode);
        isStreaming = true;

        // Optional host->Quest video plane (separate from telemetry transport)
        if (ShowVideoStream)
        {
            if (string.Equals(ServerIP, "255.255.255.255", StringComparison.Ordinal))
            {
                HandleDisconnection("Video requires a concrete host IP. Switch protocol to TCP/Wireless or enter host LAN IP.");
                return;
            }

            VideoStreamManager manager = FindFirstObjectByType<VideoStreamManager>();
            if (manager == null)
            {
                HandleDisconnection("Video manager missing from scene.");
                return;
            }

            bool ok;
            try
            {
                ok = await manager.StartVideoSession(
                    ServerIP,
                    8765,
                    "720p30",
                    false
                );
            }
            catch (Exception ex)
            {
                ok = false;
                Debug.LogError($"[Video] Start failed: {ex.Message}");
            }

            if (!ok)
            {
                HandleDisconnection("Video session failed to start.");
                return;
            }
        }
        else
        {
            ApplyVideoCanvasVisibility();
        }
    }

    // Called by the Streamer when the socket dies
    public void HandleDisconnection(string errorMsg)
    {
        // Prevent spamming if multiple hands fail at once
        if (!isStreaming) return;

        Debug.LogError($"[Network] TCP Disconnect Triggered: {errorMsg}");
        
        // 1. Reset Logic
        isStreaming = false;
        
        // 2. Re-enable UI
        if (menuPanel != null) 
        {
            menuPanel.SetActive(true);
            
            // Optional: Recenter menu in front of user so they see the error
            MenuRecenter recenter = FindFirstObjectByType<MenuRecenter>();
            if (recenter != null) recenter.Recenter();
        }
        ToggleRays(true);

        // 3. Show Error Status
        _connectionErrorMessage = $"TCP Disconnected: {errorMsg}"; // persistent
        UpdateStatusUI(_connectionErrorMessage, Color.yellow, true);
        SendLog($"Connection dropped: {errorMsg}");
        StopVideoSessionAsync("disconnection");
        ApplyVideoCanvasVisibility();
    }

    public void StopStreaming()
    {
        isStreaming = false;
        ClearError();
        StopVideoSessionAsync("user_stop");
        ApplyVideoCanvasVisibility();

        // Re-enable UI and Rays for interaction
        if (menuPanel != null) 
        {
            menuPanel.SetActive(true);
            MenuRecenter recenterScript = FindFirstObjectByType<MenuRecenter>();
            if (recenterScript != null) recenterScript.Recenter();
        }
        ToggleRays(true);

        SendLog("Streaming stopped by user.");
    }

    private async void StopVideoSessionAsync(string reason)
    {
        VideoStreamManager manager = FindFirstObjectByType<VideoStreamManager>();
        if (manager != null)
        {
            try
            {
                await manager.StopVideoSession(reason);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Video] Stop failed: {ex.Message}");
            }
        }
    }

    private void UpdateHandVisuals(int mode)
    {
        bool showLeft = (mode == 0 || mode == 1);
        bool showRight = (mode == 0 || mode == 2);

        if (syntheticHandLeft != null) syntheticHandLeft.SetActive(showLeft);
        if (syntheticHandRight != null) syntheticHandRight.SetActive(showRight);
    }

    private void ToggleRays(bool state)
    {
        if (rayInteractors == null) return;
        foreach (var ray in rayInteractors)
        {
            if (ray != null) ray.SetActive(state);
        }
    }

    private void SendLog(string message)
    {
        if (LogManager.Instance != null)
            LogManager.Instance.Log(targetLogSource, message);
        else
            Debug.Log($"[{targetLogSource}] {message}");
    }

    private void ApplyVideoCanvasVisibility()
    {
        VideoStreamManager manager = FindFirstObjectByType<VideoStreamManager>();
        if (manager == null)
        {
            return;
        }
        manager.SetVideoUiVisible(isStreaming && ShowVideoStream);
    }

    private System.Collections.IEnumerator QuickTCPCheck()
    {
        UpdateStatusUI("Checking TCP Connection...", Color.yellow, false);
        
        string targetIP = ipInputField.text;
        int targetPort;
        if (!int.TryParse(portInputField.text, out targetPort)) yield break;

        bool success = false;
        
        var task = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    var result = client.BeginConnect(targetIP, targetPort, null, null);
                    if (result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        client.EndConnect(result);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        });

        while (!task.IsCompleted) yield return null;
        success = task.Result;

        if (!success)
        {
            _connectionErrorMessage = "TCP Error: Connection refused. Is host server running?";
            // Persistent Yellow for the passive background check
            UpdateStatusUI(_connectionErrorMessage, Color.yellow, true);
        }
    }
}
