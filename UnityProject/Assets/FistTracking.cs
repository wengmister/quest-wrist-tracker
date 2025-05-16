using UnityEngine;
using Oculus.Interaction.Input;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System;

public class LeftHandFistDetector : MonoBehaviour
{
    [SerializeField]
    private bool _logToHUD = true;
    
    [SerializeField]
    private float _logFrequency = 0.01f; // Log every 0.01 seconds
    
    [SerializeField]
    [Tooltip("Threshold angle (in degrees) below which a finger is considered bent")]
    private float _bentFingerThreshold = 40f;
    
    [SerializeField]
    [Tooltip("How many fingers need to be bent to consider the hand closed")]
    private int _closedFistThreshold = 4; // All fingers bent = closed fist
    
    [SerializeField]
    [Tooltip("The log source name that matches your left hand LogDisplay's logSource")]
    private string _logSourceName = "Left"; // Make sure this matches your left LogDisplay's logSource
    
    [Header("UDP Settings")]
    [SerializeField]
    private bool _broadcastUDP = true;
    
    [SerializeField]
    private string _remoteIP = "255.255.255.255"; // Broadcast address
    
    [SerializeField]
    private int _remotePort = 9000; // Port to send data to
    
    private IHand _hand;
    private float _timer = 0f;
    private string _currentState = "Unknown";
    private UdpClient _udpClient;
    private IPEndPoint _remoteEndPoint;
    
    private void Start()
    {
        _hand = GetComponent<IHand>();
        if (_hand == null)
        {
            LogHUD("LeftHandFistDetector requires a component that implements IHand on the same GameObject");
            enabled = false;
            return;
        }
        
        // Verify this is tracking the left hand
        if (_hand.Handedness != Handedness.Left)
        {
            LogHUD("This FistDetector is configured for Left hand only");
            enabled = false;
            return;
        }

        // Initialize UDP client
        if (_broadcastUDP)
        {
            try
            {
                _udpClient = new UdpClient();
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(_remoteIP), _remotePort);
                LogHUD("UDP broadcasting initialized");
            }
            catch (Exception ex)
            {
                LogHUD("UDP initialization error: " + ex.Message);
                _broadcastUDP = false;
            }
        }

        // Subscribe to hand updates
        _hand.WhenHandUpdated += OnHandUpdated;
        
        // Initial log to show the detector is active
        LogHUD("Fist Detector Active");
    }
    
    private void OnDestroy()
    {
        if (_hand != null)
        {
            _hand.WhenHandUpdated -= OnHandUpdated;
        }
        
        // Clean up UDP client
        if (_udpClient != null)
        {
            _udpClient.Close();
            _udpClient = null;
        }
    }
    
    private void OnHandUpdated()
    {
        _timer += Time.deltaTime;
        if (_timer >= _logFrequency)
        {
            _timer = 0f;
            DetectHandState();
        }
    }
    
    private void DetectHandState()
    {
        if (!_hand.IsTrackedDataValid)
        {
            LogHUD("State: Not Tracked");
            _currentState = "Not Tracked";
            return;
        }
        
        if (!_hand.GetJointPosesLocal(out ReadOnlyHandJointPoses jointPoses))
        {
            LogHUD("State: Failed to get joint poses");
            _currentState = "Error";
            return;
        }
        
        // Count bent fingers
        int bentFingerCount = 0;
        
        // Check Index finger
        if (IsFingerBent(jointPoses, HandFinger.Index))
            bentFingerCount++;
            
        // Check Middle finger
        if (IsFingerBent(jointPoses, HandFinger.Middle))
            bentFingerCount++;
            
        // Check Ring finger
        if (IsFingerBent(jointPoses, HandFinger.Ring))
            bentFingerCount++;
            
        // Check Pinky finger
        if (IsFingerBent(jointPoses, HandFinger.Pinky))
            bentFingerCount++;
            
        // Check Thumb (special case)
        if (IsThumbBent(jointPoses))
            bentFingerCount++;
        
        // Determine hand state based on bent finger count
        string state = bentFingerCount >= _closedFistThreshold ? "CLOSED" : "OPEN";
        
        // Only log if state has changed
        if (state != _currentState)
        {
            _currentState = state;
            LogHUD($"Fist State: {_currentState} (Bent fingers: {bentFingerCount}/5)");
            
            // Send UDP message
            if (_broadcastUDP && _udpClient != null)
            {
                SendUdpMessage($"Fist: {_currentState.ToLower()}");
            }
        }
    }
    
    private bool IsFingerBent(ReadOnlyHandJointPoses jointPoses, HandFinger finger)
    {
        int[] indices = GetFingerJointIndices(finger);
        if (indices.Length < 5)
            return false;
        
        // Compute MCP → PIP rotation
        Quaternion mcpToPip = Quaternion.Inverse(jointPoses[indices[1]].rotation) * jointPoses[indices[2]].rotation;
        Vector3 mcpEuler = mcpToPip.eulerAngles;
        float mcpFlexion = NormalizeAngle(mcpEuler.x);
        
        // Consider finger bent if MCP flexion exceeds threshold
        return mcpFlexion > _bentFingerThreshold;
    }
    
    private bool IsThumbBent(ReadOnlyHandJointPoses jointPoses)
    {
        int[] indices = GetFingerJointIndices(HandFinger.Thumb);
        if (indices.Length < 4)
            return false;
        
        // Transition: CMC → MCP
        Quaternion cmcToMcp = Quaternion.Inverse(jointPoses[indices[0]].rotation) * jointPoses[indices[1]].rotation;
        Vector3 cmcToMcpEuler = cmcToMcp.eulerAngles;
        float cmcMcpFlexion = NormalizeAngle(cmcToMcpEuler.x);
        
        // Consider thumb bent if CMC flexion exceeds threshold
        return cmcMcpFlexion > _bentFingerThreshold;
    }
    
    /// <summary>
    /// Normalizes an angle (in degrees) to the range [-180, 180].
    /// </summary>
    private float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
        {
            angle -= 360f;
        }
        return angle;
    }
    
    /// <summary>
    /// Returns joint indices for the given finger based on the standard Oculus hand skeleton.
    /// For the thumb, we assume a 4-joint chain.
    /// For non-thumb fingers, we assume a 5-joint chain.
    /// </summary>
    private int[] GetFingerJointIndices(HandFinger finger)
    {
        switch (finger)
        {
            case HandFinger.Thumb:
                return new int[] { 1, 2, 3, 4 };
            case HandFinger.Index:
                return new int[] { 5, 6, 7, 8, 9 };
            case HandFinger.Middle:
                return new int[] { 10, 11, 12, 13, 14 };
            case HandFinger.Ring:
                return new int[] { 15, 16, 17, 18, 19 };
            case HandFinger.Pinky:
                return new int[] { 20, 21, 22, 23, 24 };
            default:
                return new int[0];
        }
    }
    
    /// <summary>
    /// Sends the provided message over UDP.
    /// </summary>
    private void SendUdpMessage(string message)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            _udpClient.Send(data, data.Length, _remoteEndPoint);
        }
        catch (Exception ex)
        {
            LogHUD("UDP Send Error: " + ex.Message);
            // Disable UDP after error to prevent spamming errors
            _broadcastUDP = false;
        }
    }
    
    // Helper method to log messages to the HUD with the correct source
    private void LogHUD(string message)
    {
        if (_logToHUD)
        {
            LogManager.Instance.Log(_logSourceName, message);
        }
    }
}